using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Diagnostics;
using System;
using System.IO;
using System.IO.Pipes;

namespace Pulsar_DomeDriver.Goodnight
{
    public enum GNSType
    {
        Message,
        New,
        Alarm,
        Stop,
        Cease
    }

    public class GNS : IDisposable

    {
        private readonly FileLogger _logger;
        private readonly ConfigManager _config;
        private readonly object _disposeLock = new();
        private volatile bool _disposed = false;

        public GNS(FileLogger logger, ConfigManager config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
        }

        public void SendGNS(GNSType type, string message = "", int? timeoutSeconds = null)
        {
            if (!_config.UseGNS) { return; }

            string formatted = FormatMessage(type, message, timeoutSeconds);

            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", "GNSBridge", PipeDirection.Out))
                {
                    pipeClient.Connect(1000); // 1-second timeout

                    using (var writer = new StreamWriter(pipeClient))
                    {
                        writer.AutoFlush = true;
                        writer.Write(formatted);
                    }
                }

                SafeLog($"GNS dispatched: '{message}' (type: {type}, formatted: {formatted})", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SafeLog($"Failed to send GNS message via pipe: {ex.Message}", LogLevel.Error);
            }
        }

        private string FormatMessage(GNSType type, string message, int? timeoutSeconds)
        {
            string action = type.ToString().ToLowerInvariant();
            string safeMessage = message?.Replace("|", "¦").Trim().TrimEnd('.') ?? "";

            return type switch
            {
                GNSType.Message => $"{action}|{safeMessage}",
                GNSType.New => $"{action}|{safeMessage}|{timeoutSeconds ?? 0}",
                GNSType.Alarm => $"{action}|{safeMessage}|0",
                GNSType.Stop => $"{action}|{safeMessage}|-1",
                GNSType.Cease => $"{action}|{safeMessage}|-2",
                _ => $"{action}|{safeMessage}"
            };
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;

                // No owned resources to dispose — just mark as disposed
                SafeLog("GNS disposed.", LogLevel.Info);
            }
        }



        private void SafeLog(string message, LogLevel level)
        {
            if (_disposed) return;
            try { _logger?.Log(message, level); }
            catch { /* suppress logging errors during disposal */ }
        }
    }
}
