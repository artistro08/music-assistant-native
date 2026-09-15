using Microsoft.Extensions.Logging;

namespace MusicAssistant.Remote;

/// <summary>
/// Temporary: routes SIPSorcery's own logging into %LOCALAPPDATA%\MusicAssistant\sipsorcery.log
/// so the ICE/DTLS connection lifecycle can be inspected. Enabled only when the
/// MA_RTC_LOG environment variable is set.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>When MA_RTC_LOG is set, empties sipsorcery.log and points SIPSorcery's logging at it; otherwise does nothing.</summary>
    public static void EnableIfRequested()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MA_RTC_LOG"))) return;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicAssistant", "sipsorcery.log");
        try
        {
            File.WriteAllText(path, "");
        }
        catch (IOException)
        {
        }
        SIPSorcery.LogFactory.Set(new FileLoggerFactory(path));
    }

    private sealed class FileLoggerFactory(string path) : ILoggerFactory
    {
        private static readonly object Gate = new();

        public ILogger CreateLogger(string categoryName) => new FileLogger(path, categoryName);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class FileLogger(string path, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {category.Split('.')[^1]}: {formatter(state, ex)}{(ex is null ? "" : $" {ex.Message}")}";
                lock (Gate)
                {
                    try
                    {
                        File.AppendAllText(path, $"{line}{Environment.NewLine}");
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }
}
