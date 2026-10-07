using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.Logging;

/// <summary>
/// Daily-rolling file logger (logs\anyportproxy-yyyyMMdd.log, 14 days kept). Logging never blocks the
/// proxy: lines go into a bounded queue that a background task writes in batches. If the queue is full
/// or the disk misbehaves, lines are dropped and counted instead of slowing anything down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 14;
    private const long QuietAboveBytes = 256L * 1024 * 1024; // past this size, only warnings/errors are kept for the day

    private readonly string _dir;
    private readonly Channel<(LogLevel Level, string Line)> _queue =
        Channel.CreateBounded<(LogLevel, string)>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → we count the drop
            SingleReader = true,
        });
    private readonly Task _writer;
    private long _dropped;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(LogLevel level, string line)
    {
        if (!_queue.Writer.TryWrite((level, line))) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Writes a line synchronously (used for crash reports, when the process may be about to die).</summary>
    public void WriteNow(string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(PathFor(DateOnly.FromDateTime(DateTime.Now)), line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private string PathFor(DateOnly day) => Path.Combine(_dir, $"anyportproxy-{day:yyyyMMdd}.log");

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateOnly day = default;
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                try
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (writer is null || today != day)
                    {
                        writer?.Dispose();
                        Directory.CreateDirectory(_dir);
                        var fs = new FileStream(PathFor(today), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                        writer = new StreamWriter(fs, new UTF8Encoding(false));
                        day = today;
                        Prune();
                    }

                    bool quiet = writer.BaseStream.Length > QuietAboveBytes;
                    long dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                        writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} WARN Logger: {dropped} log lines were skipped because the proxy was very busy");

                    int batch = 0;
                    while (batch < 5000 && reader.TryRead(out var item))
                    {
                        if (!quiet || item.Level >= LogLevel.Warning) writer.WriteLine(item.Line);
                        batch++;
                    }
                    writer.Flush();
                }
                catch (Exception)
                {
                    // Disk full / file locked: drop this batch, back off, keep going.
                    try { writer?.Dispose(); } catch { }
                    writer = null;
                    while (reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
                    await Task.Delay(2000);
                }
            }
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "anyportproxy-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetainDays))
                {
                    try { File.Delete(f); } catch (IOException) { }
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(3));
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category[(category.LastIndexOf('.') + 1)..];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRCE",
                LogLevel.Debug => "DBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "FAIL",
                _ => "CRIT",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            _provider.Enqueue(logLevel, line);
        }
    }
}
