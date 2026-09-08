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

    // 保存上一次已观察到的完整文件内容，用于在同一文件发生截断并重新增长时定位真实边界。
    // 日志输入本身在判断脚本侧已有容量限制；这里的 checkpoint 只服务当前 Attempt 的增量读取协议。
    private byte[] _checkpoint = Array.Empty<byte>();

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
            byte[] current = ReadCurrentBytes();
            ReadOnlySpan<byte> previous = _checkpointReady ? _checkpoint : ReadOnlySpan<byte>.Empty;
            if (_checkpointReady && current.AsSpan().SequenceEqual(previous))
            {
                return "";
            }

            int commonPrefix = CommonPrefixLength(previous, current);
            int start = !_checkpointReady
                ? 0
                : commonPrefix == previous.Length && current.Length >= previous.Length
                    ? checked((int)Math.Min(_position, current.Length))
                    : commonPrefix;
            string content = DecodeUtf8(current, start);
            _position = current.Length;
            _lastCommittedOffset = _position;
            _checkpoint = current;
            _checkpointReady = true;
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
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                    _checkpoint = Array.Empty<byte>();
                    _checkpointReady = false;
                }
                else
                {
                    _checkpoint = ReadCurrentBytes();
                    _checkpointReady = true;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private byte[] ReadCurrentBytes()
    {
        if (_stream is null)
        {
            return Array.Empty<byte>();
        }
        long originalPosition = _stream.Position;
        try
        {
            _stream.Seek(0, SeekOrigin.Begin);
            using var snapshot = new MemoryStream();
            _stream.CopyTo(snapshot);
            return snapshot.ToArray();
        }
        finally
        {
            _stream.Seek(Math.Min(originalPosition, _stream.Length), SeekOrigin.Begin);
        }
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        int length = Math.Min(previous.Length, current.Length);
        int index = 0;
        while (index < length && previous[index] == current[index])
        {
            index++;
        }
        return index;
    }

    private static string DecodeUtf8(byte[] bytes, int start)
    {
        if (start >= bytes.Length)
        {
            return "";
        }
        using var stream = new MemoryStream(bytes, start, bytes.Length - start, writable: false, publiclyVisible: true);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: start == 0,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
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
        _checkpoint = Array.Empty<byte>();
        _checkpointReady = false;
    }
}

internal sealed record LogCandidateSnapshot(long Length, string Identity);
