using ASCOM;
using ASCOM.DeviceInterface;
using ASCOM.Utilities;
using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Diagnostics;
using Pulsar_DomeDriver.Goodnight;
using Pulsar_DomeDriver.Helper;
using Pulsar_DomeDriver.MQTT;
using Pulsar_DomeDriver.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SerialPortGuard = Pulsar_DomeDriver.Helper.SerialPortGuard;

namespace Pulsar_DomeDriver.Driver
{
    [ComVisible(true)]
    [Guid("5096766c-f998-42bd-a22d-8f0a00ed4b51")]
    [ProgId("Pulsar_DomeDriver")]

    public class DomeDriver : IDomeV2
    {
        #region Class setup
        private Profile _profile;
        private FileLogger _logger;
        private ConfigManager _config;
        private SerialPort _port;
        private SerialPortGuard? _guard;

        // connection
        private bool _connected = false;
        private readonly string _pingResponse = "Y159";
        private readonly string _generalResponse = "A";

        // extras
        private readonly object _resetLock = new();
        private readonly object _mqttLock = new();
        private readonly object _actionLock = new();
        private readonly object _watchdogLock = new object();

        //polling thread for serial comms
        private CancellationTokenSource _pollingCancel = null;
        private bool _pollingActive = false;
        private readonly object _pollingLock = new();
        private Task _pollingTask = null;

        // Timestamp updated by polling loop
        private DateTime _lastPollTimestamp = DateTime.MinValue;

        // Watchdog control
        private CancellationTokenSource? _systemWatchdogCts;
        private Task? _systemWatchdogTask;
        private readonly TimeSpan _systemWatchdogInterval = TimeSpan.FromSeconds(5);         // How often to check
        private readonly TimeSpan _pollingStallThreshold = TimeSpan.FromSeconds(10);
        //private volatile bool _config.CommandInProgress = false;
        private bool _rebooting = false;
        private int _watchdogGeneration = 0;
        private readonly object _alarmTimerLock = new();
        private CancellationTokenSource _alarmTimerCts;
        private Task _alarmTimerTask;
        private DateTime _lastWatchdogPing = DateTime.UtcNow;
        private readonly TimeSpan _alarmTimeout = TimeSpan.FromSeconds(30); // or 2× watchdog interval

        // Dome/Shutter status variables
        private string oldDomeActivity;
        private string oldShutterActivity;
        private string domeCurrent;
        private string shutterCurrent;
        private string domeOutputStatus;
        private double changeAzimuth;

        // Dome/Shutter control
        private CancellationTokenSource? _actionWatchdogCts;
        private ActionWatchdog _actionWatchdog;

        // last command
        private DomeCommandIntent _lastIntent = DomeCommandIntent.None;

        // MQTT
        private MqttPublisher _mqttPublisher;
        string _mqttStatus = "Dome/DriverStatus";
        string _mqttDomeStatus = "Dome/Dome/Status";
        string _mqttShutterStatus = "Dome/Shutter/Status";
        string _mqttAlarm = "Dome/Alarm/Status";
        string _mqttAlarmMessage = "Dome/Alarm/Message";
        string _mqttWatchdog = "Dome/Watchdog";

        // GNS
        private GNS _GNS;

        // Dsiposal etc
        private volatile bool _disposed = false;

        // alarm
        private volatile bool _alarmTriggered = false;
        private readonly object _alarmLock = new();

        #endregion

        public DomeDriver()
        {
            try
            {
                const string driverId = "Pulsar_DomeDriver";

                // setup temporary profile to read registry values
                var initialProfile = new Profile();
                initialProfile.DeviceType = "Dome";
                _profile = initialProfile;

                // Resolve and constructlog directory
                string regLogPath = _profile.GetValue(driverId, "LogLocation", "");
                if (string.IsNullOrWhiteSpace(regLogPath))
                {
                    regLogPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Logs"
                    );
                }
                string logPath = Path.Combine(regLogPath, "Pulsar_DomeDriver.log");
                SafeLog($"Registry LogLocation = '{regLogPath}'", LogLevel.Debug);

                // Resolve debug flag
                string debugRaw = _profile.GetValue(driverId, "DebugLog", "");
                bool debugLog = string.Equals(debugRaw, "true", StringComparison.OrdinalIgnoreCase);
                SafeLog($"Registry DebugLog = '{debugRaw}' → parsed as {debugLog}", LogLevel.Debug);

                string traceRaw = _profile.GetValue(driverId, "TraceLog", "");
                bool traceLog = string.Equals(traceRaw, "true", StringComparison.OrdinalIgnoreCase);
                SafeLog($"Registry DebugLog = '{traceRaw}' → parsed as {traceLog}", LogLevel.Debug);

                // Initialize logger, config and GNS
                _logger = new FileLogger(logPath, debugLog, traceLog);
                _config = new ConfigManager(_profile, _logger);
                _GNS = new GNS(_logger, _config);

                //int _pollingIntervalMs = _config.pollingIntervalMs;

                // Proceed with driver setup
                InitializeSerialPort();
                AppDomain.CurrentDomain.ProcessExit += (_, __) => Dispose();
                AppDomain.CurrentDomain.DomainUnload += (_, __) => Dispose();
            }
            catch (Exception ex)
            {
                SafeLog($"Driver initialization failed: {ex}", LogLevel.Error);
                throw;
            }
        }

        #region Connection

