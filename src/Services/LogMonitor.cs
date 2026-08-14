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

    private bool _reopenScheduled;

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
        if (_stream is null || !_fileIdValid)
        {
            return false;
        }
        (uint vol, uint hi, uint lo, bool ok) = QueryFileId(_stream.SafeFileHandle);
        if (!ok)
        {
            return false;
        }
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            (uint pVol, uint pHi, uint pLo, bool pOk) = QueryFileId(probe.SafeFileHandle);
            if (!pOk)
            {
                try
                {
                    return File.GetCreationTimeUtc(path).Ticks != FileStamp;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return pVol != vol || pHi != hi || pLo != lo;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string ReadNew()
    {
        if (_stream is null)
        {
            Open();
            if (_stream is null)
            {
                return "";
            }
        }
        if (_reopenScheduled)
        {
            Open();
            _reopenScheduled = false;
            if (_stream is null)
            {
                return "";
            }
        }
        try
        {
            if (_stream.Length < _position)
            {
                // v0.6.9+（P8）：部分截断（缩短但未归零，如脚本循环 > 重定向）时从新文件尾续读——此前归零从头读
                // 会把截断点之前的已读旧行重新输出（判定输入重复污染）；长度归零（Length=0）时仍从头读（契约不变）。
                _position = Math.Max(0, _stream.Length);
            }
            _stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string content = reader.ReadToEnd();
            _position = _stream.Position;
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

    private void Open()
    {
        _stream?.Dispose();
        _stream = null;
        try
        {
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 读取起点：从头读 / 显式起点（v0.6.5+：尝试开始时长度，只读本次尝试新增内容）/ 打开时文件尾。
            // 显式起点超过当前长度（文件被截断/重建）时由 ReadNew 的长度检查归零从头读。
            if (_readFromStart || _stream.Length == 0)
            {
                _position = 0;
            }
            else
            {
                _position = _initialPosition >= 0 ? Math.Min(_initialPosition, _stream.Length) : _stream.Length;
            }
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
        }
        catch (Exception)
        {
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
    }
}
