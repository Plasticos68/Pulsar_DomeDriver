using Pulsar_DomeDriver.Config;
using System;
using System.IO;
using static Pulsar_DomeDriver.Config.ConfigManager;

namespace Pulsar_DomeDriver.Diagnostics
{
    public class FileLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly bool _debugLog;
        private readonly bool _traceLog;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new();
        private volatile bool _disposed = false;
        private readonly object _disposeLock = new();

        public FileLogger(string logFilePath, bool debugEnabled = false, bool traceEnabled = false)
        {
            _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
            _debugLog = debugEnabled;
            _traceLog = traceEnabled;

            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var fileStream = new FileStream(
                _logFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite
            );

            _writer = new StreamWriter(fileStream)
            {
                AutoFlush = true
            };

            Log("Logger initialized.", LogLevel.Info);
        }

        public void Log(string message, LogLevel level = LogLevel.None)
        {
            if (_disposed) return;
            if (!_traceLog && !_debugLog) return;

            string label = level switch
            {
                LogLevel.Error => "[ERROR]",
                LogLevel.Warning => "[WARNING]",
                LogLevel.Info => "[INFO]",
                LogLevel.Debug => "[DEBUG]",
                LogLevel.Trace => "[TRACE]",
                _ => "[LOG]"
            };

            label = label.PadRight(12);
            WriteLine(label, message, level);
        }

        private void WriteLine(string levelLabel, string message, LogLevel level = LogLevel.None)
        {
            if (_disposed) return;

            string cleanedMessage = message.Replace("\r", "").Replace("\t", " - ").TrimEnd();
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");

            if (level == LogLevel.Trace && !_traceLog) return;
            if (level == LogLevel.Debug && !_debugLog) return;

            lock (_writeLock)
            {
                if (_disposed) return;
                _writer.WriteLine($"[{timestamp}] \t{levelLabel} {cleanedMessage}");
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    lock (_writeLock)
                    {
                        _writer?.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] \t[INFO]     Logger disposed.");
                        _writer?.Dispose();
                    }
                }
                catch
                {
                    // Swallow any disposal-time exceptions silently
                }
            }
        }
    }
}


//byte[] buffer = Encoding.ASCII.GetBytes(response);
//string hex = BitConverter.ToString(buffer);
//_logger.Log($"Raw response bytes: {hex}");
