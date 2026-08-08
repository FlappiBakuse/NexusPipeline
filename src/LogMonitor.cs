using System.Text;

namespace NexusPipeline;

internal class LogMonitor : IDisposable
{
    private readonly string _path;

    private bool _readFromStart;

    private FileStream? _stream;

    private long _position;

    private bool _reopenScheduled;

    public LogMonitor(string path, bool readFromStart = false)
    {
        _path = path;
        _readFromStart = readFromStart;
        Open();
    }

    public string Path => _path;

    /// <summary>打开时记录的文件创建时间（Ticks），用于检测同路径文件被删除重建（追加写不改变创建时间，不误判）。</summary>
    public long FileStamp { get; private set; }

    public DateTime LastWrite { get; private set; } = DateTime.Now;

    /// <summary>重新打开并从文件头读取（文件被重建/截断后使用）。</summary>
    public void ReopenFromStart()
    {
        _readFromStart = true;
        Open();
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
                _position = 0;
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
            _position = _readFromStart || _stream.Length == 0 ? 0 : _stream.Length;
            try
            {
                FileStamp = File.GetCreationTimeUtc(_path).Ticks;
            }
            catch (Exception)
            {
                FileStamp = 0;
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public static string? ResolveFile(string logPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return null;
            }
            if (File.Exists(logPath))
            {
                return logPath;
            }
            if (Directory.Exists(logPath))
            {
                string? newest = Directory.GetFiles(logPath)
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
                return newest;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