        private void InitializeSerialPort()
        {
            string serialPort = _profile.GetValue(_config._driverId, "SerialPort", "");
            if (!string.IsNullOrWhiteSpace(serialPort))
            {
                _port = new SerialPort
                {
                    PortName = serialPort,
                    BaudRate = 115200,
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Handshake = Handshake.None,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
            }
        }

        public bool Connected
        {
            get => _connected;
            set
            {
                lock (_pollingLock)
                {
                    if (value && _connected)
                    {
                        SafeLog("Already connected. Skipping reinitialization.", LogLevel.Debug);
                        return;
                    }

                    if (value)
                    {
                        bool success = ConnectController();

                        if (!success)
                            throw new ASCOM.NotConnectedException("Failed to connect to controller.");

                        if (_config.UseMQTT)
                        {
                            try
                            {
                                //StartMQTTAsync().GetAwaiter().GetResult(); // block until MQTT is ready
                                Task.Run(async () => await StartMQTTAsync()).Wait();
                                //TryMQTTPublish(_mqttStatus, "Driver connected");
                                ResetAlarmMonitor();
                                StartSystemMonitors();
                            }
                            catch (Exception ex)
                            {
                                SafeLog($"MQTT startup failed: {ex.Message}", LogLevel.Error);
                                throw new ASCOM.NotConnectedException("MQTT startup failed.");
                            }
                        }

                        lock (_pollingLock)
                        {
                            _connected = true;
                        }

                        SetPollingInterval(_config.pollingIntervalMs);
                        StartPolling();
                        _GNS.SendGNS(GNSType.Message, "Dome driver connected");
                    }
                    else
                    {
                        bool pollingStopped = StopPolling();
                        StopWatchdog();
                        if (pollingStopped)
                        {
                            Task pollingWaitTask = null;
                            if (pollingStopped && _pollingTask != null)
                            {
                                pollingWaitTask = _pollingTask;
                            }

                            // Release lock before waiting
                            lock (_pollingLock)
                            {
                                _connected = false;
                            }

                            if (pollingWaitTask != null)
                            {
                                if (!pollingWaitTask.Wait(3000)) // timeout in ms
                                {
                                    SafeLog("Polling task did not complete within timeout — continuing disconnect.", LogLevel.Warning);
                                }
                                else
                                {
                                    SafeLog("Polling task stopped successfully.", LogLevel.Debug);
                                }

                                Thread.Sleep(_config.serialSettle); // Optional settle delay
                            }
                            SafeLog("Polling task stopped successfully.", LogLevel.Debug);
                        }
                        if (_mqttPublisher != null && _mqttPublisher.IsConnected)
                        {
                            Task.Run(async () => await _mqttPublisher.PublishAsync(_mqttStatus, "Driver disconnected"));
                            Task.Run(async () => await _mqttPublisher.DisconnectAsync());
                            SafeLog("MQTT disconnected", LogLevel.Info);
                        }



                        lock (_pollingLock)
                        {
                            _connected = false;
                        }
                        SafeLog("Disconnect initiated.", LogLevel.Info);
                        var start = DateTime.UtcNow;

                        DisconnectController();

                        var elapsed = DateTime.UtcNow - start;
                        SafeLog($"Disconnect completed in {elapsed.TotalMilliseconds} ms.", LogLevel.Info);
                        _GNS.SendGNS(GNSType.Message, "Dome driver disconnected");
                    }
                }
            }
        }

        public bool ConnectController()
        {
            if (_port == null)
            {
                SafeLog("Serial port not initialized. Did InitializeSerialPort run?", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port is not initialized.");
            }

            if (string.IsNullOrWhiteSpace(_port.PortName))
            {
                SafeLog("Connection attempt but Serial Port name is missing.", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port name is missing. Please run Setup.");
            }

            if (!_port.IsOpen)
            {
                try
                {
                    _port.Open();
                    SafeLog($"Serial port {_port.PortName} opened.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    SafeLog($"Failed to open serial port {_port.PortName}: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }

            try
            {
                // Create the guard immediately after opening
                _guard = new SerialPortGuard(
                    _port,
                    _logger,
                    () =>
                    {
                        lock (_pollingLock)
                        {
                            _config.CommandInProgress = true;
                            SafeLog($"[Guard] CommandInProgress = true at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Debug);
                        }
                    },
                    () =>
                    {
                        lock (_pollingLock)
                        {
                            _config.CommandInProgress = false;
                            SafeLog($"[Guard] CommandInProgress = false at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Debug);
                        }
                    }
                );
            }
            catch (Exception ex)
            {
                SafeLog($"Failed to create SerialPortGuard: {ex.Message}", LogLevel.Error);
                _port.Close();
                _guard = null;
                SafeLog("Serial port closed due to guard creation failure.", LogLevel.Info);
                return false;
            }

            //Thread.Sleep(_config.initialPingDelay);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                //_guard.Send("NOP", false); // Arduino ignores this
                //Thread.Sleep(700);
                if (_guard != null && PingController())
                {
                    SafeLog("Connected ok", LogLevel.Info);
                    return true;
                }

                //Thread.Sleep(100); // between attempts

                if (attempt == 1)
                {
                    SafeLog("Ping failed after 2 attempts.", LogLevel.Error);
                    try
                    {
                        _port.Close();
                        SafeLog("Serial port closed after failed ping.", LogLevel.Debug);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Error closing serial port: {ex.Message}", LogLevel.Error);
                    }

                    if (_guard != null)
                    {
                        try
                        {
                            _guard.Dispose();
                            SafeLog("SerialPortGuard disposed after failed ping.", LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            SafeLog($"Error disposing SerialPortGuard: {ex.Message}", LogLevel.Error);
                        }
                        _guard = null;
                    }
                    SafeLog("Driver disconnected.", LogLevel.Info);
                }
            }

            return false;
        }

        public bool PingController()
        {
            try
            {
                return SendAndVerify("PULSAR", ResponseMode.MatchExact, new[] { _pingResponse }).IsMatch;
            }
            catch (Exception ex)
            {
                SafeLog($"PingController failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        public bool DisconnectController(bool reboot = false)
        {
            if (reboot)
            {
                try
                {
                    _port?.Close();
                    _guard?.Dispose();
                    _guard = null;
                    SafeLog("Serial port forcibly closed.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    SafeLog($"Forced port close failed: {ex.Message}", LogLevel.Error);
                }
                return true;
            }

            try
            {
                StopPolling();
                StopWatchdog();

                if (_port != null && _port.IsOpen)
                {
                    try
                    {
                        _port.Close();
                        _guard?.Dispose();
                        _guard = null;
                        SafeLog($"Serial port {_port.PortName} closed.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Error closing serial port {_port.PortName}: {ex.Message}", LogLevel.Error);
                    }
                }
                SafeLog("Driver disconnected.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                SafeLog($"DisconnectController failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private void LogConnectionSnapshot()
        {
            SafeLog("=== Dome Driver Connection Snapshot ===", LogLevel.Trace);

            SafeLog($"PollingActive: {_pollingActive}", LogLevel.Trace);
            SafeLog($"PollingTask status: {_pollingTask?.Status}", LogLevel.Trace);
            SafeLog($"CommandInProgress: {_config.CommandInProgress}", LogLevel.Trace);
            SafeLog($"Rebooting: {_rebooting}", LogLevel.Trace);
            SafeLog($"ResetInProgress: {_config.Resetting}", LogLevel.Trace);
            SafeLog($"LastPollTimestamp: {_lastPollTimestamp:HH:mm:ss.fff}", LogLevel.Trace);
            SafeLog($"ShutterStatus: {_config.ShutterStatus}", LogLevel.Trace);
            SafeLog($"DomeState: {_config.DomeState}", LogLevel.Trace);
            SafeLog($"HomeStatus: {_config.HomeStatus}", LogLevel.Trace);
            SafeLog($"ParkStatus: {_config.ParkStatus}", LogLevel.Trace);
            SafeLog("=======================================", LogLevel.Trace);

        }

        #endregion

        #region Polling section

        public void SetPollingInterval(int milliseconds)
        {
            lock (_pollingLock)
            {
                if (milliseconds < 50 || milliseconds > 10000)
                {
                    SafeLog($"Polling interval {milliseconds} ms is out of bounds. Clamping to safe range.", LogLevel.Error);
                    milliseconds = Math.Max(50, Math.Min(milliseconds, 10000));
                }
                _config.pollingIntervalMs = milliseconds;
            }
        }

        private struct PollingStartupSnapshot
        {
            public bool CommandInProgress;
            public bool Rebooting;
            public int PollingIntervalMs;
            public int ControllerTimeout;
        }

        public void StartPolling()
        {
            SafeLog("StartPolling() invoked — launching polling loop", LogLevel.Info);

            PollingStartupSnapshot snapshot;

            lock (_pollingLock)
            {
                if (_config.CommandInProgress)
                {
                    SafeLog("Polling start blocked: command in progress.", LogLevel.Debug);
                    return;
                }

                if (_pollingActive)
                {
                    SafeLog("Polling already active. Skipping restart.", LogLevel.Debug);
                    return;
                }

                if (_pollingTask != null)
                {
                    SafeLog($"Polling task status: {_pollingTask.Status}", LogLevel.Trace);

                    if (_pollingTask.IsCompleted || _pollingTask.IsFaulted || _pollingTask.IsCanceled)
                    {
                        SafeLog($"Polling task is stale (status: {_pollingTask.Status}). Resetting.", LogLevel.Debug);
                        _pollingTask = null;
                    }
                }

                SafeLog($"Starting polling with interval {_config.pollingIntervalMs} ms.", LogLevel.Debug);

                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = new CancellationTokenSource();

                _lastPollTimestamp = DateTime.UtcNow;

                // 🧠 Capture atomic snapshot of startup flags
                snapshot = new PollingStartupSnapshot
                {
                    CommandInProgress = _config.CommandInProgress,
                    Rebooting = _config.Rebooting,
                    PollingIntervalMs = _config.pollingIntervalMs,
                    ControllerTimeout = _config.controllerTimeout
                };

                try
                {
                    _pollingTask = Task.Run(() => PollLoopAsync(_pollingCancel.Token, snapshot));
                    _pollingActive = true;
                }
                catch (Exception ex)
                {
                    SafeLog($"Failed to start polling task: {ex.Message}", LogLevel.Error);
                    _pollingActive = false;
                }
            }
        }

        public bool StopPolling()
        {
            lock (_pollingLock)
            {
                if (_config.CommandInProgress)
                {
                    SafeLog("Polling stop deferred: command in progress.", LogLevel.Debug);
                    return false;
                }

                //***********************************************
                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = null;
                _pollingActive = false;
                _config.CommandInProgress = false;
                //_config.Rebooting = false;
                //_config.Resetting = false;
                //***********************************************
                _pollingTask = null;

                SafeLog("Polling cancelled.", LogLevel.Debug);
                return true;
            }
        }

        private async Task PollLoopAsync(CancellationToken token, PollingStartupSnapshot startup)
        {
            if (_disposed) return;

            SafeLog("Polling loop heartbeat", LogLevel.Trace);
            int cycleCount = 0;
            int startupWaitMs = 0;

            try
            {
                // 🧠 Wait for readiness if command or reboot was active at launch
                while (!_disposed && !token.IsCancellationRequested)
                {
                    if (!startup.CommandInProgress && !startup.Rebooting)
                        break;

                    if (startupWaitMs == 0)
                    {
                        SafeLog($"Polling delayed: command={startup.CommandInProgress}, reboot={startup.Rebooting}. Waiting for readiness...", LogLevel.Debug);
                    }

                    await Task.Delay(startup.PollingIntervalMs, token);
                    startupWaitMs += startup.PollingIntervalMs;

                    if (startupWaitMs >= startup.ControllerTimeout)
                    {
                        SafeLog("Polling startup timed out after 10s waiting for readiness. Exiting.", LogLevel.Warning);
                        return;
                    }
                }

                if (_disposed || token.IsCancellationRequested) return;

                SafeLog("Polling loop started.", LogLevel.Debug);
                int errorCount = 0;

                while (!_disposed && !token.IsCancellationRequested)
                {
                    cycleCount++;
                    bool shouldPoll;
                    int interval;

                    lock (_pollingLock)
                    {
                        shouldPoll = _pollingActive;
                        interval = _config.pollingIntervalMs;
                    }

                    if (shouldPoll)
                    {
                        try
                        {
                            _lastPollTimestamp = DateTime.UtcNow;
                            SystemStatus(); // Safe outside lock
                            errorCount = 0;

                            // 🧠 Watchdog success coordination
                            if (_lastIntent == DomeCommandIntent.GoHome && _config.HomeStatus)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.Park && _config.ParkStatus)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.OpenShutter && _config.ShutterStatus == 0)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.CloseShutter && _config.ShutterStatus == 1)
                                _actionWatchdog?.MarkSuccess();
                            else if (_lastIntent == DomeCommandIntent.SlewAzimuth && _config.DomeState == 0)
                            {
                                _actionWatchdog?.MarkSuccess();
                                _lastIntent = DomeCommandIntent.None;
                            }

                            SafeLog($"Polling cycle at {DateTime.UtcNow:HH:mm:ss}, interval {interval} ms.", LogLevel.Trace);
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            SafeLog($"Polling error #{errorCount}: {ex.Message}", LogLevel.Debug);

                            if (errorCount >= _config.pollingLoopRetries)
                            {
                                SafeLog($"Polling error threshold reached ({_config.pollingLoopRetries}). Invoking PollingLoopFailure().", LogLevel.Warning);
                                HandleDriverFailure(ex, errorCount);
                                break;
                            }

                            if (errorCount >= 3)
                            {
                                SafeLog("Throttling polling due to repeated errors.", LogLevel.Debug);
                                await Task.Delay(interval, token);
                            }
                        }
                    }

                    try
                    {
                        await Task.Delay(interval, token);
                    }
                    catch (TaskCanceledException)
                    {
                        SafeLog("Polling delay cancelled.", LogLevel.Debug);
                        break;
                    }
                    catch (Exception ex)
                    {
                        HandleDriverFailure(ex);
                        SafeLog($"Polling delay error: {ex.Message}", LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    SafeLog($"Polling thread terminated unexpectedly: {ex.Message}", LogLevel.Debug);
            }
            finally
            {
                SafeLog($"Polling thread exited cleanly. Last cancellation state: {token.IsCancellationRequested}, disposed={_disposed}", LogLevel.Debug);
            }
        }

        private void HandleDriverFailure(Exception lastException, int errorCount = 0, string reason = "Unknown")
        {
            //SafeLog($"[DriverFailure] Triggered by {reason}. Last error: {lastException.Message}", LogLevel.Error);
            AlarmOn(reason);
            //_mqttPublisher?.PublishAsync(_mqttAlarm, $"Alarm: {reason}");
            _actionWatchdog?.MarkFailure();

            _pollingActive = false;
            Connected = false;

            //OnDriverDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void StartSystemMonitors()
        {
            StartAlarmMonitor();
            StartWatchdog();
        }

        private void StartWatchdog()
        {
            StartAlarmMonitor();
            lock (_watchdogLock)
            {
                if (_disposed) return;

                try
                {
                    _systemWatchdogCts?.Cancel();
                    _systemWatchdogCts?.Dispose();
                }
                catch (Exception ex)
                {
                    SafeLog($"Error disposing previous watchdog CTS: {ex.Message}", LogLevel.Warning);
                }

                _systemWatchdogCts = new CancellationTokenSource();
                var token = _systemWatchdogCts.Token;

                _systemWatchdogTask = Task.Run(async () =>
                {
                    lock (_watchdogLock)
                    {
                        if (_disposed) return;
                    }

                    try { SafeLog("Polling watchdog started.", LogLevel.Debug); } catch { }

                    _config.WatchdogRunning = true;

                    try
                    {
                        while (true)
                        {
                            lock (_watchdogLock)
                            {
                                if (_disposed || token.IsCancellationRequested)
                                    break;
                            }

                            try
                            {
                                await Task.Delay(_systemWatchdogInterval, token);

                                // 🧠 MQTT heartbeat
                                if (_mqttPublisher?.IsConnected == true)
                                {
                                    string utcTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
                                    string heartbeat = $"Alive at {utcTime}";
                                    TryMQTTPublish(_mqttWatchdog, $"{utcTime} :{domeOutputStatus}");
                                    AlarmHeartbeat();
                                    _lastWatchdogPing = DateTime.UtcNow;
                                }
                                else
                                {
                                    SafeLog("[Watchdog] MQTT not connected — heartbeat skipped", LogLevel.Warning);
                                }

                                // 🧠 Polling stall detection
                                var elapsed = DateTime.UtcNow - _lastPollTimestamp;
                                if (elapsed > _pollingStallThreshold)
                                {
                                    SafeLog($"Polling watchdog triggered — last poll was {elapsed.TotalSeconds:F1}s ago. Restarting polling.", LogLevel.Debug);

                                    StopPolling();
                                    Thread.Sleep(50); // brief pause to ensure clean stop
                                    StartPolling();
                                }
                            }
                            catch (TaskCanceledException)
                            {
                                lock (_watchdogLock)
                                {
                                    if (!_disposed)
                                        SafeLog("Polling watchdog cancelled.", LogLevel.Debug);
                                }
                                break;
                            }
                            catch (Exception ex)
                            {
                                lock (_watchdogLock)
                                {
                                    if (!_disposed)
                                        SafeLog($"Polling watchdog error: {ex.Message}", LogLevel.Error);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _config.WatchdogRunning = false;
                        lock (_watchdogLock)
                        {
                            if (!_disposed)
                                SafeLog("Polling watchdog exited.", LogLevel.Debug);
                        }
                    }
                }, token);
            }
        }

        private void StopWatchdog()
        {
            if (_systemWatchdogCts != null)
            {
                SafeLog("Stopping polling watchdog.", LogLevel.Debug);

                try
                {
                    _systemWatchdogCts.Cancel();
                }
                catch (Exception ex)
                {
                    SafeLog($"Error cancelling watchdog: {ex.Message}", LogLevel.Error);
                }
                _systemWatchdogCts.Dispose();
                _systemWatchdogCts = null;
            }
            _systemWatchdogTask = null;
        }

        private void StartAlarmMonitor()
        {
            lock (_alarmTimerLock)
            {
                _alarmTimerCts?.Cancel();
                _alarmTimerCts = new CancellationTokenSource();
                var token = _alarmTimerCts.Token;

                _alarmTimerTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(5000, token); // check every 5s

                            var elapsed = DateTime.UtcNow - _lastWatchdogPing;
                            if (elapsed > _alarmTimeout)
                            {
                                AlarmOn("Watchdog failed to reset alarm timer — possible hang or stall.");
                                return;
                            }
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        SafeLog($"Alarm monitor error: {ex.Message}", LogLevel.Warning);
                    }
                }, token);
            }
        }

        private void StopAlarmMonitor()
        {
            lock (_alarmTimerLock)
            {
                _alarmTimerCts?.Cancel();
                _alarmTimerCts?.Dispose();
                _alarmTimerCts = null;
                _alarmTimerTask = null;
            }
        }

        private void ResetAlarmMonitor()
        {
            lock (_alarmTimerLock)
            {
                SafeLog("Resetting Alarm Monitor alarm", LogLevel.Info);
                _alarmTriggered = false;
                StopAlarmMonitor();
                TryMQTTPublish(_mqttAlarm, "Not set");
                TryMQTTPublish(_mqttAlarmMessage, "");
            }
        }

        public void AlarmOn(string message = null)
        {
            lock (_alarmLock)
            {
                if (_alarmTriggered)
                {
                    // Already triggered — do nothing
                    return;
                }

                _alarmTriggered = true;

                TryMQTTPublish(_mqttAlarm, "on");
                SafeLog("Alarm triggered: status 'on' published", LogLevel.Warning);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    TryMQTTPublish(_mqttAlarmMessage, message);
                    SafeLog($"Alarm message published: {message}", LogLevel.Warning);
                }
            }
        }

        public void AlarmHeartbeat()
        {
            lock (_alarmLock)
            {
                if (_alarmTriggered)
                {
                    TryMQTTPublish(_mqttAlarm, "on");
                }
                else
                {
                    TryMQTTPublish(_mqttAlarm, "off");
                }
            }
        }


        #endregion

        #region Pulsar Dome specific methods

        public void SystemStatus()
        {
            var retryPolicy = new RetryPolicy
            {
                MaxAttempts = _config.statusMaxRetries,
                DelayMs = 100,
                ExponentialBackoff = false
            };

            try
            {
                retryPolicy.Execute(() =>
                {
                    bool domeOk = ParseDomeStatus();
                    bool homeOk = ParseHomeStatus();
                    bool parkOk = ParseParkStatus();
                    SlewingStatus();

                    bool allOk = domeOk && homeOk && parkOk;

                    if (!allOk)
                    {
                        SafeLog("[DomeStatus] Status check failed", LogLevel.Warning);
                        if (!domeOk) SafeLog("Dome status malformed", LogLevel.Debug);
                        if (!homeOk) SafeLog("Home status malformed", LogLevel.Debug);
                        if (!parkOk) SafeLog("Park status malformed", LogLevel.Debug);
                    }

                    return allOk;

                }, isSuccess => isSuccess);

                CompleteSystemStatus();
            }
            catch (TimeoutException)
            {
                SafeLog("[DomeStatus] Status check failed after retries — raising alarm", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Dome status failed after retries");
            }
        }

        public void CompleteSystemStatus()
        {
            SafeLog($"[DomeStatus] Invoked at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);

            try
            {
                _config.ControllerReady = true;

                // Dome status description
                switch (_config.DomeState)
                {
                    case 0: domeCurrent = "Idle"; break;
                    case 1: domeCurrent = "Slewing"; break;
                    case 9: domeCurrent = "Finding home"; break;
                    default: domeCurrent = "Unknown state"; break;
                }

                if (_config.HomeStatus) domeCurrent = "Home";
                if (_config.ParkStatus) domeCurrent = "Parked";

                // Shutter status description
                switch (_config.ShutterStatus)
                {
                    case 0: shutterCurrent = "Open"; break;
                    case 1: shutterCurrent = "Closed"; break;
                    case 2: shutterCurrent = "Opening"; break;
                    case 3: shutterCurrent = "Closing"; break;
                    case 4: shutterCurrent = "Error"; break;
                    case 5: shutterCurrent = "Unknown"; break;
                    default: shutterCurrent = "Invalid status"; break;
                }

                LogDomeStatus(domeCurrent, shutterCurrent);

                // MQTT section
                if (_mqttPublisher?.IsConnected == true && _config.UseMQTT)
                {
                    TryMQTTPublish(_mqttDomeStatus, domeCurrent);
                    TryMQTTPublish(_mqttShutterStatus, shutterCurrent);
                }

                domeOutputStatus =
                    $"{_config.DomeState}," +
                    $"{(_config.HomeStatus ? 1 : 0)}," +
                    $"{(_config.ParkStatus ? 1 : 0)}," +
                    $"{_config.Azimuth}," +
                    $"{_config.TargetAzimuth}," +
                    $"{(_config.SlewingStatus ? 1 : 0)}," +
                    $"{_config.ShutterStatus}";

                // Unified completion check
                bool complete = IsCommandComplete();
                if (complete)
                {
                    if (_config.ForceBusy)
                    {
                        SafeLog($"ForceBusy overridden: command {_lastIntent} appears complete based on status.", LogLevel.Debug);
                        _config.ForceBusy = false;
                    }

                    _actionWatchdog?.MarkSuccess();
                    var completedIntent = _lastIntent;
                    _lastIntent = DomeCommandIntent.None;

                    switch (completedIntent)
                    {
                        case DomeCommandIntent.GoHome:
                            _GNS.SendGNS(GNSType.Stop, "Dome homed successfully");
                            break;
                        case DomeCommandIntent.Park:
                            _GNS.SendGNS(GNSType.Stop, "Dome parked successfully");
                            break;
                        case DomeCommandIntent.OpenShutter:
                            _GNS.SendGNS(GNSType.Stop, "Shutter opened successfully");
                            break;
                        case DomeCommandIntent.CloseShutter:
                            _GNS.SendGNS(GNSType.Stop, "Shutter closed successfully");
                            break;
                        case DomeCommandIntent.SlewAzimuth:
                            if (changeAzimuth >= _config.JogSize)
                                _GNS.SendGNS(GNSType.Stop, "Dome slew successful");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog($"DomeStatus error: {ex.Message}", LogLevel.Debug);
            }
        }

        public bool ParseDomeStatus()
        {
            string? raw = SendAndVerify("V", ResponseMode.Raw).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                SafeLog("Empty or null dome response.", LogLevel.Error);
                return false;
            }

            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var tokens = line.Trim().Split('\t');
                if (tokens.Length >= 13)
                {
                    if (TryParseDomeTokens(tokens))
                        return true;
                }
            }

            SafeLog($"Dome response had {lines.Length} lines. No valid status line found. Raw: '{raw}'", LogLevel.Debug);
            return false;
        }

        private bool TryParseDomeTokens(string[] tokens)
        {
            if (tokens.Length < 13)
            {
                SafeLog($"Insufficient token count: expected 13, got {tokens.Length}", LogLevel.Debug);
                return false;
            }

            try
            {
                if (!double.TryParse(tokens[0], out double azimuth) || azimuth < 0 || azimuth > 360)
                    throw new ArgumentOutOfRangeException("Azimuth", azimuth, "Must be between 0 and 360");

                if (!int.TryParse(tokens[1], out int state) || state < 0 || state > 9)
                    throw new ArgumentOutOfRangeException("State", state, "Must be between 0 and 9");

                if (!double.TryParse(tokens[2], out double rot))
                    throw new FormatException($"Invalid rotation value: '{tokens[2]}'");

                if (!double.TryParse(tokens[3], out double targetAzimuth) || targetAzimuth < -179 || targetAzimuth > 539)
                    throw new ArgumentOutOfRangeException("TargetAzimuth", targetAzimuth, "Must be between -179 and 539");

                double normalizedTarget = (targetAzimuth % 360 + 360) % 360;
                SafeLog($"Target azimuth: raw={targetAzimuth}, normalized={normalizedTarget}", LogLevel.Trace);

                if (!int.TryParse(tokens[4], out int motorDir) || motorDir < 0 || motorDir > 2)
                    throw new ArgumentOutOfRangeException("MotorDir", motorDir, "Must be 0, 1, or 2");

                if (!short.TryParse(tokens[5], out short shutterStatus) || shutterStatus < 0 || shutterStatus > 6)
                    throw new ArgumentOutOfRangeException("ShutterStatus", shutterStatus, "Must be between 0 and 6");

                if (!int.TryParse(tokens[6], out int shutterPercentage) || shutterPercentage < 0 || shutterPercentage > 1000)
                    throw new ArgumentOutOfRangeException("ShutterPercentage", shutterPercentage, "Must be between 0 and 1000");

                if (!int.TryParse(tokens[7], out int shutterVoltage))
                    throw new FormatException($"Invalid shutter voltage: '{tokens[7]}'");

                if (!int.TryParse(tokens[8], out int shutterCurrent))
                    throw new FormatException($"Invalid shutter current: '{tokens[8]}'");

                if (!int.TryParse(tokens[9], out int encoderValue))
                    throw new FormatException($"Invalid encoder value: '{tokens[9]}'");

                if (!int.TryParse(tokens[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp))
                    throw new FormatException($"Invalid temperature value: '{tokens[10]}'");

                if (!int.TryParse(tokens[12], out int relayInt) || (relayInt != 0 && relayInt != 1))
                    throw new ArgumentOutOfRangeException("RelayState", relayInt, "Must be 0 or 1");

                bool relayState = relayInt == 1;

                lock (_pollingLock)
                {
                    _config.Azimuth = azimuth;
                    _config.DomeState = state;
                    _config.Rot = rot;
                    _config.TargetAzimuth = targetAzimuth;
                    _config.MotorDir = motorDir;
                    _config.ShutterStatus = shutterStatus;
                    _config.ShutterPercentage = shutterPercentage;
                    _config.ShutterVoltage = shutterVoltage;
                    _config.ShutterCurrent = shutterCurrent;
                    _config.EncoderValue = encoderValue;
                    _config.Temp = temp;
                    _config.RelayState = relayState;
                }

                return true;
            }
            catch (Exception ex)
            {
                SafeLog($"Error parsing dome status: {ex.Message}", LogLevel.Debug);
                return false;
            }
        }

        public bool ParseHomeStatus()
        {
            string? raw = SendAndVerify("HOME ?", ResponseMode.MatchAny, new[] { "0", "1" }).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                SafeLog("Empty HOME response.", LogLevel.Debug);
                return false;
            }

            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed == "1")
                {
                    _config.HomeStatus = true;
                    return true;
                }
                else if (trimmed == "0")
                {
                    _config.HomeStatus = false;
                    return true;
                }
            }

            SafeLog($"Unexpected HOME response: '{raw}'", LogLevel.Debug);
            return false;
        }

        public bool ParseParkStatus()
        {
            string? raw = SendAndVerify("PARK ?", ResponseMode.MatchAny, new[] { "0", "1" }).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                SafeLog("Empty PARK response.", LogLevel.Debug);
                return false;
            }

            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed == "1")
                {
                    _config.ParkStatus = true;
                    return true;
                }
                else if (trimmed == "0")
                {
                    _config.ParkStatus = false;
                    return true;
                }
            }

            SafeLog($"Unexpected PARK response: '{raw}'", LogLevel.Debug);
            return false;
        }

        public void SlewingStatus()
        {
            bool forceBusy = _config.ForceBusy;

            if (forceBusy)
            {
                _config.SlewingStatus = true;
            }
            else
            {
                bool isStationary;

                lock (_pollingLock)
                {
                    isStationary = (_config.DomeState == 0 && (_config.ShutterStatus == 0 || _config.ShutterStatus == 1));
                }

                _config.SlewingStatus = !isStationary;
            }
        }

        private bool IsCommandComplete()
        {
            return _lastIntent switch
            {
                DomeCommandIntent.GoHome => _config.DomeState == 0 && _config.HomeStatus,
                DomeCommandIntent.SlewAzimuth => _config.DomeState == 0 &&
                    Math.Abs(_config.TargetAzimuth - _config.SlewAz) <= _config.AzimuthTolerance,
                DomeCommandIntent.OpenShutter => _config.ShutterStatus == 0,
                DomeCommandIntent.CloseShutter => _config.ShutterStatus == 1,
                DomeCommandIntent.Park => _config.DomeState == 0 && _config.ParkStatus,
                _ => true
            };
        }

        private void LogDomeStatus(string domeActivity, string shutterActivity)
        {
            if (domeActivity != oldDomeActivity && domeActivity != "")
            {
                SafeLog($"Dome is: \t{domeActivity}", LogLevel.Info);
                oldDomeActivity = domeActivity;
            }

            if (shutterActivity != oldShutterActivity && shutterActivity != "")
            {
                SafeLog($"Shutter is: \t{shutterActivity}", LogLevel.Info);
                oldShutterActivity = shutterActivity;
            }
        }

        public class RetryPolicy
        {
            public Action<string, LogLevel>? Log { get; set; }
            public int MaxAttempts { get; set; } = 3;
            public int DelayMs { get; set; } = 100;
            public bool ExponentialBackoff { get; set; } = false;

            public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, Func<T, bool> isSuccess)
            {
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    var result = await action();
                    if (isSuccess(result)) return result;

                    int delay = ExponentialBackoff ? DelayMs * (1 << attempt) : DelayMs;
                    await Task.Delay(delay);
                }

                throw new TimeoutException($"RetryPolicy failed after {MaxAttempts} attempts.");
            }

            public T Execute<T>(Func<T> action, Func<T, bool> isSuccess)
            {
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    Log?.Invoke($"RetryPolicy attempt {attempt}", LogLevel.Debug);
                    try
                    {
                        var result = action();
                        if (isSuccess(result)) return result;
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"RetryPolicy caught exception: {ex.Message}", LogLevel.Warning);
                    }

                    int delay = ExponentialBackoff ? DelayMs * (1 << attempt) : DelayMs;
                    Thread.Sleep(delay);
                }

                throw new TimeoutException($"RetryPolicy failed after {MaxAttempts} attempts.");
            }
        }

        #endregion

        #region Safe Calls

        private void SafeLog(string message, LogLevel level)
        {
            if (_disposed) return;
            try { _logger?.Log(message, level); }
            catch { /* suppress logging errors during disposal */ }
        }

        #endregion

        #region Reset

        public async Task ResetRoutineAsync(string reset = "full")
        {
            lock (_resetLock)
            {
                if (_config.Resetting)
                {
                    SafeLog("Reset already in progress — skipping duplicate trigger.", LogLevel.Warning);
                    return;
                }

                _config.Resetting = true;
            }

            CancelCurrentActionWatchdog();

            TryMQTTPublish("Dome/Debug", "in reset");

            try
            {
                SafeLog("Requesting watchdog cancellation...", LogLevel.Warning);
                _systemWatchdogCts?.Cancel();

                var timeout = Task.Delay(5000);
                while (_config.WatchdogRunning && !timeout.IsCompleted)
                {
                    SafeLog("Waiting for watchdog to exit...", LogLevel.Trace);
                    await Task.Delay(200);
                }

                if (_config.WatchdogRunning)
                {
                    SafeLog("Watchdog did not exit after cancellation — aborting reset.", LogLevel.Error);
                    _GNS.SendGNS(GNSType.Alarm, "Reset aborted: watchdog failed to exit after cancellation.");
                    lock (_resetLock) { _config.Resetting = false; }
                    return;
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Unexpected error while waiting for watchdog: {ex.Message}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Reset aborted due to watchdog coordination error.");
                lock (_resetLock) { _config.Resetting = false; }
                return;
            }

            bool softOnly = reset == "soft";
            bool hardOnly = reset == "hard";

            StopPolling();
            _config.ControllerReady = false;

            SafeLog("Triggering ResetRoutine due to command error or stall.", LogLevel.Info);
            _GNS.SendGNS(GNSType.Message, "Triggering ResetRoutine due to command error or stall.");

            lock (_pollingLock)
            {
                _config.SlewingStatus = true;
            }

            try
            {
                if ((_config.SoftReset || softOnly) && !_config.SoftResetAttempted)
                {
                    SafeLog("Performing soft reset.", LogLevel.Warning);
                    _config.SoftResetAttempted = true;
                    _config.SoftResetSuccess = false;
                    _config.HardResetAttempted = false;
                    _config.HardResetSuccess = false;

                    try
                    {
                        _config.SoftResetSuccess = await HardwareReset("soft");

                        bool proceed;
                        lock (_resetLock)
                        {
                            proceed = _config.SoftResetSuccess && !_config.Rebooting;
                        }

                        if (proceed)
                        {
                            SafeLog("Soft reset completed successfully.", LogLevel.Info);
                            _GNS.SendGNS(GNSType.Message, "Soft reset completed successfully.");

                            lock (_pollingLock)
                            {
                                _config.SlewingStatus = false;
                                _config.DomeState = 0;
                            }

                            StartPolling();
                            Thread.Sleep(_config.serialSettle);
                            await Task.Delay(_config.serialSettle * 4);

                            _ = Task.Run(ReplayLastCommand);
                            return;
                        }

                        SafeLog("Soft reset failed or rebooting still active.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Soft reset failed: {ex.Message}", LogLevel.Error);
                    }
                }

                if ((_config.HardReset || hardOnly) && !_config.HardResetAttempted)
                {
                    SafeLog("Performing hard hardware reset.", LogLevel.Warning);
                    _config.HardResetAttempted = true;
                    _config.HardResetSuccess = false;

                    try
                    {
                        _config.HardResetSuccess = await HardwareReset("hard");

                        bool proceed;
                        lock (_resetLock)
                        {
                            proceed = _config.HardResetSuccess && !_config.Rebooting;
                        }

                        if (proceed)
                        {
                            SafeLog("Hard reset completed successfully.", LogLevel.Info);
                            _GNS.SendGNS(GNSType.Message, "Hard reset completed successfully.");

                            StartPolling();
                            Thread.Sleep(60);
                            _ = Task.Run(ReplayLastCommand);
                            return;
                        }

                        SafeLog("Hard reset failed or rebooting still active.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Hard reset failed: {ex.Message}", LogLevel.Error);
                    }
                }

                if (_config.HardResetAttempted && !_config.HardResetSuccess)
                {
                    string failMessage = $"Unrecoverable failure after hard reset for {_lastIntent}";
                    SafeLog(failMessage, LogLevel.Error);
                    _GNS.SendGNS(GNSType.Alarm, failMessage);
                    TryMQTTPublish(_mqttAlarm, failMessage);
                }
            }
            catch (Exception ex)
            {
                string exMessage = $"ResetRoutine encountered unexpected error: {ex.Message}";
                SafeLog(exMessage, LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, exMessage);
                TryMQTTPublish(_mqttAlarm, exMessage);
            }
            finally
            {
                lock (_resetLock)
                {
                    _config.Resetting = false;
                }
            }
        }

        public async Task<bool> HardwareReset(string type)
        {
            try
            {
                SafeLog($"{type} reset initiated.", LogLevel.Warning);
                _GNS.SendGNS(GNSType.Stop, $"{type} reset initiated.");

                lock (_resetLock)
                {
                    _config.Rebooting = true;
                }

                _actionWatchdog = null;
                _config.ControllerReady = false;

                bool success = type switch
                {
                    "soft" => await PerformSoftReset(),
                    "hard" => await PerformHardReset(),
                    _ => false
                };

                lock (_resetLock)
                {
                    _config.Rebooting = false;
                }

                if (success)
                {
                    SafeLog($"{type} Reset completed successfully.", LogLevel.Info);
                }
                else
                {
                    SafeLog($"{type} Reset failed.", LogLevel.Warning);
                }

                return success;
            }
            catch (Exception ex)
            {
                SafeLog($"{type} Reset error: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private async Task<bool> PerformSoftReset()
        {
            _config.SoftResetAttempted = true;
            _config.HardResetAttempted = false;

            SendAndVerify("RESTART", ResponseMode.Blind);
            SafeLog("RESTART command sent (blind). Checking for reboot via shutter status...", LogLevel.Info);

            await Task.Delay(_config.pollingIntervalMs * 2);
            SystemStatus();
            int initialStatus = _config.ShutterStatus;

            if (initialStatus >= 0 && initialStatus <= 3)
            {
                SafeLog($"Soft reset likely ignored — shutter status is {initialStatus}.", LogLevel.Debug);
                return false;
            }
            else if (initialStatus == 6)
            {
                SafeLog("Shutter status is 6 — reboot likely in progress. Waiting for recovery...", LogLevel.Error);
                bool ready = await WaitForShutterReady();
                return ready;
            }
            else
            {
                // general failure
                SafeLog("Shutter status has thrown an error", LogLevel.Error);
                return false;
            }
        }

        private async Task<bool> PerformHardReset()
        {
            _config.HardResetAttempted = true;

            string exePath = _config.ResetExe;
            string offParams = _config.ResetOffParameters;
            string onParams = _config.ResetOnParameters;

            if (!await ResetDisconnection("hard")) return false;
            if (!LaunchResetProcess(exePath, offParams, "OFF")) return false;

            SafeLog($"Waiting {_config.CycleDelay}ms for controller to cycle...", LogLevel.Info);
            await Task.Delay(_config.CycleDelay);

            if (!LaunchResetProcess(exePath, onParams, "ON")) return false;

            SafeLog($"Waiting {_config.ResetDelay}ms for controller to come online...", LogLevel.Info);
            await Task.Delay(_config.ResetDelay);

            SafeLog($"Hard Reset: attempting reconnection after reset...", LogLevel.Info);

            bool connected = ConnectController();
            SafeLog($"Hard Reset: ConnectController returned {connected} after reset.", LogLevel.Debug);

            if (!connected)
            {
                SafeLog($"Hard Reset: failed to connect to controller.", LogLevel.Error);
                return false;
            }

            bool baseConnected = PingController(); // sync call
            SafeLog($"Hard Reset: PingController returned {baseConnected}.", LogLevel.Debug);

            if (!baseConnected)
            {
                SafeLog($"Hard Reset: base controller did not respond.", LogLevel.Error);
                return false;
            }

            SafeLog($"Hard Reset: base controller responded. Checking shutter status...", LogLevel.Info);

            bool successfulReboot = await WaitForShutterReady();

            return successfulReboot;

        }

        private async Task<bool> WaitForShutterReady(int timeoutMs = 30000)
        {
            int elapsed = 0;
            int pollInterval = 1000;

            while (elapsed < timeoutMs)
            {
                SystemStatus(); // triggers a fresh V read
                int status = _config.ShutterStatus;

                if (status >= 0 && status <= 3)
                {
                    SafeLog($"Shutter ready (status={status}). Waiting {_config.shutterSettle / 1000}s before continuing...", LogLevel.Info);
                    await Task.Delay(_config.shutterSettle);
                    return true;
                }

                await Task.Delay(pollInterval);
                elapsed += pollInterval;
            }

            SafeLog("Shutter did not become ready within timeout window.", LogLevel.Warning);
            return false;
        }

        public async Task<bool> ResetDisconnection(string type)
        {
            // Disconnect device
            int maxRetries = _config._connectRetryCount;
            int retryDelayMs = _config._connectRetryDelay;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (DisconnectController(true))
                {
                    SafeLog($"Device disconnected for {type} reset.", LogLevel.Info);
                    return true;
                }
                SafeLog($"Attempt {attempt} failed. Retrying in {retryDelayMs}ms...", LogLevel.Error);
                await Task.Delay(retryDelayMs);
            }
            SafeLog($"Device disconnection failed during {type} reset.", LogLevel.Error);
            return false;
        }

        private bool LaunchResetProcess(string exePath, string parameters, string label)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                SafeLog($"Reset executable not found: '{exePath}'", LogLevel.Error);
                return false;
            }

            SafeLog($"Launching external reset [{label}]: {exePath} {parameters}", LogLevel.Warning);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = parameters,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        SafeLog($"Failed to start external reset process [{label}].", LogLevel.Error);
                        return false;
                    }

                    process.WaitForExit();

                    int exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        SafeLog($"External reset process [{label}] completed successfully (exit code 0).", LogLevel.Info);
                        return true;
                    }
                    else
                    {
                        SafeLog($"External reset process [{label}] exited with error code {exitCode}.", LogLevel.Error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Error running external reset [{label}]: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private void ReplayLastCommand()
        {

            SafeLog($"Replaying last intent after reset: {_lastIntent}", LogLevel.Info);

            if (_lastIntent == DomeCommandIntent.None)
                return;
            try
            {
                switch (_lastIntent)
                {
                    case DomeCommandIntent.CloseShutter:
                        CloseShutter();
                        break;
                    case DomeCommandIntent.OpenShutter:
                        OpenShutter();
                        break;
                    case DomeCommandIntent.GoHome:
                        FindHome();
                        break;
                    case DomeCommandIntent.Park:
                        Park();
                        break;
                    case DomeCommandIntent.SlewAzimuth:
                        SlewToAzimuth(_config.SlewAz);
                        break;
                    default:
                        SafeLog($"No replay logic defined for intent: {_lastIntent}", LogLevel.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Replay of intent {_lastIntent} failed: {ex.Message}", LogLevel.Error);
            }
        }

        #endregion

        #region MQTT

        // use this
        // if (_mqttPublisher != null && _mqttPublisher.IsConnected)
        // {
        // await _mqttPublisher.PublishAsync("dome/status", "Some message");
        // }
        // 
        // OR
        //
        // Task.Run(async () => await _mqttPublisher.PublishAsync("dome/status", "Some message");

        public async Task StartMQTTAsync()
        {
            SafeLog("StartMQTTAsync entered", LogLevel.Debug);

            try
            {
                _mqttPublisher = new MqttPublisher(_logger, _config);
                SafeLog("MqttPublisher instance created", LogLevel.Debug);

                SafeLog("Initializing MQTT connection...", LogLevel.Debug);
                await _mqttPublisher.InitializeAsync(_config.MQTTip, _config.MQTTport);

                await _mqttPublisher.WaitForConnectedAsync(); // ✅ Add this line

                SafeLog("MQTT connection confirmed", LogLevel.Info);

                string startupMessage = "Pulsar Ascom driver connected";
                SafeLog($"Publishing startup message: '{startupMessage}'", LogLevel.Debug);
                TryMQTTPublish(_mqttStatus, startupMessage);
                SafeLog("Startup message published", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                SafeLog($"StartDriverAsync failed: {ex}", LogLevel.Error);
            }
        }
        // Null-safe, non-blocking MQTT publish
        private void TryMQTTPublish(string topic, string message)
        {
            lock (_mqttLock)
            {
                if (_config.Rebooting) return;
            }

            if (_mqttPublisher != null && _mqttPublisher.IsConnected && !string.IsNullOrWhiteSpace(topic))
            {
                _ = _mqttPublisher.PublishAsync(topic, message);
            }
        }

        #endregion

        #region Ascom Helpers

        public string CommandString(string command, bool raw)
        {
            SafeLog($"CommandString: {command}, raw={raw}", LogLevel.Debug);
            return "OK";
        }

        public string? SendCommand(string command, bool expectResponse = true)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                SafeLog("SendCommand called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            if (_guard == null || !_guard.IsReady)
            {
                SafeLog("SerialPortGuard not ready — aborting send.", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port is not ready.");
            }

            SafeLog($"Dispatching command: {command} (ExpectResponse={expectResponse})", LogLevel.Trace);

            return _guard.Send(command, expectResponse);
        }

        public ResponseResult SendAndVerify(
            string command,
            ResponseMode mode,
            IEnumerable<string>? expectedResponses = null,
            RetryPolicy? retryPolicy = null)
        {
            retryPolicy ??= new RetryPolicy
            {
                MaxAttempts = _config.sendVerifyMaxRetries,
                DelayMs = 100,
                Log = SafeLog
                //ExponentialBackoff = false
            };

            string response = "";
            bool expectResponse = mode != ResponseMode.Blind;
            int repeatLoop = 0;

            return retryPolicy.Execute(() =>
            {
                response = SendCommand(command, expectResponse);
                SafeLog($"Loop count - {repeatLoop}", LogLevel.Debug);
                SafeLog($"Sent - {command} \t got - {response}", LogLevel.Debug);

                repeatLoop++;

                if (mode == ResponseMode.Blind)
                {
                    return new ResponseResult { Response = null, IsMatch = true, Command = command };
                }

                if (mode == ResponseMode.Raw)
                {
                    return new ResponseResult { Response = response, IsMatch = true, Command = command };
                }

                if ((mode == ResponseMode.MatchExact || mode == ResponseMode.MatchAny) && expectedResponses == null)
                {
                    SafeLog($"Expected responses not provided for mode {mode}.", LogLevel.Error);
                    return new ResponseResult { Response = response, IsMatch = false, Command = command };
                }

                if (mode == ResponseMode.MatchExact && expectedResponses?.Count() == 1)
                {
                    string expected = expectedResponses.First();
                    SafeLog($"Expected response was {expected}", LogLevel.Debug);

                    if (string.Equals(response?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        SafeLog("MatchExact succeeded — returning early", LogLevel.Debug);
                        return new ResponseResult { Response = response, IsMatch = true, Command = command };
                    }
                }

                if (mode == ResponseMode.MatchAny && expectedResponses != null)
                {
                    foreach (var expected in expectedResponses)
                    {
                        if (string.Equals(response?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            SafeLog($"Matched one of expected responses: {response}", LogLevel.Debug);
                            return new ResponseResult { Response = response, IsMatch = true, Command = command };
                        }
                    }
                }

                return new ResponseResult { Response = response, IsMatch = false, Command = command };

            }, result => result.IsMatch);
        }

        public void SetupDialog()
        {
            var profile = new Profile { DeviceType = "Dome" };
            using (var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "PPBA_DomeSettingsForm.log"), _config.DebugLog, _config.TraceLog))
            {
                var configForUI = new ConfigManager(profile, logger);
                using (var form = new SettingsForm(configForUI))
                {
                    form.ShowDialog();
                }
            }
        }

        #region Dispose / Shutdown

        ~DomeDriver()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            var failures = new List<string>();

            void SafeDispose(Action action, string name)
            {
                try { action(); }
                catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
            }

            SafeDispose(() => StopPolling(), "StopPolling");
            SafeDispose(() => StopWatchdog(), "StopWatchdog");
            SafeDispose(() => DisconnectMqttSafely(), "DisconnectMqttSafely");
            SafeDispose(() => StopAlarmMonitor(), "Stopping Watchdog alarm");

            SafeDispose(() =>
            {
                if (_guard != null)
                {
                    _guard.Dispose();
                    _guard = null;
                }
            }, "SerialPortGuard");

            SafeDispose(() =>
            {
                if (_port != null)
                {
                    if (_port.IsOpen)
                    {
                        _port.Close();
                    }
                    _port.Dispose();
                    _port = null;
                }
            }, "SerialPort");

            SafeDispose(() =>
            {
                lock (_pollingLock)
                {
                    _connected = false;
                }
            }, "PollingLock");

            if (disposing)
            {
                SafeDispose(() =>
                {
                    _systemWatchdogCts?.Dispose();
                    _systemWatchdogCts = null;
                }, "SystemWatchdogCts");

                SafeDispose(() =>
                {
                    _pollingCancel?.Dispose();
                    _pollingCancel = null;
                }, "PollingCancel");

                SafeDispose(() => SafeLog("Driver disposed.", LogLevel.Info), "Logger.Log");
                SafeDispose(() => _logger?.Dispose(), "Logger.Dispose");
            }

            if (failures.Any())
            {
                try
                {
                    SafeLog($"Disposal warnings: {string.Join("; ", failures)}", LogLevel.Warning);
                }
                catch { /* Don't let logging failure block disposal */ }
            }
        }

        private void DisconnectMqttSafely()
        {
            try
            {
                if (_mqttPublisher != null && _mqttPublisher.IsConnected)
                {
                    _mqttPublisher.DisconnectAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
            }
        }

        #endregion


        #endregion

        #region Ascom Metadata
        public string Name => _config._driverId;
        public string Description => _config._driverVersion;
        public string DriverInfo => _config._description;
        public string DriverVersion => _config._driverVersion;
        public short InterfaceVersion => 2;

        #endregion

        #region Ascom Capabilities

        public bool CanFindHome => true;
        public bool CanPark => true;
        public bool CanSetAzimuth => true;
        public bool CanSetAltitude => false;
        public bool CanSetShutter => true;
        public bool CanSyncAzimuth => false;
        public bool CanSetPark => true;
        public bool CanSlave => false;

        #endregion

        #region Ascom Status

        public double Altitude
        {
            get
            {
                SafeLog("Altitude was called but not supported", LogLevel.Debug);
                throw new MethodNotImplementedException("Altitude not supported");
            }
        }
        public double Azimuth
        {
            get
            {
                lock (_pollingLock)
                {
                    return _config.Azimuth;
                }
            }
        }
        public bool AtHome
        {
            get
            {
                lock (_pollingLock)
                {
                    return _config.HomeStatus;
                }
            }
        }
        public bool AtPark
        {
            get
            {
                lock (_pollingLock)
                {
                    return _config.ParkStatus;
                }
            }
        }
        public ShutterState ShutterStatus
        {
            get
            {
                lock (_pollingLock)
                {
                    return _config.ShutterStatus switch
                    {
                        0 => ShutterState.shutterOpen,
                        1 => ShutterState.shutterClosed,
                        2 => ShutterState.shutterOpening,
                        3 => ShutterState.shutterClosing,
                        4 => ShutterState.shutterError,
                        _ => ShutterState.shutterError // fallback for 5, 6, or anything unexpected
                    };
                }
            }
        }
        public bool Slaved
        {
            get => false;
            set
            {
                SafeLog("Slave was called but not supported", LogLevel.Debug);
                throw new MethodNotImplementedException("Slaved not supported");
            }
        }
        public bool Slewing
        {
            get
            {
                lock (_pollingLock)
                {
                    return _config.SlewingStatus;
                }
            }
        }

        #endregion

        #region Ascom Action helpers

        private void ExecuteDomeCommand(
string command,
string message,
int timeoutMs,
DomeCommandIntent intent,
Func<bool> alreadyAtTarget,
Action updateConfigBeforeWatchdog,
Func<ActionWatchdog.WatchdogResult> checkStatus,
string mqttTopic,
string mqttSuccess,
string mqttFail,
string actionLabel,
string longAction,
string? gnsOverride = null,
double? gnsTimeoutFactor = null)
        {
            PrepareCommandExecution(message, gnsOverride, gnsTimeoutFactor, timeoutMs, intent);

            try
            {
                if (!TrySendCommand(command, intent, alreadyAtTarget, actionLabel))
                    return;

                lock (_pollingLock)
                {
                    updateConfigBeforeWatchdog();
                }

                if (!WaitForControllerReady(actionLabel))
                    return;

                LaunchActionWatchdog(
                    timeout: TimeSpan.FromMilliseconds(timeoutMs),
                    action: actionLabel,
                    longAction: longAction,
                    checkStatus: checkStatus,
                    mqttTopic: mqttTopic,
                    mqttSuccess: mqttSuccess,
                    mqttFail: mqttFail
                );
            }
            catch (Exception ex)
            {
                SafeLog($"{actionLabel} command failed: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                StartPolling();
            }
        }


        private void PrepareCommandExecution(string message, string? gnsOverride, double? gnsTimeoutFactor, int timeoutMs, DomeCommandIntent intent)
        {
            _config.ForceBusy = true;
            _lastIntent = intent;

            if (gnsOverride != null || gnsTimeoutFactor != null)
            {
                int gnsTimeout = (int)Math.Round(timeoutMs / 1000 * (gnsTimeoutFactor ?? 3.5));
                _GNS.SendGNS(GNSType.New, gnsOverride ?? message, gnsTimeout);
            }

            bool pollingWasStopped = StopPolling();
            if (pollingWasStopped)
            {
                _pollingTask?.Wait(_config.pollingIntervalMs * 2);
                Thread.Sleep(60);
            }
        }

        private bool TrySendCommand(string command, DomeCommandIntent intent, Func<bool> alreadyAtTarget, string actionLabel)
        {
            if (alreadyAtTarget())
            {
                SafeLog("Already at target — skipping command and watchdog.", LogLevel.Info);
                _config.ForceBusy = false;
                StartPolling();
                return false;
            }

            if (!SendAndVerify(command, ResponseMode.MatchExact, new[] { _generalResponse }).IsMatch)
            {
                SafeLog($"{actionLabel} command failed: No match response.", LogLevel.Error);
                RaiseAlarmAndReset($"{actionLabel} command failed (no ACK).");
                return false;
            }

            return true;
        }

        private bool WaitForControllerReady(string actionLabel)
        {
            SafeLog($"[WaitForControllerReady] ControllerReady={_config.ControllerReady} at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);
            SafeLog($"[WaitForControllerReady] Entered at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Debug);
            int waitMs = 0;
            while (!_config.ControllerReady && waitMs < _config.controllerTimeout)
            {
                SafeLog($"[WaitForControllerReady] ControllerReady={_config.ControllerReady}, waited={waitMs} ms", LogLevel.Trace);
                Thread.Sleep(_config.pollingIntervalMs);
                waitMs += _config.pollingIntervalMs;
            }

            if (!_config.ControllerReady)
            {
                SafeLog($"[WaitForControllerReady] Timeout reached — ControllerReady still false", LogLevel.Warning);
                SafeLog("Controller not ready after wait — watchdog launch aborted.", LogLevel.Warning);
                RaiseAlarmAndReset($"Controller not ready after {actionLabel}; initiating recovery.");
                return false;
            }

            return true;
        }

        private void LaunchActionWatchdog(
    TimeSpan timeout,
    string action,
    string longAction,
    Func<ActionWatchdog.WatchdogResult> checkStatus,
    string mqttTopic,
    string mqttSuccess,
    string mqttFail)
        {
            var cts = new CancellationTokenSource();

            try { _actionWatchdogCts?.Dispose(); } catch { }
            _actionWatchdogCts = cts;

            int generation = Interlocked.Increment(ref _watchdogGeneration);

            var watchdog = new ActionWatchdog(
                config: _config,
                timeout: timeout,
                action: action,
                longAction: longAction,
                checkStatus: checkStatus,
                internalCts: cts,
                resetRoutine: async () => await ResetRoutineAsync(),
                mqttPublisher: _mqttPublisher,
                mqttTopic: mqttTopic,
                mqttSuccess: mqttSuccess,
                mqttFail: mqttFail
            );

            _actionWatchdog = watchdog;

            Task.Run(async () =>
            {
                try
                {
                    await watchdog.Start();
                }
                catch (Exception ex)
                {
                    SafeLog($"Watchdog error ({action}): {ex.Message}", LogLevel.Error);
                }
                finally
                {
                    if (_watchdogGeneration == generation)
                    {
                        _actionWatchdog = null;
                        if (_actionWatchdogCts == cts)
                        {
                            try { _actionWatchdogCts?.Dispose(); } catch { }
                            _actionWatchdogCts = null;
                        }
                    }
                }
            });
        }

        // Centralize: raise alarm via GNS + MQTT and kick off reset
        private void RaiseAlarmAndReset(string alarmMessage)
        {
            _GNS.SendGNS(GNSType.Alarm, alarmMessage);
            TryMQTTPublish(_mqttAlarm, alarmMessage);
            SafeLog($"[ALARM] {alarmMessage}", LogLevel.Error);
            _config.ForceBusy = true;
            _ = Task.Run(async () => await ResetRoutineAsync());
        }

        #endregion

        #region Ascom Actions

        public void OpenShutter()
        {
            string message = "Opening shutter...";
            LogShutter(message);

            ExecuteDomeCommand(
                command: "OPEN",
                message: message,
                timeoutMs: _config.ShutterTimeout,
                intent: DomeCommandIntent.OpenShutter,
                alreadyAtTarget: () => _config.ShutterStatus == 0,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.ShutterStatus = 1; // opening
                    _config.SlewingStatus = true;
                },
                checkStatus: () => _config.ShutterStatus switch
                {
                    0 => ActionWatchdog.WatchdogResult.Success,
                    4 => ActionWatchdog.WatchdogResult.Error,
                    5 => ActionWatchdog.WatchdogResult.Failure,
                    _ => ActionWatchdog.WatchdogResult.InProgress
                },
                mqttTopic: _mqttShutterStatus,
                mqttSuccess: "Shutter is open.",
                mqttFail: "Shutter failed to open.",
                actionLabel: "open shutter",
                longAction: "Shutter opening..."
            );
        }

        public void CloseShutter()
        {
            string message = "Closing shutter...";
            LogShutter(message);

            ExecuteDomeCommand(
                command: "CLOSE",
                message: message,
                timeoutMs: _config.ShutterTimeout,
                intent: DomeCommandIntent.CloseShutter,
                alreadyAtTarget: () => _config.ShutterStatus == 1,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.ShutterStatus = 3; // closing
                    _config.SlewingStatus = true;
                },
                checkStatus: () => _config.ShutterStatus switch
                {
                    1 => ActionWatchdog.WatchdogResult.Success,
                    4 => ActionWatchdog.WatchdogResult.Error,
                    5 => ActionWatchdog.WatchdogResult.Failure,
                    _ => ActionWatchdog.WatchdogResult.InProgress
                },
                mqttTopic: _mqttShutterStatus,
                mqttSuccess: "Shutter closed.",
                mqttFail: "Shutter failed to close.",
                actionLabel: "Close shutter",
                longAction: "Shutter closing..."
            );
        }

        public void SlewToAzimuth(double azimuth)
        {
            if (azimuth < 0 || azimuth >= 360)
                throw new InvalidValueException($"Invalid Azimuth request of {azimuth} — must be between 0 and less than 360 degrees.");

            changeAzimuth = Math.Abs(azimuth - _config.Azimuth);
            string message = $"Slewing to {azimuth}...";
            string command = $"ABS {azimuth}";
            LogDome(message);

            ExecuteDomeCommand(
                command: command,
                message: message,
                timeoutMs: _config.RotationTimeout,
                intent: DomeCommandIntent.SlewAzimuth,
                alreadyAtTarget: () => Math.Abs(_config.Azimuth - azimuth) < _config.AzimuthTolerance,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.DomeState = 1;
                    _config.SlewingStatus = true;
                },
                checkStatus: () =>
                {
                    if (_config.DomeState == 0 && Math.Abs(_config.Azimuth - azimuth) < _config.AzimuthTolerance)
                        return ActionWatchdog.WatchdogResult.Success;

                    if (_config.DomeState == 1 || Math.Abs(_config.Azimuth - azimuth) >= _config.AzimuthTolerance)
                        return ActionWatchdog.WatchdogResult.InProgress;

                    return ActionWatchdog.WatchdogResult.Failure;
                },
                mqttTopic: _mqttDomeStatus,
                mqttSuccess: $"Dome azimuth {azimuth}.",
                mqttFail: $"Dome failed to reach azimuth {azimuth}.",
                actionLabel: "goto azimuth",
                longAction: $"Slewing to {azimuth}...",
                gnsOverride: changeAzimuth > _config.JogSize ? message : null,
                gnsTimeoutFactor: changeAzimuth > _config.JogSize ? 2.5 : null
            );
        }

        public void FindHome()
        {
            string message = "Finding Home...";
            int timeoutMs = _config.RotationTimeout;
            LogDome(message);

            ExecuteDomeCommand(
                command: "GO H",
                message: message,
                timeoutMs: timeoutMs,
                intent: DomeCommandIntent.GoHome,
                alreadyAtTarget: () => _config.HomeStatus,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.DomeState = 9; // going home
                    _config.SlewingStatus = true;
                },
                checkStatus: () =>
                {
                    return _config.HomeStatus
                        ? ActionWatchdog.WatchdogResult.Success
                        : ActionWatchdog.WatchdogResult.InProgress;
                },
                mqttTopic: _mqttDomeStatus,
                mqttSuccess: "Dome homed.",
                mqttFail: "Dome failed to find home.",
                actionLabel: "home",
                longAction: "Finding home...",
                gnsOverride: message,
                gnsTimeoutFactor: 2.5
            );
        }

        public void Park()
        {
            string message = "Parking...";
            int timeoutMs = _config.RotationTimeout;
            LogDome(message);

            ExecuteDomeCommand(
                command: "GO P",
                message: message,
                timeoutMs: timeoutMs,
                intent: DomeCommandIntent.Park,
                alreadyAtTarget: () => _config.ParkStatus,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.DomeState = 1; // moving to target (park)
                    _config.SlewingStatus = true;
                },
                checkStatus: () =>
                {
                    return _config.ParkStatus
                        ? ActionWatchdog.WatchdogResult.Success
                        : ActionWatchdog.WatchdogResult.InProgress;
                },
                mqttTopic: _mqttDomeStatus,
                mqttSuccess: "Dome parked.",
                mqttFail: "Dome failed to park.",
                actionLabel: "Park",
                longAction: "Parking...",
                gnsOverride: message,
                gnsTimeoutFactor: 2.5
            );
        }

        public void AbortSlew()
        {
            if (_disposed) return;

            LogDomeStatus("Aborting all movement...", "Aborting all movement...");

            // Ensure abort is not allowed during reset or reboot
            lock (_actionLock)
            {
                if (_config.Resetting || _config.Rebooting)
                {
                    SafeLog("Abort ignored: reset/reboot in progress.", LogLevel.Warning);
                    return;
                }
            }

            StopPolling();
            Thread.Sleep(60); // settle serial

            try
            {
                const string toSend = "STOP";
                bool success = SendAndVerify(toSend, ResponseMode.MatchExact, new[] { _generalResponse }).IsMatch;

                if (success)
                {
                    lock (_pollingLock)
                    {
                        _config.SlewingStatus = false; // let poller confirm DomeState
                    }

                    CancelCurrentActionWatchdog();

                    _config.WatchdogRunning = false;
                    _lastIntent = DomeCommandIntent.None;

                    _GNS.SendGNS(GNSType.Cease, "Abort succeeded.");
                    TryMQTTPublish(_mqttDomeStatus, "Abort succeeded");
                }
                else
                {
                    SafeLog("Abort STOP failed (no ACK). Initiating recovery.", LogLevel.Error);
                    _GNS.SendGNS(GNSType.Alarm, "Abort failed (no ACK). Resetting controller.");
                    TryMQTTPublish(_mqttAlarm, "Alarm");

                    _config.ForceBusy = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ResetRoutineAsync();
                        }
                        catch (Exception ex)
                        {
                            SafeLog($"ResetRoutineAsync failed during abort recovery: {ex.Message}", LogLevel.Error);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Abort command error: {ex.Message}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Abort threw an exception. Resetting controller.");
                TryMQTTPublish(_mqttAlarm, "Alarm");

                _config.ForceBusy = true;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ResetRoutineAsync();
                    }
                    catch (Exception innerEx)
                    {
                        SafeLog($"ResetRoutineAsync failed during abort exception recovery: {innerEx.Message}", LogLevel.Error);
                    }
                });
            }
            finally
            {
                StartPolling(); // resume monitoring
            }
        }

        public void SyncToAzimuth(double azimuth)
        {
            SafeLog($"SyncToAzimuth called with altitude = {azimuth} — not supported.", LogLevel.Debug);
            throw new ASCOM.MethodNotImplementedException("SyncToAzimuth is not supported by this dome.");

        }

        public void Unpark()
        {
            SafeLog("Unpark called.", LogLevel.Debug);
            throw new ASCOM.MethodNotImplementedException("Unpark is not supported by this dome.");
        }

        public void SetPark()
        {
            SafeLog("SetPark called.", LogLevel.Debug);
            // Add logic to define current position as park position
        }

        public void SlewToAltitude(double altitude)
        {
            SafeLog($"SlewToAltitude {altitude} called — not supported.", LogLevel.Debug);
            throw new ASCOM.MethodNotImplementedException("SlewToAltitude is not supported by this dome.");
        }

        #endregion

        #region Action helpers

        // Fully cancel and clear any active action watchdog
        private void CancelCurrentActionWatchdog()
        {
            try { _actionWatchdog?.Stop(); } catch { }
            _actionWatchdog = null;

            try { _actionWatchdogCts?.Cancel(); } catch { }
            try { _actionWatchdogCts?.Dispose(); } catch { }
            _actionWatchdogCts = null;

            _config.WatchdogRunning = false;
        }

        // Convenience wrappers to keep status logs consistent
        private void LogDome(string message) => LogDomeStatus(message, "");

        private void LogShutter(string message) => LogDomeStatus("", message);

        #endregion

        #region Ascom registration

        private static void RegUnregASCOM(bool bRegister)
        {
            string ProgId = "Pulsar_DomeDriver";
            string Description = "Pulsar Dome Controller";
            using (var P = new ASCOM.Utilities.Profile())
            {
                P.DeviceType = "Dome";
                if (bRegister)
                {
                    P.Register(ProgId, Description);
                }
                else
                {
                    P.Unregister(ProgId);
                }
            }
        }

        [ComRegisterFunction]
        public static void RegisterASCOM(Type t)
        {
            RegUnregASCOM(true);

            try
            {
                using var profile = new ASCOM.Utilities.Profile { DeviceType = "Dome" };
                //var logger = new FileLogger("PulsarDome_Registration", _config.DebugLog, _config.TraceLog); // Or null if logging isn't needed here

                var config = new ConfigManager(profile);
                config.RegistryEntries();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Registry prepopulation failed: {ex.Message}");
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterASCOM(Type t)
        {
            RegUnregASCOM(false);
        }

        #endregion

        #region Unsupported actions

        public void CommandBlind(string command, bool raw)
        {
            throw new MethodNotImplementedException("CommandBlind");

        }

        public bool CommandBool(string command, bool raw)
        {
            throw new MethodNotImplementedException("CommandBool");
        }

        #endregion

        #region Additional actions

        public ArrayList SupportedActions => new ArrayList
{
    "Full Reset",
    "Soft Reset",
    "Hard Reset"

};
        public string Action(string actionName, string actionParameters = "")
        {
            SafeLog($"Action called: {actionName} with parameters: {actionParameters}", LogLevel.Info);

            if (actionName == "Full Reset")
            {
                Task.Run(async () => await ResetRoutineAsync());
                return "Reset initiated";
            }
            else if (actionName == "Soft Reset")
            {
                Task.Run(async () => await ResetRoutineAsync("soft"));
                return "Soft Reset executed";
            }
            else if (actionName == "Hard Reset")
            {
                Task.Run(async () => await ResetRoutineAsync("hard"));
                return "Hard Reset executed";
            }
            else
            {
                throw new ASCOM.ActionNotImplementedException($"Action '{actionName}' is not implemented.");
            }
        }

        #endregion

    }
}
