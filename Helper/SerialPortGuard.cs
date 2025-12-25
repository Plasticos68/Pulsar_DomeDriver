using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Diagnostics;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar_DomeDriver.Helper
{
    public class SerialPortGuard : IDisposable
    {
        private readonly SerialPort _port;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly FileLogger _logger;
        private readonly Action _setBusy;
        private readonly Action _clearBusy;
        private volatile bool _isDisposing = false;
        private readonly object _disposeLock = new();
        private volatile bool _disposed = false;

        public SerialPortGuard(SerialPort port, FileLogger logger, Action setBusy, Action clearBusy)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _logger = logger;
            _setBusy = setBusy;
            _clearBusy = clearBusy;
        }

        public bool IsReady
        {
            get
            {
                lock (_disposeLock)
                {
                    return !_isDisposing && _port?.IsOpen == true;
                }
            }
        }

        public string? Send(string command, bool expectResponse = true)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SerialPortGuard), "Send() called after disposal.");

            if (string.IsNullOrWhiteSpace(command))
            {
                SafeLog("Send called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            SafeLog($"Waiting for serial gate to send: {command}", LogLevel.Debug);
            _gate.Wait();
            SafeLog($"Serial gate acquired for: {command}", LogLevel.Trace);

            try
            {
                lock (_disposeLock)
                {
                    if (_isDisposing)
                        throw new ObjectDisposedException(nameof(SerialPortGuard));

                    _setBusy?.Invoke();

                    if (_port == null || !_port.IsOpen)
                        throw new ASCOM.NotConnectedException("Serial port is not open.");

                    // 🧠 Flush loop with readback logging
                    for (int i = 0; i < 3; i++)
                    {
                        _port.DiscardInBuffer();
                        Thread.Sleep(50);

                        if (_port.BytesToRead > 0)
                        {
                            string leftover = _port.ReadExisting();
                            if (!string.IsNullOrWhiteSpace(leftover))
                                SafeLog($"[FlushAttempt {i + 1}] Residual data before sending '{command}': {leftover}", LogLevel.Warning);
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Final settle delay to catch in-flight USB data
                    Thread.Sleep(100);

                    _port.DiscardOutBuffer();
                    Thread.Sleep(50); // optional: settle before write

                    _port.Write($"{command}\r\n");
                    SafeLog($"Sending {command}", LogLevel.Debug);

                    if (!expectResponse)
                        return null;

                    var buffer = new StringBuilder();
                    //var timeout = DateTime.Now.AddMilliseconds(750);
                    var timeout = DateTime.Now.AddMilliseconds(_port.ReadTimeout);
                    var quietWindow = TimeSpan.FromMilliseconds(100);
                    DateTime lastDataTime = DateTime.Now;
                    bool terminatorSeen = false;

                    while (DateTime.Now < timeout)
                    {
                        string chunk = string.Empty;

                        try
                        {
                            chunk = _port.ReadExisting();
                        }
                        catch (ObjectDisposedException)
                        {
                            SafeLog("Serial port was disposed during read.", LogLevel.Error);
                            throw new ASCOM.NotConnectedException("Serial port was disposed during read.");
                        }

                        if (!string.IsNullOrEmpty(chunk))
                        {
                            buffer.Append(chunk);
                            lastDataTime = DateTime.Now;

                            if (chunk.Contains('\r') || chunk.Contains('\n'))
                                terminatorSeen = true;
                        }

                        if (terminatorSeen && DateTime.Now - lastDataTime > quietWindow)
                            break;

                        Thread.Sleep(10);
                    }

                    string raw = buffer.ToString();
                    SafeLog($"Sent - {command} \t received - {raw}", LogLevel.Debug);

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        SafeLog($"No response received for command: {command}", LogLevel.Error);
                        throw new IOException($"No response received for command: {command}");
                    }

                    return raw;
                }
            }
            catch (ObjectDisposedException)
            {
                throw new ASCOM.NotConnectedException("Serial port was disposed.");
            }
            finally
            {
                _clearBusy?.Invoke();
                _gate.Release();
            }
        }

        public async Task<string?> SendAsync(string command)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SerialPortGuard), "SendAsync() called after disposal.");

            if (string.IsNullOrWhiteSpace(command))
            {
                SafeLog("SendAsync called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            await _gate.WaitAsync();

            try
            {
                lock (_disposeLock)
                {
                    if (_isDisposing)
                        throw new ObjectDisposedException(nameof(SerialPortGuard));

                    _setBusy?.Invoke();

                    if (_port == null || !_port.IsOpen)
                        throw new ASCOM.NotConnectedException("Serial port is not open.");

                    _port.WriteLine(command);
                    string response = _port.ReadLine();

                    SafeLog($"Async send - {command} \t got - {response}", LogLevel.Debug);
                    return response;
                }
            }
            catch (IOException ex)
            {
                SafeLog($"Async I/O error: {ex.Message}", LogLevel.Error);
                return null;
            }
            finally
            {
                _clearBusy?.Invoke();
                _gate.Release();
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                _isDisposing = true;

                try { _port?.Close(); } catch { }
                try { _port?.Dispose(); } catch { }
                try { _gate?.Dispose(); } catch { }

                _disposed = true;
            }
        }

        private void SafeLog(string message, LogLevel level)
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                try { _logger?.Log(message, level); }
                catch { /* suppress logging errors during disposal */ }
            }
        }

        private void FlushIfStale(string context)
        {
            lock (_disposeLock)
            {
                if (_port?.BytesToRead > 0)
                {
                    string leftover = _port.ReadExisting();
                    if (!string.IsNullOrWhiteSpace(leftover))
                        SafeLog($"[{context}] Unexpected data in buffer: {leftover}", LogLevel.Warning);
                }
            }
        }
    }
}