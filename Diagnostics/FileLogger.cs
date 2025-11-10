using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.MQTT;
using System;
using System.IO;
using static Pulsar_DomeDriver.Config.ConfigManager;

namespace Pulsar_DomeDriver.Diagnostics
{
    public class FileLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly LogLevel _logLevel;
        private readonly bool _debugLog;
        private readonly bool _traceLog;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new();
        //private readonly MqttPublisher _mqttPublisher;

        public FileLogger(
            string logFilePath,
            LogLevel logLevel,
            bool debugEnabled = false,
            bool traceEnabled = false,
            MqttPublisher mqttPublisher = null)
        {
            _logFilePath = logFilePath;
            _logLevel = logLevel;
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

        public void Log(string message, LogLevel level = LogLevel.Debug)
        {
            if (level > _logLevel) return;

            if (level == LogLevel.Debug && !_debugLog) return;
            if (level == LogLevel.Trace && !_traceLog) return;

            string label = level switch
            {
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARNING",
                LogLevel.Info => "Info",
                LogLevel.Debug => "DEBUG",
                LogLevel.Trace => "TRACE",
                _ => "LOG"
            };

            WriteLine(label, message);
        }

        private void WriteLine(string levelLabel, string message)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;

            lock (_writeLock)
            {
                _writer.WriteLine($"[{timestamp}] \t{levelLabel}\t[Thread:{threadId}] {message}");
            }
        }

        public void Dispose()
        {
            Log("Logger disposed.", LogLevel.Info);
            _writer?.Dispose();
        }
    }
}


//byte[] buffer = Encoding.ASCII.GetBytes(response);
//string hex = BitConverter.ToString(buffer);
//_logger.Log($"Raw response bytes: {hex}");
