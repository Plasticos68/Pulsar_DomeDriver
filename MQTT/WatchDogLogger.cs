using System;
using System.IO;
using System.Text;

namespace Pulsar_DomeDriver.Diagnostics
{
    public static class WatchdogLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDirectory = @"D:\OneDrive\Documents\Logs";
        private static readonly string _logFile = Path.Combine(_logDirectory, $"WatchdogLog_{DateTime.Now:yyyyMMdd}.txt");

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(_logDirectory))
                        Directory.CreateDirectory(_logDirectory);

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string entry = $"[{timestamp}] {message}{Environment.NewLine}";

                    File.AppendAllText(_logFile, entry, Encoding.UTF8);
                }
            }
            catch
            {
                // Silent fail — avoid crashing watchdog if logging fails
            }
        }
    }
}


