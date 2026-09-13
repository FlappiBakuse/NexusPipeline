using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Services;

internal class LogMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;

        public long CreationTime;

        public long LastAccessTime;

        public long LastWriteTime;

        public uint VolumeSerialNumber;

        public uint FileSizeHigh;

        public uint FileSizeLow;

        public uint NumberOfLinks;

        public uint FileIndexHigh;

        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    private readonly string _path;

    private bool _readFromStart;

    private long _initialPosition = -1;

    private FileStream? _stream;

    private long _position;

    private long _lastCommittedOffset;

    private bool _reopenScheduled;

    // 只保留最近窗口，避免日志文件越大，Attempt 的常驻内存和每轮复制就越大。
    // 窗口数组按监视器实例一次分配，ReadNew 不再为整文件创建快照。
    internal const int CheckpointCapacityBytes = 4 * 1024 * 1024;

    private const int ReadBufferBytes = 64 * 1024;

    private readonly byte[] _checkpoint = new byte[CheckpointCapacityBytes];

    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];

    private int _checkpointLength;

    private long _checkpointStartOffset;

    private long _checkpointEndOffset;

    private long _checkpointLastWriteTicks;

    private bool _checkpointReady;

    private uint _volSerial;

    private uint _fileIndexHigh;

    private uint _fileIndexLow;

    private bool _fileIdValid;

    public LogMonitor(string path, bool readFromStart = false, long initialPosition = -1)
    {
        _path = path;
        _readFromStart = readFromStart;
        _initialPosition = initialPosition;
        Open();
    }

    public string Path => _path;

    /// <summary>打开时记录的文件创建时间（Ticks），作为 FileId 不可用时的替换检测回退。</summary>
    public long FileStamp { get; private set; }

    public DateTime LastWrite { get; private set; } = DateTime.Now;

    /// <summary>诊断用 checkpoint 当前占用字节数；不参与日志读取协议。</summary>
    internal int CheckpointBytes => _checkpointLength;

    internal static LogCandidateSnapshot? CaptureSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            (uint vol, uint hi, uint lo, bool ok) = QueryFileId(stream.SafeFileHandle);
            string identity = ok
                ? $"fileid:{vol:x8}:{hi:x8}:{lo:x8}"
                : $"stamp:{File.GetCreationTimeUtc(path).Ticks:x16}";
            return new LogCandidateSnapshot(stream.Length, identity);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按 Attempt 开始快照决定候选文件读取起点：原文件续读，新建/替换文件从头读。</summary>
    internal static (bool ReadFromStart, long InitialPosition) DecideStart(
        LogCandidateSnapshot? beforeAttempt,
        LogCandidateSnapshot? current)
    {
        if (beforeAttempt is null || current is null || !string.Equals(beforeAttempt.Identity, current.Identity, StringComparison.Ordinal))
        {
            return (true, 0);
        }
        return (false, Math.Min(beforeAttempt.Length, current.Length));
    }

    /// <summary>重新打开并从文件头读取（文件被重建/截断后使用）。</summary>
    public void ReopenFromStart()
    {
        _readFromStart = true;
        Open();
    }

    /// <summary>
    /// 检测同路径文件是否已被替换（move 归档后重建/删除重建）：对比当前打开句柄与路径当前文件的
    /// 卷序列号+文件索引（FileId）。FileId 不可用时回退创建时间对比；路径文件不存在/打不开时不判定
    /// （保留旧句柄，待新文件出现后下轮检测）。追加写不改变 FileId，不会误判。
    /// </summary>
    public bool FileReplaced(string path)
    {
        if (_stream is null)
        {
            return false;
        }
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            (uint pVol, uint pHi, uint pLo, bool pOk) = QueryFileId(probe.SafeFileHandle);
            if (_fileIdValid)
            {
                (uint vol, uint hi, uint lo, bool ok) = QueryFileId(_stream.SafeFileHandle);
                if (ok && pOk)
                {
                    return pVol != vol || pHi != hi || pLo != lo;
                }
            }
            // 句柄 FileId 不可用或读取失败时，仍使用创建时间回退；不能因为能力探测失败而永久跳过替换检测。
            return File.GetCreationTimeUtc(path).Ticks != FileStamp;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string ReadNew()
    {
        if (_reopenScheduled)
        {
            Open(resumeCommittedOffset: true);
            _reopenScheduled = false;
        }
        if (_stream is null)
        {
            Open();
            if (_stream is null)
            {
                return "";
            }
        }
        try
        {
            long currentLength = _stream.Length;
            long start;
            if (!_checkpointReady)
            {
                start = Math.Min(Math.Max(0, _position), currentLength);
            }
            else
            {
                long? divergence = FindCheckpointDivergence(currentLength);
                if (divergence is long changedAt)
                {
                    // 窗口内发生截断/重写：从第一个可证明变化的字节重新交给判定层。
                    start = changedAt;
                }
                else if (currentLength > _checkpointEndOffset)
                {
                    // 保留窗口仍连续时，只读取旧文件尾之后的新增区间。
                    // 若窗口外发生深度重写，旧区间没有可证明边界，保守地丢弃它，
                    // 但仍交付文件长度增长后真正位于旧 EOF 之后的内容。
                    start = Math.Max(_position, _checkpointEndOffset);
                }
                else if (currentLength == _checkpointEndOffset
                    && _position < currentLength
                    && !CheckpointMetadataChanged())
                {
                    // Attempt 初始位置可能落在已有文件尾之前；首次读取仍需交付该段。
                    start = _position;
                }
                else
                {
                    // 文件缩短但保留窗口前缀仍一致，或同长度发生窗口外未知重写：
                    // 不注入无法证明属于本 Attempt 的旧内容，将新长度作为可信基线。
                    start = currentLength;
                }
            }

            string content = ReadRange(start, currentLength);
            _position = currentLength;
            _lastCommittedOffset = _position;
            RefreshCheckpoint(currentLength);
            if (content.Length > 0)
            {
                LastWrite = DateTime.Now;
            }
            return content;
        }
        catch (Exception)
        {
            _reopenScheduled = true;
            return "";
        }
    }

    private void Open(bool resumeCommittedOffset = false)
    {
        uint previousVol = _volSerial;
        uint previousHi = _fileIndexHigh;
        uint previousLo = _fileIndexLow;
        bool previousFileIdValid = _fileIdValid;
        long previousStamp = FileStamp;
        bool hadPreviousStream = _stream is not null;
        _stream?.Dispose();
        _stream = null;
        try
        {
            // 日志由其他进程持续写入；FileStream 的默认用户态缓冲可能在同一实例内保留
            // 已经读取过的旧窗口，导致截断/同长度重写在下一轮比较时不可见。
            // 使用最小缓冲，让每次 Seek/Read 都以文件当前内容为准，checkpoint 自身仍按固定窗口复用。
            _stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.RandomAccess);
            try
            {
                FileStamp = File.GetCreationTimeUtc(_path).Ticks;
            }
            catch (Exception)
            {
                FileStamp = 0;
            }
            (uint vol, uint hi, uint lo, bool ok) = QueryFileId(_stream.SafeFileHandle);
            _volSerial = vol;
            _fileIndexHigh = hi;
            _fileIndexLow = lo;
            _fileIdValid = ok;

            bool replacementDuringReopen = resumeCommittedOffset
                && hadPreviousStream
                && (previousFileIdValid && ok
                    ? previousVol != vol || previousHi != hi || previousLo != lo
                    : previousStamp != FileStamp);
            // 读取起点：从头读 / 显式起点（尝试开始时长度）/ 打开时文件尾；瞬时重开仅续读同一文件，
            // 若重开期间确认文件身份已变化，则按替换文件从头处理，避免重复或漏读。
            if (resumeCommittedOffset && !replacementDuringReopen)
            {
                _position = Math.Min(_lastCommittedOffset, _stream.Length);
            }
            else if (_readFromStart || _stream.Length == 0 || replacementDuringReopen)
            {
                _position = 0;
            }
            else
            {
                _position = _initialPosition >= 0 ? Math.Min(_initialPosition, _stream.Length) : _stream.Length;
            }
            if (!resumeCommittedOffset || replacementDuringReopen)
            {
                _lastCommittedOffset = _position;
                if (_readFromStart || replacementDuringReopen)
                {
                    ClearCheckpoint();
                }
                else
                {
                    RefreshCheckpoint(_stream.Length);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void ClearCheckpoint()
    {
        _checkpointLength = 0;
        _checkpointStartOffset = 0;
        _checkpointEndOffset = 0;
        _checkpointLastWriteTicks = 0;
        _checkpointReady = false;
    }

    private long? FindCheckpointDivergence(long currentLength)
    {
        if (_stream is null || currentLength < _checkpointStartOffset || _checkpointLength == 0)
        {
            return null;
        }

        long overlap = Math.Min(_checkpointLength, currentLength - _checkpointStartOffset);
        if (overlap <= 0)
        {
            return null;
        }

        long originalPosition = _stream.Position;
        try
        {
            _stream.Seek(_checkpointStartOffset, SeekOrigin.Begin);
            long compared = 0;
            while (compared < overlap)
            {
                int requested = (int)Math.Min(_readBuffer.Length, overlap - compared);
                int read = _stream.Read(_readBuffer, 0, requested);
                if (read <= 0)
                {
                    return null;
                }
                for (int index = 0; index < read; index++)
                {
                    if (_readBuffer[index] != _checkpoint[compared + index])
                    {
                        return _checkpointStartOffset + compared + index;
                    }
                }
                compared += read;
            }
            return null;
        }
        finally
        {
            _stream.Seek(Math.Min(originalPosition, _stream.Length), SeekOrigin.Begin);
        }
    }

    private bool CheckpointMetadataChanged()
    {
        if (_stream is null)
        {
            return true;
        }
        try
        {
            return File.GetLastWriteTimeUtc(_path).Ticks != _checkpointLastWriteTicks;
        }
        catch
        {
            return true;
        }
    }

    private string ReadRange(long start, long end)
    {
        if (_stream is null || end <= start)
        {
            return "";
        }
        start = Math.Max(0, start);
        end = Math.Max(start, end);
        long originalPosition = _stream.Position;
        using var output = new MemoryStream();
        try
        {
            _stream.Seek(start, SeekOrigin.Begin);
            long remaining = end - start;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(_readBuffer.Length, remaining);
                int read = _stream.Read(_readBuffer, 0, requested);
                if (read <= 0)
                {
                    break;
                }
                output.Write(_readBuffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            _stream.Seek(Math.Min(originalPosition, _stream.Length), SeekOrigin.Begin);
        }

        if (output.Length == 0)
        {
            return "";
        }
        if (!output.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
        {
            return Encoding.UTF8.GetString(output.ToArray());
        }
        using var stream = new MemoryStream(
            segment.Array,
            segment.Offset,
            checked((int)output.Length),
            writable: false,
            publiclyVisible: true);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: start == 0,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private void RefreshCheckpoint(long fileLength)
    {
        if (_stream is null)
        {
            ClearCheckpoint();
            return;
        }
        fileLength = Math.Max(0, fileLength);
        int expected = (int)Math.Min(fileLength, CheckpointCapacityBytes);
        long start = fileLength - expected;
        long originalPosition = _stream.Position;
        int readTotal = 0;
        try
        {
            _stream.Seek(start, SeekOrigin.Begin);
            while (readTotal < expected)
            {
                int read = _stream.Read(_checkpoint, readTotal, expected - readTotal);
                if (read <= 0)
                {
                    break;
                }
                readTotal += read;
            }
        }
        finally
        {
            _stream.Seek(Math.Min(originalPosition, _stream.Length), SeekOrigin.Begin);
        }
        _checkpointLength = readTotal;
        _checkpointStartOffset = start;
        _checkpointEndOffset = start + readTotal;
        _checkpointLastWriteTicks = GetLastWriteTicks();
        _checkpointReady = readTotal == expected;
    }

    private long GetLastWriteTicks()
    {
        try
        {
            return File.GetLastWriteTimeUtc(_path).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static (uint Vol, uint Hi, uint Lo, bool Ok) QueryFileId(SafeFileHandle handle)
    {
        try
        {
            if (GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, true);
            }
        }
        catch (Exception)
        {
        }
        return (0, 0, 0, false);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        ClearCheckpoint();
    }
}

internal sealed record LogCandidateSnapshot(long Length, string Identity);
