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
        private volatile bool _commandInProgress = false;
        private bool _rebooting = false;
        private int _watchdogGeneration = 0;

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
        string _mqttAlarm = "Dome/Alarm";
        string _mqttWatchdog = "Dome/Watchdog";

        // GNS
        private GNS _GNS;

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
                _logger?.Log($"Registry LogLocation = '{regLogPath}'", LogLevel.Debug);

                // Resolve debug flag
                string debugRaw = _profile.GetValue(driverId, "DebugLog", "");
                bool debugLog = string.Equals(debugRaw, "true", StringComparison.OrdinalIgnoreCase);
                _logger?.Log($"Registry DebugLog = '{debugRaw}' → parsed as {debugLog}", LogLevel.Debug);

                string traceRaw = _profile.GetValue(driverId, "TraceLog", "");
                bool traceLog = string.Equals(traceRaw, "true", StringComparison.OrdinalIgnoreCase);
                _logger?.Log($"Registry DebugLog = '{traceRaw}' → parsed as {traceLog}", LogLevel.Debug);

                // Initialize logger, config and GNS
                _logger = new FileLogger(logPath, LogLevel.Trace, debugLog, traceLog, _mqttPublisher);
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
                _logger?.Log($"Driver initialization failed: {ex}", LogLevel.Error);
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
                    ReadTimeout = 2000,
                    WriteTimeout = 2000
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
                        _logger?.Log("Already connected. Skipping reinitialization.", LogLevel.Debug);
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
                                StartWatchdog();
                            }
                            catch (Exception ex)
                            {
                                _logger?.Log($"MQTT startup failed: {ex.Message}", LogLevel.Error);
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
                            if (_pollingTask != null)
                            {
                                _pollingTask.Wait();      // Waits for polling task to finish (synchronously)
                                Thread.Sleep(_config.serialSettle);         // Optional buffer to let serial port settle
                            }
                            _logger?.Log("Polling task stopped successfully.", LogLevel.Debug);
                        }
                        if (_mqttPublisher != null && _mqttPublisher.IsConnected)
                        {
                            Task.Run(async () => await _mqttPublisher.PublishAsync(_mqttStatus, "Driver disconnected"));
                            Task.Run(async () => await _mqttPublisher.DisconnectAsync());
                            _logger?.Log("MQTT disconnected", LogLevel.Info);
                        }



                        lock (_pollingLock)
                        {
                            _connected = false;
                        }
                        _logger?.Log("Disconnect initiated.", LogLevel.Info);
                        var start = DateTime.UtcNow;

                        DisconnectController();

                        var elapsed = DateTime.UtcNow - start;
                        _logger?.Log($"Disconnect completed in {elapsed.TotalMilliseconds} ms.", LogLevel.Info);
                        _GNS.SendGNS(GNSType.Message, "Dome driver disconnected");
                    }
                }
            }
        }

        public bool ConnectController()
        {
            if (_port == null)
            {
                _logger?.Log("Serial port not initialized. Did InitializeSerialPort run?", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port is not initialized.");
            }

            if (string.IsNullOrWhiteSpace(_port.PortName))
            {
                _logger?.Log("Connection attempt but Serial Port name is missing.", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port name is missing. Please run Setup.");
            }

            if (!_port.IsOpen)
            {
                try
                {
                    _port.Open();
                    _logger?.Log($"Serial port {_port.PortName} opened.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Failed to open serial port {_port.PortName}: {ex.Message}", LogLevel.Error);
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
                            _commandInProgress = true;
                            _logger?.Log($"[Guard] _commandInProgress = true at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);
                        }
                    },
                    () =>
                    {
                        lock (_pollingLock)
                        {
                            _commandInProgress = false;
                            _logger?.Log($"[Guard] _commandInProgress = false at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);
                        }
                    }
                );
            }
            catch (Exception ex)
            {
                _logger?.Log($"Failed to create SerialPortGuard: {ex.Message}", LogLevel.Error);
                _port.Close();
                _guard = null;
                _logger?.Log("Serial port closed due to guard creation failure.", LogLevel.Info);
                return false;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_guard != null && PingController())
                {
                    _logger?.Log("Connected ok", LogLevel.Info);
                    return true;
                }

                Thread.Sleep(100); // between attempts

                if (attempt == 1)
                {
                    _logger?.Log("Ping failed after 2 attempts.", LogLevel.Error);

                    try
                    {
                        _port.Close();
                        _logger?.Log("Serial port closed after failed ping.", LogLevel.Debug);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log($"Error closing serial port: {ex.Message}", LogLevel.Error);
                    }

                    if (_guard != null)
                    {
                        try
                        {
                            _guard.Dispose();
                            _logger?.Log("SerialPortGuard disposed after failed ping.", LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Log($"Error disposing SerialPortGuard: {ex.Message}", LogLevel.Error);
                        }
                        _guard = null;
                    }

                    _logger?.Log("Driver disconnected.", LogLevel.Info);
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
                _logger?.Log($"PingController failed: {ex.Message}", LogLevel.Error);
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
                    _logger?.Log("Serial port forcibly closed.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Forced port close failed: {ex.Message}", LogLevel.Error);
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
                        _logger?.Log($"Serial port {_port.PortName} closed.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log($"Error closing serial port {_port.PortName}: {ex.Message}", LogLevel.Error);
                    }
                }
                _logger?.Log("Driver disconnected.");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Log($"DisconnectController failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private void LogConnectionSnapshot()
        {
            _logger?.Log("=== Dome Driver Connection Snapshot ===", LogLevel.Trace);

            _logger?.Log($"PollingActive: {_pollingActive}", LogLevel.Trace);
            _logger?.Log($"PollingTask status: {_pollingTask?.Status}", LogLevel.Trace);
            _logger?.Log($"CommandInProgress: {_commandInProgress}", LogLevel.Trace);
            _logger?.Log($"Rebooting: {_rebooting}", LogLevel.Trace);
            _logger?.Log($"ResetInProgress: {_config.Resetting}", LogLevel.Trace);
            _logger?.Log($"LastPollTimestamp: {_lastPollTimestamp:HH:mm:ss.fff}", LogLevel.Trace);
            _logger?.Log($"ShutterStatus: {_config.ShutterStatus}", LogLevel.Trace);
            _logger?.Log($"DomeState: {_config.DomeState}", LogLevel.Trace);
            _logger?.Log($"HomeStatus: {_config.HomeStatus}", LogLevel.Trace);
            _logger?.Log($"ParkStatus: {_config.ParkStatus}", LogLevel.Trace);
            _logger?.Log("=======================================", LogLevel.Trace);

        }

        #endregion

        #region Polling section

        public void SetPollingInterval(int milliseconds)
        {
            lock (_pollingLock)
            {
                if (milliseconds < 50 || milliseconds > 10000)
                {
                    _logger?.Log($"Polling interval {milliseconds} ms is out of bounds. Clamping to safe range.", LogLevel.Error);
                    milliseconds = Math.Max(50, Math.Min(milliseconds, 10000));
                }
                _config.pollingIntervalMs = milliseconds;
            }
        }

        public void StartPolling()
        {
            _logger?.Log("StartPolling() invoked — launching polling loop", LogLevel.Info);
            lock (_pollingLock)
            {
                if (_commandInProgress)
                {
                    _logger?.Log("Polling start blocked: command in progress.", LogLevel.Debug);
                    return;
                }

                if (_pollingActive)
                {
                    _logger?.Log("Polling already active. Skipping restart.", LogLevel.Debug);
                    return;
                }

                if (_pollingTask != null)
                {
                    _logger?.Log($"Polling task status: {_pollingTask.Status}", LogLevel.Trace);

                    if (_pollingTask.IsCompleted || _pollingTask.IsFaulted || _pollingTask.IsCanceled)
                    {
                        _logger?.Log($"Polling task is stale (status: {_pollingTask.Status}). Resetting.", LogLevel.Debug);
                        _pollingTask = null;
                    }
                }

                _logger?.Log($"Starting polling with interval {_config.pollingIntervalMs} ms.", LogLevel.Debug);

                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = new CancellationTokenSource();

                _lastPollTimestamp = DateTime.UtcNow;

                try
                {
                    _pollingTask = Task.Run(() => PollLoopAsync(_pollingCancel.Token));
                    _pollingActive = true;
                    //LogPollingStartupSnapshot();
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Failed to start polling task: {ex.Message}", LogLevel.Error);
                    _pollingActive = false;
                }
            }
        }

        public bool StopPolling()
        {
            lock (_pollingLock)
            {
                if (_commandInProgress)
                {
                    _logger?.Log("Polling stop deferred: command in progress.", LogLevel.Debug);
                    return false;
                }

                //***********************************************
                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = null;
                _pollingActive = false;
                _commandInProgress = false;
                //_config.Rebooting = false;
                //_config.Resetting = false;
                //***********************************************
                _pollingTask = null;

                _logger?.Log("Polling cancelled.", LogLevel.Debug);
                return true;
            }
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            _logger?.Log("Polling loop heartbeat", LogLevel.Trace);
            int cycleCount = 0;
            bool commandWasActive;
            bool rebootWasActive;

            int startupWaitMs = 0;
            while (true)
            {
                lock (_pollingLock)
                {
                    commandWasActive = _commandInProgress;
                    rebootWasActive = _config.Rebooting;
                }

                _logger?.Log($"[PollLoop] commandWasActive={commandWasActive}, rebootWasActive={rebootWasActive}, elapsed={startupWaitMs} ms", LogLevel.Trace);

                if (!commandWasActive && !rebootWasActive)
                    break;

                if (startupWaitMs == 0)
                {
                    _logger?.Log($"Polling delayed: command={commandWasActive}, reboot={rebootWasActive}. Waiting for readiness...", LogLevel.Debug);
                }

                await Task.Delay(_config.pollingIntervalMs, token);
                startupWaitMs += _config.pollingIntervalMs;

                if (startupWaitMs >= _config.controllerTimeout)
                {
                    _logger?.Log("Polling startup timed out after 10s waiting for readiness. Exiting.", LogLevel.Warning);
                    return;
                }
            }

            _logger?.Log("Polling loop started.", LogLevel.Debug);
            int errorCount = 0;

            try
            {
                while (true)
                {
                    if (token.IsCancellationRequested)
                    {
                        _logger?.Log("Polling cancellation requested.", LogLevel.Debug);
                        break;
                    }

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

                            _logger?.Log($"Polling cycle at {DateTime.UtcNow:HH:mm:ss}, interval {interval} ms.", LogLevel.Trace);
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            _logger?.Log($"Polling error #{errorCount}: {ex.Message}", LogLevel.Debug);

                            if (errorCount >= _config.pollingLoopRetries)
                            {
                                _logger?.Log($"Polling error threshold reached ({_config.pollingLoopRetries}). Invoking PollingLoopFailure().", LogLevel.Warning);
                                HandleDriverFailure(ex, errorCount);
                                break;
                            }

                            if (errorCount >= 3)
                            {
                                _logger?.Log("Throttling polling due to repeated errors.", LogLevel.Debug);
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
                        _logger?.Log("Polling delay cancelled.", LogLevel.Debug);
                        break;
                    }
                    catch (Exception ex)
                    {
                        HandleDriverFailure(ex);
                        _logger?.Log($"Polling delay error: {ex.Message}", LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Polling thread terminated unexpectedly: {ex.Message}", LogLevel.Debug);
            }
            finally
            {
                _logger?.Log($"Polling thread exited cleanly. Last cancellation state: {token.IsCancellationRequested}", LogLevel.Debug);
            }
        }

        private void HandleDriverFailure(Exception lastException, int errorCount = 0, string reason = "Unknown")
        {
            _logger?.Log($"[DriverFailure] Triggered by {reason}. Last error: {lastException.Message}", LogLevel.Error);

            _mqttPublisher?.PublishAsync(_mqttAlarm, $"Alarm: {reason}");
            _actionWatchdog?.MarkFailure();

            _pollingActive = false;
            Connected = false;

            //OnDriverDisconnected?.Invoke(this, EventArgs.Empty);
        }

        private void StartWatchdog()
        {
            _systemWatchdogCts?.Cancel();
            _systemWatchdogCts?.Dispose();
            _systemWatchdogCts = new CancellationTokenSource();
            var token = _systemWatchdogCts.Token;

            _systemWatchdogTask = Task.Run(async () =>
            {
                _logger?.Log("Polling watchdog started.", LogLevel.Debug);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_systemWatchdogInterval, token);

                        // 🧠 MQTT heartbeat
                        if (_mqttPublisher?.IsConnected == true)
                        {
                            string utcTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
                            string heartbeat = $"Alive at {utcTime}";
                            TryMQTTPublish(_mqttWatchdog, $"{utcTime} :{domeOutputStatus}");
                            //await _mqttPublisher.PublishAsync("dome/status", domeOutputStatus);
                            //_logger?.Log($"[Watchdog] Heartbeat published: {heartbeat}", LogLevel.Trace);
                        }
                        else
                        {
                            _logger?.Log("[Watchdog] MQTT not connected — heartbeat skipped", LogLevel.Warning);
                        }

                        // 🧠 Polling stall detection
                        var elapsed = DateTime.UtcNow - _lastPollTimestamp;
                        if (elapsed > _pollingStallThreshold)
                        {
                            _logger?.Log($"Polling watchdog triggered — last poll was {elapsed.TotalSeconds:F1}s ago. Restarting polling.", LogLevel.Debug);

                            StopPolling();
                            Thread.Sleep(50); // brief pause to ensure clean stop
                            StartPolling();
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        _logger?.Log("Polling watchdog cancelled.", LogLevel.Debug);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log($"Polling watchdog error: {ex.Message}", LogLevel.Error);
                    }
                }

                _logger?.Log("Polling watchdog exited.", LogLevel.Debug);
            }, token);
        }

        private void StopWatchdog()
        {
            if (_systemWatchdogCts != null)
            {
                _logger?.Log("Stopping polling watchdog.", LogLevel.Debug);

                try
                {
                    _systemWatchdogCts.Cancel();
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Error cancelling watchdog: {ex.Message}", LogLevel.Error);
                }
                _systemWatchdogCts.Dispose();
                _systemWatchdogCts = null;
            }
            _systemWatchdogTask = null;
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
                        _logger?.Log("[DomeStatus] Status check failed", LogLevel.Warning);
                        if (!domeOk) _logger?.Log("Dome status malformed", LogLevel.Debug);
                        if (!homeOk) _logger?.Log("Home status malformed", LogLevel.Debug);
                        if (!parkOk) _logger?.Log("Park status malformed", LogLevel.Debug);
                    }

                    return allOk;

                }, isSuccess => isSuccess);

                CompleteSystemStatus();
            }
            catch (TimeoutException)
            {
                _logger?.Log("[DomeStatus] Status check failed after retries — raising alarm", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Dome status failed after retries");
            }
        }

        public void CompleteSystemStatus()
        {
            _logger?.Log($"[DomeStatus] Invoked at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);

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
                        _logger?.Log($"ForceBusy overridden: command {_lastIntent} appears complete based on status.", LogLevel.Debug);
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
                _logger?.Log($"DomeStatus error: {ex.Message}", LogLevel.Debug);
            }
        }

        public bool ParseDomeStatus()
        {
            string? raw = SendAndVerify("V", ResponseMode.Raw).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger?.Log("Empty or null dome response.", LogLevel.Error);
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

            _logger?.Log($"Dome response had {lines.Length} lines. No valid status line found. Raw: '{raw}'", LogLevel.Debug);
            return false;
        }

        private bool TryParseDomeTokens(string[] tokens)
        {
            if (tokens.Length < 13)
            {
                _logger?.Log($"Insufficient token count: expected 13, got {tokens.Length}", LogLevel.Debug);
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
                _logger?.Log($"Target azimuth: raw={targetAzimuth}, normalized={normalizedTarget}", LogLevel.Trace);

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
                _logger?.Log($"Error parsing dome status: {ex.Message}", LogLevel.Debug);
                return false;
            }
        }

        public bool ParseHomeStatus()
        {
            string? raw = SendAndVerify("HOME ?", ResponseMode.MatchAny, new[] { "0", "1" }).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger?.Log("Empty HOME response.", LogLevel.Debug);
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

            _logger?.Log($"Unexpected HOME response: '{raw}'", LogLevel.Debug);
            return false;
        }

        public bool ParseParkStatus()
        {
            string? raw = SendAndVerify("PARK ?", ResponseMode.MatchAny, new[] { "0", "1" }).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger?.Log("Empty PARK response.", LogLevel.Debug);
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

            _logger?.Log($"Unexpected PARK response: '{raw}'", LogLevel.Debug);
            return false;
        }

        public void SlewingStatus()
        {
            // Slewing status
            if (_config.ForceBusy)
            {
                _config.SlewingStatus = true;
            }
            else
            {
                bool isStationary = (_config.DomeState == 0 && (_config.ShutterStatus == 0 || _config.ShutterStatus == 1));
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
                _logger?.Log($"Dome is: \t{domeActivity}", LogLevel.Info);
                oldDomeActivity = domeActivity;
            }

            if (shutterActivity != oldShutterActivity && shutterActivity != "")
            {
                _logger?.Log($"Shutter is: \t{shutterActivity}", LogLevel.Info);
                oldShutterActivity = shutterActivity;
            }
        }

        public class RetryPolicy
        {
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
                    var result = action();
                    if (isSuccess(result)) return result;

                    int delay = ExponentialBackoff ? DelayMs * (1 << attempt) : DelayMs;
                    Thread.Sleep(delay);
                }

                throw new TimeoutException($"RetryPolicy failed after {MaxAttempts} attempts.");
            }
        }

        #endregion

        #region Reset

        public async Task ResetRoutineAsync(string reset = "full")
        {
            TryMQTTPublish("Dome/Debug", "in reset");

            if (_config.Resetting)
            {
                _logger?.Log("Reset already in progress — skipping duplicate trigger.", LogLevel.Warning);
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                while (_config.WatchdogRunning)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        _logger?.Log("Watchdog stuck — forcing cleanup", LogLevel.Error);
                        CancelCurrentActionWatchdog();
                        _actionWatchdog?.Stop();
                        _actionWatchdog = null;
                        _config.WatchdogRunning = false;
                        break;
                    }

                    _logger?.Log("Waiting for existing watchdog to release...", LogLevel.Trace);
                    await Task.Delay(500, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected if delay is canceled mid-loop
                _logger?.Log("Watchdog wait canceled — forcing cleanup", LogLevel.Warning);
                CancelCurrentActionWatchdog();
                _actionWatchdog?.Stop();
                _actionWatchdog = null;
                _config.WatchdogRunning = false;
            }

            if (_config.WatchdogRunning)
            {
                _logger?.Log("Watchdog still running after forced stop — aborting reset.", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Reset aborted: watchdog failed to stop");
                return;
            }

            bool softOnly = reset == "soft";
            bool hardOnly = reset == "hard";

            StopPolling();
            _config.ControllerReady = false;
            _config.Resetting = true;

            string initialMessage = "Triggering ResetRoutine due to command error or stall.";
            _logger?.Log(initialMessage, LogLevel.Info);
            _GNS.SendGNS(GNSType.Message, initialMessage);

            lock (_pollingLock)
            {
                _config.SlewingStatus = true;
            }

            try
            {
                if ((_config.SoftReset || softOnly) && !_config.SoftResetAttempted)
                {
                    _logger?.Log("Performing soft reset.", LogLevel.Warning);
                    _config.SoftResetAttempted = true;
                    _config.SoftResetSuccess = false;
                    _config.HardResetAttempted = false;
                    _config.HardResetSuccess = false;

                    try
                    {
                        _config.SoftResetSuccess = await HardwareReset("soft");

                        if (_config.SoftResetSuccess && !_config.Rebooting)
                        {
                            string message = "Soft reset completed successfully.";
                            _logger?.Log(message, LogLevel.Info);
                            _GNS.SendGNS(GNSType.Message, message);

                            lock (_pollingLock)
                            {
                                _config.SlewingStatus = false;
                                _config.DomeState = 0;
                            }

                            StartPolling();
                            Thread.Sleep(_config.serialSettle); // settle time for serial
                            await Task.Delay(_config.serialSettle * 4);

                            _ = Task.Run(ReplayLastCommand);
                            return;
                        }

                        _logger?.Log("Soft reset failed or rebooting still active.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log($"Soft reset failed: {ex.Message}", LogLevel.Error);
                    }
                }

                if ((_config.HardReset || hardOnly) && !_config.HardResetAttempted)
                {
                    _logger?.Log("Performing hard hardware reset.", LogLevel.Warning);
                    _config.HardResetAttempted = true;
                    _config.HardResetSuccess = false;

                    try
                    {
                        _config.HardResetSuccess = await HardwareReset("hard");

                        if (_config.HardResetSuccess && !_config.Rebooting)
                        {
                            string message = "Hard reset completed successfully.";
                            _logger?.Log(message, LogLevel.Info);
                            _GNS.SendGNS(GNSType.Message, message);

                            StartPolling();
                            Thread.Sleep(60);
                            _ = Task.Run(ReplayLastCommand);
                            return;
                        }

                        _logger?.Log("Hard reset failed or rebooting still active.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log($"Hard reset failed: {ex.Message}", LogLevel.Error);
                    }
                }

                if (_config.HardResetAttempted && !_config.HardResetSuccess)
                {
                    string failMessage = $"Unrecoverable failure after hard reset for {_lastIntent}";
                    _logger?.Log(failMessage, LogLevel.Error);
                    _GNS.SendGNS(GNSType.Alarm, failMessage);
                    TryMQTTPublish(_mqttAlarm, failMessage);
                }
            }
            catch (Exception ex)
            {
                string exMessage = $"ResetRoutine encountered unexpected error: {ex.Message}";
                _logger?.Log(exMessage, LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, exMessage);
                TryMQTTPublish(_mqttAlarm, exMessage);
            }
            finally
            {
                _config.Resetting = false;
            }
        }

        public async Task<bool> HardwareReset(string type)
        {
            try
            {
                _logger?.Log($"{type} reset initiated.", LogLevel.Warning);
                _GNS.SendGNS(GNSType.Stop, $"{type} reset initiated.");

                lock (_pollingLock)
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

                _config.Rebooting = false;

                if (success)
                {
                    _logger?.Log($"{type} Reset completed successfully.", LogLevel.Info);
                }
                else
                {
                    _logger?.Log($"{type} Reset failed.", LogLevel.Warning);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.Log($"{type} Reset error: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private async Task<bool> PerformSoftReset()
        {
            _config.SoftResetAttempted = true;
            _config.HardResetAttempted = false;

            SendAndVerify("RESTART", ResponseMode.Blind);
            _logger?.Log("RESTART command sent (blind). Checking for reboot via shutter status...", LogLevel.Info);

            await Task.Delay(_config.pollingIntervalMs * 2);
            SystemStatus();
            int initialStatus = _config.ShutterStatus;

            if (initialStatus >= 0 && initialStatus <= 3)
            {
                _logger?.Log($"Soft reset likely ignored — shutter status is {initialStatus}.", LogLevel.Debug);
                return false;
            }
            else if (initialStatus == 6)
            {
                _logger?.Log("Shutter status is 6 — reboot likely in progress. Waiting for recovery...", LogLevel.Error);
                bool ready = await WaitForShutterReady();
                return ready;
            }
            else
            {
                // general failure
                _logger?.Log("Shutter status has thrown an error", LogLevel.Error);
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

            _logger?.Log($"Waiting {_config.CycleDelay}ms for controller to cycle...", LogLevel.Info);
            await Task.Delay(_config.CycleDelay);

            if (!LaunchResetProcess(exePath, onParams, "ON")) return false;

            _logger?.Log($"Waiting {_config.ResetDelay}ms for controller to come online...", LogLevel.Info);
            await Task.Delay(_config.ResetDelay);

            _logger?.Log($"Hard Reset: attempting reconnection after reset...", LogLevel.Info);

            bool connected = ConnectController();
            _logger?.Log($"Hard Reset: ConnectController returned {connected} after reset.", LogLevel.Debug);

            if (!connected)
            {
                _logger?.Log($"Hard Reset: failed to connect to controller.", LogLevel.Error);
                return false;
            }

            bool baseConnected = PingController(); // sync call
            _logger?.Log($"Hard Reset: PingController returned {baseConnected}.", LogLevel.Debug);

            if (!baseConnected)
            {
                _logger?.Log($"Hard Reset: base controller did not respond.", LogLevel.Error);
                return false;
            }

            _logger?.Log($"Hard Reset: base controller responded. Checking shutter status...", LogLevel.Info);

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
                    _logger?.Log($"Shutter ready (status={status}). Waiting {_config.shutterSettle / 1000}s before continuing...", LogLevel.Info);
                    await Task.Delay(_config.shutterSettle);
                    return true;
                }

                await Task.Delay(pollInterval);
                elapsed += pollInterval;
            }

            _logger?.Log("Shutter did not become ready within timeout window.", LogLevel.Warning);
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
                    _logger?.Log($"Device disconnected for {type} reset.", LogLevel.Info);
                    return true;
                }
                _logger?.Log($"Attempt {attempt} failed. Retrying in {retryDelayMs}ms...", LogLevel.Error);
                await Task.Delay(retryDelayMs);
            }
            _logger?.Log($"Device disconnection failed during {type} reset.", LogLevel.Error);
            return false;
        }

        private bool LaunchResetProcess(string exePath, string parameters, string label)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                _logger?.Log($"Reset executable not found: '{exePath}'", LogLevel.Error);
                return false;
            }

            _logger?.Log($"Launching external reset [{label}]: {exePath} {parameters}", LogLevel.Warning);

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
                        _logger?.Log($"Failed to start external reset process [{label}].", LogLevel.Error);
                        return false;
                    }

                    process.WaitForExit();

                    int exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        _logger?.Log($"External reset process [{label}] completed successfully (exit code 0).", LogLevel.Info);
                        return true;
                    }
                    else
                    {
                        _logger?.Log($"External reset process [{label}] exited with error code {exitCode}.", LogLevel.Error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Error running external reset [{label}]: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private void ReplayLastCommand()
        {

            _logger?.Log($"Replaying last intent after reset: {_lastIntent}", LogLevel.Info);

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
                        _logger?.Log($"No replay logic defined for intent: {_lastIntent}", LogLevel.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Replay of intent {_lastIntent} failed: {ex.Message}", LogLevel.Error);
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
            _logger?.Log("StartMQTTAsync entered", LogLevel.Debug);

            try
            {
                // Create MQTT publisher
                _mqttPublisher = new MqttPublisher(_logger, _config);
                _logger?.Log("MqttPublisher instance created", LogLevel.Debug);

                // Connect to broker
                _logger?.Log("Initializing MQTT connection...", LogLevel.Debug);
                await _mqttPublisher.InitializeAsync("10.17.1.92");
                _logger?.Log("MQTT connection established", LogLevel.Info);

                // Publish startup message
                string startupMessage = "Pulsar Ascom driver connected";
                _logger?.Log($"Publishing startup message: '{startupMessage}'", LogLevel.Debug);
                TryMQTTPublish(_mqttStatus, startupMessage);
                _logger?.Log("Startup message published", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _logger?.Log($"StartDriverAsync failed: {ex}", LogLevel.Error);
            }
        }

        // Null-safe, non-blocking MQTT publish
        private void TryMQTTPublish(string topic, string message)
        {
            if (_config.Rebooting) return;

            if (_mqttPublisher != null && _mqttPublisher.IsConnected && !string.IsNullOrWhiteSpace(topic))
            {
                _ = _mqttPublisher.PublishAsync(topic, message);
            }
        }

        #endregion

        #region Ascom Helpers

        public string CommandString(string command, bool raw)
        {
            _logger?.Log($"CommandString: {command}, raw={raw}", LogLevel.Debug);
            return "OK";
        }

        public string? SendCommand(string command, bool expectResponse = true)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                _logger?.Log("SendCommand called with empty or null command.", LogLevel.Error);
                throw new ArgumentException("Command cannot be null or empty.");
            }

            if (_guard == null || !_guard.IsReady)
            {
                _logger?.Log("SerialPortGuard not ready — aborting send.", LogLevel.Error);
                throw new ASCOM.NotConnectedException("Serial port is not ready.");
            }

            _logger?.Log($"Dispatching command: {command} (ExpectResponse={expectResponse})", LogLevel.Trace);

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
                ExponentialBackoff = false
            };

            string response = "";
            bool expectResponse = mode != ResponseMode.Blind;

            return retryPolicy.Execute(() =>
            {
                response = SendCommand(command, expectResponse);
                _logger?.Log($"Sent - {command} \t got - {response}", LogLevel.Debug);

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
                    _logger?.Log($"Expected responses not provided for mode {mode}.", LogLevel.Error);
                    return new ResponseResult { Response = response, IsMatch = false, Command = command };
                }

                if (mode == ResponseMode.MatchExact && expectedResponses?.Count() == 1)
                {
                    string expected = expectedResponses.First();
                    _logger?.Log($"Expected response was {expected}", LogLevel.Debug);

                    if (string.Equals(response?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Log("MatchExact succeeded — returning early", LogLevel.Debug);
                        return new ResponseResult { Response = response, IsMatch = true, Command = command };
                    }
                }

                if (mode == ResponseMode.MatchAny && expectedResponses != null)
                {
                    foreach (var expected in expectedResponses)
                    {
                        if (string.Equals(response?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.Log($"Matched one of expected responses: {response}", LogLevel.Debug);
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
            using (var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "PPBA_DomeSettingsForm.log"), LogLevel.Error))
            {
                var configForUI = new ConfigManager(profile, logger);
                using (var form = new SettingsForm(configForUI))
                {
                    form.ShowDialog();
                }
            }
        }

        #region Dispose / Shutdown

        private bool _disposed;

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

                SafeDispose(() => _logger?.Log("Driver disposed.", LogLevel.Info), "Logger.Log");

                SafeDispose(() => _logger?.Dispose(), "Logger.Dispose");
            }

            if (failures.Any())
            {
                try
                {
                    _logger?.Log($"Disposal warnings: {string.Join("; ", failures)}", LogLevel.Warning);
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
                _logger?.Log("Altitude was called but not supported", LogLevel.Debug);
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
                _logger?.Log("Slave was called but not supported", LogLevel.Debug);
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
                _logger?.Log($"{actionLabel} command failed: {ex.Message}", LogLevel.Error);
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
                _logger?.Log("Already at target — skipping command and watchdog.", LogLevel.Info);
                _config.ForceBusy = false;
                StartPolling();
                return false;
            }

            if (!SendAndVerify(command, ResponseMode.MatchExact, new[] { _generalResponse }).IsMatch)
            {
                _logger?.Log($"{actionLabel} command failed: No match response.", LogLevel.Error);
                RaiseAlarmAndReset($"{actionLabel} command failed (no ACK).");
                return false;
            }

            return true;
        }

        private bool WaitForControllerReady(string actionLabel)
        {
            _logger?.Log($"[WaitForControllerReady] ControllerReady={_config.ControllerReady} at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);
            _logger?.Log($"[WaitForControllerReady] Entered at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Debug);
            int waitMs = 0;
            while (!_config.ControllerReady && waitMs < _config.controllerTimeout)
            {
                _logger?.Log($"[WaitForControllerReady] ControllerReady={_config.ControllerReady}, waited={waitMs} ms", LogLevel.Trace);
                Thread.Sleep(_config.pollingIntervalMs);
                waitMs += _config.pollingIntervalMs;
            }

            if (!_config.ControllerReady)
            {
                _logger?.Log($"[WaitForControllerReady] Timeout reached — ControllerReady still false", LogLevel.Warning);
                _logger?.Log("Controller not ready after wait — watchdog launch aborted.", LogLevel.Warning);
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
                    _logger?.Log($"Watchdog error ({action}): {ex.Message}", LogLevel.Error);
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
            _logger?.Log($"[ALARM] {alarmMessage}", LogLevel.Error);
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
            LogDomeStatus("Aborting all movement...", "Aborting all movement...");

            if (_config.Resetting || _config.Rebooting)
            {
                _logger?.Log("Abort ignored: reset/reboot in progress.", LogLevel.Warning);
                return;
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

                    // Cancel any active watchdog
                    CancelCurrentActionWatchdog();

                    _config.WatchdogRunning = false;
                    _lastIntent = DomeCommandIntent.None;

                    _GNS.SendGNS(GNSType.Cease, "Abort succeeded.");
                    TryMQTTPublish(_mqttDomeStatus, "Abort succeeded");
                }
                else
                {
                    _logger?.Log("Abort STOP failed (no ACK). Initiating recovery.", LogLevel.Error);
                    _GNS.SendGNS(GNSType.Alarm, "Abort failed (no ACK). Resetting controller.");
                    TryMQTTPublish(_mqttAlarm, "Alarm");
                    _config.ForceBusy = true; // keep client seeing activity
                    _ = Task.Run(async () => await ResetRoutineAsync());
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Abort command error: {ex.Message}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, "Abort threw an exception. Resetting controller.");
                TryMQTTPublish(_mqttAlarm, "Alarm");
                _config.ForceBusy = true;
                _ = Task.Run(async () => await ResetRoutineAsync());
            }
            finally
            {
                StartPolling(); // resume monitoring
            }
        }

        public void SyncToAzimuth(double azimuth)
        {
            _logger?.Log($"SyncToAzimuth called with altitude = {azimuth} — not supported.", LogLevel.Debug);
            throw new ASCOM.MethodNotImplementedException("SyncToAzimuth is not supported by this dome.");

        }

        public void Unpark()
        {
            _logger?.Log("Unpark called.", LogLevel.Debug);
            throw new ASCOM.MethodNotImplementedException("Unpark is not supported by this dome.");
        }

        public void SetPark()
        {
            _logger?.Log("SetPark called.", LogLevel.Debug);
            // Add logic to define current position as park position
        }

        public void SlewToAltitude(double altitude)
        {
            _logger?.Log($"SlewToAltitude {altitude} called — not supported.", LogLevel.Debug);
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
                var logger = new FileLogger("PulsarDome_Registration", LogLevel.Error); // Or null if logging isn't needed here

                var config = new ConfigManager(profile, logger);
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
            _logger?.Log($"Action called: {actionName} with parameters: {actionParameters}", LogLevel.Info);

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
