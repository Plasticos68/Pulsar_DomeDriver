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

        public SerialPortGuard(SerialPort port, FileLogger logger, Action setBusy, Action clearBusy)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _logger = logger;
            _setBusy = setBusy;
            _clearBusy = clearBusy;
        }

        public bool IsReady => !_isDisposing && _port?.IsOpen == true;

        public string? Send(string command, bool expectResponse = true)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                _logger?.Log("Send called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            _logger?.Log($"Waiting for serial gate to send: {command}", LogLevel.Debug);
            _gate.Wait();
            _logger?.Log($"Serial gate acquired for: {command}", LogLevel.Trace);

            try
            {
                if (_isDisposing)
                    throw new ObjectDisposedException(nameof(SerialPortGuard));

                _setBusy?.Invoke();

                if (_port == null || !_port.IsOpen)
                    throw new ASCOM.NotConnectedException("Serial port is not open.");

                // Aggressive pre-flush with retry
                for (int i = 0; i < 3; i++)
                {
                    _port.DiscardInBuffer();
                    Thread.Sleep(50);
                    if (_port.BytesToRead == 0) break;

                    string leftover = _port.ReadExisting();
                    if (!string.IsNullOrWhiteSpace(leftover))
                        _logger?.Log($"[FlushAttempt {i + 1}] Unexpected data in buffer before sending '{command}': {leftover}", LogLevel.Warning);
                }

                _port.DiscardOutBuffer();
                Thread.Sleep(50); // settle time

                _port.Write($"{command}\r");
                _logger?.Log($"Sending {command}");

                if (!expectResponse)
                    return null;

                var buffer = new StringBuilder();
                var timeout = DateTime.Now.AddMilliseconds(750);
                bool terminatorSeen = false;

                while (DateTime.Now < timeout)
                {
                    string chunk = _port.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        buffer.Append(chunk);
                        if (chunk.Contains('\r') || chunk.Contains('\n'))
                            terminatorSeen = true;
                    }

                    if (terminatorSeen)
                        break;

                    Thread.Sleep(10);
                }

                string raw = buffer.ToString();
                _logger?.Log($"Sent - {command} \t received - {raw}", LogLevel.Debug);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    _logger?.Log($"No response received for command: {command}", LogLevel.Error);
                    throw new IOException($"No response received for command: {command}");
                }

                return raw;
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
            if (string.IsNullOrWhiteSpace(command))
            {
                _logger?.Log("SendAsync called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            await _gate.WaitAsync();

            try
            {
                if (_isDisposing)
                    throw new ObjectDisposedException(nameof(SerialPortGuard));

                _setBusy?.Invoke();

                if (_port == null || !_port.IsOpen)
                    throw new ASCOM.NotConnectedException("Serial port is not open.");

                _port.WriteLine(command);
                string response = _port.ReadLine();

                _logger?.Log($"Async send - {command} \t got - {response}", LogLevel.Debug);
                return response;
            }
            catch (IOException ex)
            {
                _logger?.Log($"Async I/O error: {ex.Message}", LogLevel.Error);
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
            }
        }

        private void FlushIfStale(string context)
        {
            if (_port?.BytesToRead > 0)
            {
                string leftover = _port.ReadExisting();
                if (!string.IsNullOrWhiteSpace(leftover))
                    _logger?.Log($"[{context}] Unexpected data in buffer: {leftover}", LogLevel.Warning);
            }
        }
    }
}