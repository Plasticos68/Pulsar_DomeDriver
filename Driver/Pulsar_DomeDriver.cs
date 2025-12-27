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

    /// <summary>
    /// ASCOM dome driver for the Pulsar controller. Coordinates serial I/O, polling,
    /// watchdogs, and optional MQTT/GNS notifications.
    /// </summary>
    public class DomeDriver : IDomeV2
    {
        #region Class setup
        // Core dependencies and lifetime-owned resources.
        private Profile _profile;
        private FileLogger _logger;
        private ConfigManager _config;
        private SerialPort _port;
        private SerialPortGuard? _guard;
        private EventHandler? _processExitHandler;
        private EventHandler? _domainUnloadHandler;

        // Connection state and protocol response tokens.
        private bool _connected = false;
        private readonly string _pingResponse = "Y519";
        private readonly string _generalResponse = "A";

        // Shared locks and gate used to coordinate multi-threaded work.
        private readonly object _resetLock = new();
        private readonly object _mqttLock = new();
        private readonly object _actionLock = new();
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private readonly object _watchdogLock = new object();
        private readonly object _connectionLock = new();

        // Polling loop state (periodic status reads over serial).
        private CancellationTokenSource _pollingCancel = null;
        private bool _pollingActive = false;
        private readonly object _pollingLock = new();
        private Task _pollingTask = null;
        private volatile bool _manualDisconnect = false;

        // Timestamp updated by polling loop for stall detection.
        private DateTime _lastPollTimestamp = DateTime.MinValue;

        // Watchdog tasks and heartbeat timing.
        private CancellationTokenSource? _systemWatchdogCts;
        private Task? _systemWatchdogTask;
        private readonly TimeSpan _systemWatchdogInterval = TimeSpan.FromSeconds(5);         // How often to check
        private readonly TimeSpan _pollingStallThreshold = TimeSpan.FromSeconds(10);
        private bool _rebooting = false;
        private int _watchdogGeneration = 0;
        private readonly object _alarmTimerLock = new();
        private CancellationTokenSource _alarmTimerCts;
        private Task _alarmTimerTask;
        private DateTime _lastWatchdogPing = DateTime.UtcNow;
        private DateTime _lastGNSHeartbeat = DateTime.MinValue;
        private readonly TimeSpan _alarmTimeout = TimeSpan.FromSeconds(30); // or 2× watchdog interval

        // Auto-reconnect loop state.
        private CancellationTokenSource? _autoReconnectCts;
        private Task? _autoReconnectTask;
        private readonly TimeSpan _autoReconnectInterval = TimeSpan.FromMinutes(1);
        private int _reconnectAttempts = 0;
        private readonly int _maxReconnectAttempts = 10;

        // Diagnostics counters for command success/failure and timing.
        private int _commandSuccessCount = 0;
        private int _commandFailureCount = 0;
        private DateTime _lastCommandTime = DateTime.MinValue;
        private TimeSpan _totalCommandTime = TimeSpan.Zero;

        // Cached status strings for change-driven logging/MQTT.
        private string oldDomeActivity;
        private string oldShutterActivity;
        private string domeCurrent;
        private string shutterCurrent;
        private string domeOutputStatus;
        private double changeAzimuth;

        // Action watchdog used for long-running operations (slew, shutter).
        private CancellationTokenSource? _actionWatchdogCts;
        private ActionWatchdog _actionWatchdog;

        // Last command intent and a grace period to keep Slewing true for fast moves.
        private volatile DomeCommandIntent _lastIntent = DomeCommandIntent.None;
        private long _slewGraceUntilTicks = 0;
        private volatile bool _homeBeforeParkActive = false;

        // MQTT topics and publisher for status/alarm messages.
        private MqttPublisher _mqttPublisher;
        string _mqttStatus = "Dome/DriverStatus";
        string _mqttDomeStatus = "Dome/Dome/Status";
        string _mqttShutterStatus = "Dome/Shutter/Status";
        string _mqttAlarm = "Dome/Alarm/Status";
        string _mqttAlarmMessage = "Dome/Alarm/Message";
        string _mqttWatchdog = "Dome/Watchdog";

        // GNS (Goodnight System) notifications.
        private GNS _GNS;

        // Disposal state.
        private volatile bool _disposed = false;

        // Alarm latch and MQTT gating to prevent spam.
        private volatile bool _alarmTriggered = false;
        private volatile bool _allowMqttAlarm = false;
        private readonly object _alarmLock = new();

        #endregion

        /// <summary>
        /// Initializes a new instance of the Pulsar Dome Driver.
        /// Sets up logging, configuration validation, serial port initialization, and monitoring systems.
        /// Sends GNS notification on successful initialization.
        /// </summary>
        public DomeDriver()
        {
            try
            {
                const string driverId = "Pulsar_DomeDriver";

                // setup temporary profile to read registry values
                var initialProfile = new Profile();
                initialProfile.DeviceType = "Dome";
                _profile = initialProfile;

                // Resolve and construct log directory
                string regLogPath = _profile.GetValue(driverId, "LogLocation", "");
                if (string.IsNullOrWhiteSpace(regLogPath))
                {
                    regLogPath = _profile.GetValue(driverId + ".LogLocation", "");
                }
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
                SafeLog($"Registry DebugLog = '{debugRaw}' ? parsed as {debugLog}", LogLevel.Debug);

                string traceRaw = _profile.GetValue(driverId, "TraceLog", "");
                bool traceLog = string.Equals(traceRaw, "true", StringComparison.OrdinalIgnoreCase);
                SafeLog($"Registry TraceLog = '{traceRaw}' ? parsed as {traceLog}", LogLevel.Debug);

                // Initialize logger, config and GNS
                _logger = new FileLogger(logPath, debugLog, traceLog);
                _config = new ConfigManager(_profile, _logger);
                _GNS = new GNS(_logger, _config);

                // Validate critical configuration
                ValidateConfiguration();

                // Proceed with driver setup
                InitializeSerialPort();
                _processExitHandler = (_, __) => Dispose();
                _domainUnloadHandler = (_, __) => Dispose();
                AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
                AppDomain.CurrentDomain.DomainUnload += _domainUnloadHandler;

                // Send startup GNS message
                _GNS.SendGNS(GNSType.Message, "Dome driver initialized successfully");
            }
            catch (Exception ex)
            {
                // Emergency logging if logger not initialized
                try { _logger?.Log($"Driver initialization failed: {ex}", LogLevel.Error); }
                catch { /* ignore */ }

                // Send GNS alarm even if initialization fails
                try { _GNS?.SendGNS(GNSType.Alarm, $"Dome driver initialization failed: {ex.Message}"); }
                catch { /* ignore */ }

                throw;
            }
        }

        private void ValidateConfiguration()
        {
            // Fail fast if any configured values fall outside known safe ranges.
            var issues = new List<string>();

            if (_config.pollingIntervalMs < 500 || _config.pollingIntervalMs > 10000)
                issues.Add($"Polling interval {_config.pollingIntervalMs}ms is out of safe range (500-10000ms)");

            if (_config.RotationTimeout < 10 || _config.RotationTimeout > 300)
                issues.Add($"Rotation timeout {_config.RotationTimeout}s is out of safe range (10-300s)");

            if (_config.ShutterTimeout < 10 || _config.ShutterTimeout > 300)
                issues.Add($"Shutter timeout {_config.ShutterTimeout}s is out of safe range (10-300s)");

            if (_config.sendVerifyMaxRetries < 1 || _config.sendVerifyMaxRetries > 10)
                issues.Add($"Send/verify retries {_config.sendVerifyMaxRetries} is out of safe range (1-10)");

            if (_config.SlewSettleMs < 0 || _config.SlewSettleMs > 60000)
                issues.Add($"Slew settle time {_config.SlewSettleMs}ms is out of safe range (0-60000ms)");

            if (issues.Any())
            {
                string message = $"Configuration validation failed: {string.Join("; ", issues)}";
                SafeLog(message, LogLevel.Error);
                _GNS.SendGNS(GNSType.Alarm, message);
                throw new System.InvalidOperationException(message);
            }

            SafeLog("Configuration validation passed", LogLevel.Info);
        }

        #region Connection

        private void InitializeSerialPort()
        {
            // Port name comes from the ASCOM profile; other settings are fixed to controller spec.
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
                    WriteTimeout = 500
                };
            }
        }

        /// <summary>
        /// Gets or sets the connection state of the dome driver.
        /// When set to true, attempts to connect to the dome controller.
        /// When set to false, disconnects and stops monitoring.
        /// </summary>
        public bool Connected
        {
            get => _connected;
            set
            {
                lock (_connectionLock)
                {
                    if (value)
                    {
                        // Transition to connected state: serial handshake, MQTT (optional), monitors, then polling.
                        bool alreadyConnected;
                        lock (_pollingLock)
                        {
                            alreadyConnected = _connected;
                        }

                        if (alreadyConnected)
                        {
                            SafeLog("Already connected. Skipping reinitialization.", LogLevel.Debug);
                            return;
                        }

                        _manualDisconnect = false;

                        bool success = ConnectController();

                        if (!success)
                            throw new ASCOM.NotConnectedException("Failed to connect to controller.");

                        bool mqttReady = false;
                        if (_config.UseMQTT)
                        {
                            try
                            {
                                //StartMQTTAsync().GetAwaiter().GetResult(); // block until MQTT is ready
                                mqttReady = Task.Run(async () => await StartMQTTAsync()).GetAwaiter().GetResult();
                                //TryMQTTPublish(_mqttStatus, "Driver connected");
                            }
                            catch (Exception ex)
                            {
                                SafeLog($"MQTT startup failed: {ex.Message}", LogLevel.Error);
                                mqttReady = false;
                            }

                            if (mqttReady)
                            {
                                ResetAlarmMonitor();
                            }
                            else
                            {
                                SafeLog("MQTT unavailable - watchdog/alarm will run without MQTT.", LogLevel.Warning);
                            }
                        }

                        StartSystemMonitors(startWatchdog: true);

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
                        DisconnectInternal(manual: true);
                    }
                }
            }
        }

        private void DisconnectInternal(bool manual)
        {
            // Manual disconnect disables auto-reconnect/alarm loops; auto disconnect keeps them for recovery.
            if (manual)
            {
                _manualDisconnect = true;
                StopAutoReconnect();
                StopAlarmMonitor();
                CancelCurrentActionWatchdog(suppressReset: true);
                _lastIntent = DomeCommandIntent.None;
            }

            StopWatchdog();

            Task pollingWaitTask;
            lock (_pollingLock)
            {
                pollingWaitTask = _pollingTask;
            }

            bool pollingStopped = StopPolling(force: true);

            if (pollingStopped && pollingWaitTask != null)
            {
                // Avoid closing the port while the polling task is still reading.
                bool isPollingTask = Task.CurrentId.HasValue && pollingWaitTask.Id == Task.CurrentId.Value;
                if (isPollingTask)
                {
                    SafeLog("Skipping polling task wait on polling thread.", LogLevel.Debug);
                }
                else if (!pollingWaitTask.Wait(3000)) // timeout in ms
                {
                    SafeLog("Polling task did not complete within timeout - continuing disconnect.", LogLevel.Warning);
                }
                else
                {
                    SafeLog("Polling task stopped successfully.", LogLevel.Debug);
                }

                Thread.Sleep(_config.serialSettle); // Optional settle delay
            }

            MqttPublisher publisher = null;
            lock (_mqttLock)
            {
                publisher = _mqttPublisher;
                _mqttPublisher = null;
            }

            if (publisher != null)
            {

                // Disconnect MQTT on a background task so we do not block the caller.
                Task.Run(async () =>
                {
                    try
                    {
                        if (publisher.IsConnected)
                        {
                            await publisher.PublishAsync(_mqttStatus, "Driver disconnected");
                            await publisher.DisconnectAsync();
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { publisher.Dispose(); } catch { }
                    }
                });

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

            Thread.Sleep(_config.initialPingDelay);

            // Controller should respond to a PULSAR ping; retry twice then clean up.
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

            _GNS.SendGNS(GNSType.Alarm, "Failed to connect to dome controller after multiple attempts");
            return false;
        }

        public bool PingController()
        {
            try
            {
                // "PULSAR" is a lightweight handshake to confirm the controller is responsive.
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
            // Reboot path: force-close the port and guard without waiting on polling.
            if (reboot)
            {
                try
                {
                    StopPolling(force: true);
                    StopWatchdog();

                    var guard = _guard;
                    _guard = null;

                    if (guard != null)
                    {
                        try { guard.Dispose(); }
                        catch (Exception ex)
                        {
                            SafeLog($"Error disposing SerialPortGuard: {ex.Message}", LogLevel.Error);
                        }
                    }

                    if (_port != null)
                    {
                        try
                        {
                            if (_port.IsOpen)
                                _port.Close();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception ex)
                        {
                            SafeLog($"Forced port close failed: {ex.Message}", LogLevel.Error);
                        }
                    }

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
                StopPolling(force: true);
                StopWatchdog();

                var guard = _guard;
                _guard = null;

                if (guard != null)
                {
                    try { guard.Dispose(); }
                    catch (Exception ex)
                    {
                        SafeLog($"Error disposing SerialPortGuard: {ex.Message}", LogLevel.Error);
                    }
                }

                if (_port != null)
                {
                    try
                    {
                        if (_port.IsOpen)
                        {
                            _port.Close();
                            SafeLog($"Serial port {_port.PortName} closed.", LogLevel.Info);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
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
            // Diagnostic dump to help investigate stalls or disconnects.
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

        // Polling keeps _config in sync with controller status and drives command completion.
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

        public void StartPolling()
        {
            SafeLog("StartPolling() invoked - launching polling loop", LogLevel.Info);

            // Ensure only one polling task runs at a time.
            Task? pollingWaitTask = null;
            int pollingWaitTimeoutMs = 0;

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
                    else
                    {
                        if (_pollingCancel != null && !_pollingCancel.IsCancellationRequested)
                        {
                            _pollingActive = true;
                            SafeLog("Polling task already running. Resuming without restart.", LogLevel.Debug);
                            return;
                        }

                        SafeLog("Polling task still running. Waiting for shutdown before restart.", LogLevel.Debug);
                        pollingWaitTask = _pollingTask;
                        pollingWaitTimeoutMs = _config.pollingIntervalMs * 2;
                    }
                }
            }

            if (pollingWaitTask != null)
            {
                try
                {
                    if (!pollingWaitTask.Wait(pollingWaitTimeoutMs))
                    {
                        SafeLog("Polling task did not exit within timeout - restart deferred.", LogLevel.Warning);
                        return;
                    }
                }
                catch (AggregateException ex)
                {
                    SafeLog($"Polling task wait failed: {ex.InnerException?.Message ?? ex.Message}", LogLevel.Warning);
                }

                lock (_pollingLock)
                {
                    if (_pollingTask == pollingWaitTask)
                    {
                        _pollingTask = null;
                    }
                }
            }

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
                    SafeLog("Polling task already running. Skipping restart.", LogLevel.Debug);
                    return;
                }

                SafeLog($"Starting polling with interval {_config.pollingIntervalMs} ms.", LogLevel.Debug);

                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = new CancellationTokenSource();

                _lastPollTimestamp = DateTime.UtcNow;

                try
                {
                    _pollingTask = Task.Run(() => PollLoopAsync(_pollingCancel.Token));
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
            return StopPolling(force: false);
        }

        private bool StopPolling(bool force)
        {
            lock (_pollingLock)
            {
                // Do not interrupt active command unless forced (serial safety).
                if (_config.CommandInProgress && !force)
                {
                    SafeLog("Polling stop deferred: command in progress.", LogLevel.Debug);
                    return false;
                }

                //***********************************************
                _pollingCancel?.Cancel();
                _pollingCancel?.Dispose();
                _pollingCancel = null;
                _pollingActive = false;
                // CommandInProgress is cleared by SerialPortGuard.
                //_config.Rebooting = false;
                //_config.Resetting = false;
                //***********************************************

                SafeLog("Polling cancelled.", LogLevel.Debug);
                return true;
            }
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            // Polling loop flow:
            // - wait for startup readiness (no command/reboot),
            // - read status at interval,
            // - mark action watchdog success when targets are reached,
            // - trigger recovery after repeated errors.
            if (_disposed) return;

            SafeLog("Polling loop heartbeat", LogLevel.Trace);
            int cycleCount = 0;
            int startupWaitMs = 0;

            try
            {
                // Wait for readiness if a command or reboot was active at launch.
                while (!_disposed && !token.IsCancellationRequested)
                {
                    bool commandInProgress;
                    bool rebooting;
                    int interval;
                    int controllerTimeout;

                    lock (_pollingLock)
                    {
                        commandInProgress = _config.CommandInProgress;
                        rebooting = _config.Rebooting;
                        interval = _config.pollingIntervalMs;
                        controllerTimeout = _config.controllerTimeout;
                    }

                    if (!commandInProgress && !rebooting)
                        break;

                    if (startupWaitMs == 0)
                    {
                        SafeLog($"Polling delayed: command={commandInProgress}, reboot={rebooting}. Waiting for readiness...", LogLevel.Debug);
                    }

                    await Task.Delay(interval, token);
                    startupWaitMs += interval;

                    if (startupWaitMs >= controllerTimeout)
                    {
                        SafeLog($"Polling startup timed out after {controllerTimeout}ms waiting for readiness. Exiting.", LogLevel.Warning);
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
                            SystemStatusInternal(token); // Safe outside lock
                            errorCount = 0;

                            // Action watchdog: mark success if polling shows target reached.
                            if (_lastIntent == DomeCommandIntent.GoHome && _config.HomeStatus)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.Park && _config.ParkStatus)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.OpenShutter && _config.ShutterStatus == 0)
                                _actionWatchdog?.MarkSuccess();
                            if (_lastIntent == DomeCommandIntent.CloseShutter && _config.ShutterStatus == 1)
                                _actionWatchdog?.MarkSuccess();
                            else if (_lastIntent == DomeCommandIntent.SlewAzimuth &&
                                _config.DomeState == 0 &&
                                AngularDistance(_config.Azimuth, _config.SlewAz) <= _config.AzimuthTolerance)
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
                lock (_pollingLock)
                {
                    _pollingActive = false;
                    if (_pollingTask?.Id == Task.CurrentId)
                        _pollingTask = null;
                }

                SafeLog($"Polling thread exited cleanly. Last cancellation state: {token.IsCancellationRequested}, disposed={_disposed}", LogLevel.Debug);
            }
        }

        private void HandleDriverFailure(Exception lastException, int errorCount = 0, string reason = "Unknown")
        {
            // Escalate persistent polling failures into an alarm and a controlled disconnect.
            //SafeLog($"[DriverFailure] Triggered by {reason}. Last error: {lastException.Message}", LogLevel.Error);
            AlarmOn(reason);
            //_mqttPublisher?.PublishAsync(_mqttAlarm, $"Alarm: {reason}");
            _actionWatchdog?.MarkFailure();

            _pollingActive = false;
            lock (_connectionLock)
            {
                DisconnectInternal(manual: false);
            }

            //OnDriverDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void StartSystemMonitors(bool startWatchdog = true)
        {
            // Start background safety/health monitors.
            if (startWatchdog)
            {
                StartWatchdog();
            }
            StartAutoReconnect();
        }

        private void StartWatchdog()
        {
            // Watchdog task handles heartbeats and detects stalled polling.
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

                    _config.SystemWatchdogRunning = true;

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
                                _lastWatchdogPing = DateTime.UtcNow;
                                // MQTT heartbeat and alarm pulse.
                                if (_config.UseMQTT)
                                {
                                    if (_mqttPublisher?.IsConnected == true)
                                    {
                                        string utcTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
                                        string heartbeat = $"Alive at {utcTime}";
                                        TryMQTTPublish(_mqttWatchdog, $"{utcTime} :{domeOutputStatus}");
                                        AlarmHeartbeat();
                                    }
                                    else if (_mqttPublisher != null)
                                    {
                                        SafeLog("[Watchdog] MQTT not connected - heartbeat skipped", LogLevel.Warning);
                                    }
                                }

                                // GNS heartbeat every 5 minutes (300 seconds) when idle.
                                bool commandActive = _config.ForceBusy ||
                                    _config.ActionWatchdogRunning ||
                                    (_lastIntent != DomeCommandIntent.None && !IsCommandComplete());
                                if (!commandActive && (DateTime.UtcNow - _lastGNSHeartbeat).TotalSeconds >= 300)
                                {
                                    _GNS.SendGNS(GNSType.Message, BuildGnsStatusMessage());
                                    _lastGNSHeartbeat = DateTime.UtcNow;
                                }

                                // Polling stall detection: restart polling if it stops updating.
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
                        _config.SystemWatchdogRunning = false;
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

        private void StartAutoReconnect()
        {
            // Background loop that periodically tries to reconnect when idle.
            lock (_watchdogLock)
            {
                if (_disposed) return;

                try
                {
                    _autoReconnectCts?.Cancel();
                    _autoReconnectCts?.Dispose();
                }
                catch (Exception ex)
                {
                    SafeLog($"Error disposing previous auto-reconnect CTS: {ex.Message}", LogLevel.Warning);
                }

                _autoReconnectCts = new CancellationTokenSource();
                var token = _autoReconnectCts.Token;

                _autoReconnectTask = Task.Run(async () =>
                {
                    lock (_watchdogLock)
                    {
                        if (_disposed) return;
                    }

                    try { SafeLog("Auto-reconnect monitor started.", LogLevel.Debug); } catch { }

                    while (true)
                    {
                        lock (_watchdogLock)
                        {
                            if (_disposed || token.IsCancellationRequested)
                                break;
                        }

                        try
                        {
                            await Task.Delay(_autoReconnectInterval, token);
                        }
                        catch (TaskCanceledException)
                        {
                            SafeLog("Auto-reconnect cancelled.", LogLevel.Debug);
                            break;
                        }

                        if (_manualDisconnect || _config.Resetting || _config.Rebooting)
                        {
                            _reconnectAttempts = 0;
                            continue;
                        }

                        if (!_connected && _reconnectAttempts < _maxReconnectAttempts)
                        {
                            SafeLog("Attempting auto-reconnect to dome controller.", LogLevel.Info);
                            _reconnectAttempts++;

                            try
                            {
                                Connected = true;
                                if (_connected)
                                {
                                    SafeLog("Auto-reconnect successful.", LogLevel.Info);
                                    _GNS.SendGNS(GNSType.Message, "Dome driver auto-reconnected");
                                    _reconnectAttempts = 0;
                                }
                            }
                            catch (Exception ex)
                            {
                                SafeLog($"Auto-reconnect attempt {_reconnectAttempts} failed: {ex.Message}", LogLevel.Warning);
                                if (_reconnectAttempts >= _maxReconnectAttempts)
                                {
                                    SafeLog("Max auto-reconnect attempts reached. Entering safe mode.", LogLevel.Error);
                                    _GNS.SendGNS(GNSType.Alarm, "Dome driver max auto-reconnect attempts exceeded");
                                    EnterSafeMode();
                                }
                            }
                        }
                    }
                }, token);
            }
        }

        private void StopAutoReconnect()
        {
            if (_autoReconnectCts != null)
            {
                SafeLog("Stopping auto-reconnect monitor.", LogLevel.Debug);

                try
                {
                    _autoReconnectCts.Cancel();
                }
                catch (Exception ex)
                {
                    SafeLog($"Error cancelling auto-reconnect: {ex.Message}", LogLevel.Error);
                }
                _autoReconnectCts.Dispose();
                _autoReconnectCts = null;
            }
            _autoReconnectTask = null;
        }

        private void EnterSafeMode()
        {
            SafeLog("Entering safe mode - dome operations suspended.", LogLevel.Error);
            // Could implement safe positioning here if needed
            _GNS.SendGNS(GNSType.Alarm, "Dome driver entered safe mode");
        }

        private void StartAlarmMonitor()
        {
            // Separate timer that raises an alarm if watchdog heartbeats stop.
            lock (_alarmTimerLock)
            {
                _alarmTimerCts?.Cancel();
                _alarmTimerCts?.Dispose();
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
                _allowMqttAlarm = false;
                StopAlarmMonitor();
                TryMQTTPublish(_mqttAlarm, "Off");
                TryMQTTPublish(_mqttAlarmMessage, "");
            }
        }

        public void AlarmOn(string message = null)
        {
            lock (_alarmLock)
            {
                // Latching alarm: first trigger wins, later calls are ignored.
                if (_alarmTriggered)
                {
                    // Already triggered - do nothing
                    return;
                }

                _alarmTriggered = true;

                string alarmMessage = message ?? "Dome driver alarm triggered";
                // Allow MQTT alarm only after ResetAlarmMonitor enables it.
                if (!_allowMqttAlarm)
                {
                    SafeLog("Alarm triggered but MQTT alarm suppressed", LogLevel.Warning);
                    return;
                }

                _GNS.SendGNS(GNSType.Alarm, alarmMessage);
                TryMQTTPublish(_mqttAlarm, "On");
                SafeLog("Alarm triggered: status 'On' published", LogLevel.Warning);

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
                if (_alarmTriggered && _allowMqttAlarm)
                {
                    TryMQTTPublish(_mqttAlarm, "On");
                }
                else
                {
                    TryMQTTPublish(_mqttAlarm, "Off");
                }
            }
        }

        private void PublishFinalAlarm(string message)
        {
            lock (_alarmLock)
            {
                // Final alarm overrides MQTT suppression.
                _allowMqttAlarm = true;
                _alarmTriggered = true;
            }

            _GNS.SendGNS(GNSType.Alarm, message);
            TryMQTTPublish(_mqttAlarm, "On");
            if (!string.IsNullOrWhiteSpace(message))
            {
                TryMQTTPublish(_mqttAlarmMessage, message);
            }
        }


        #endregion

        #region Pulsar Dome specific methods

        public void SystemStatus()
        {
            SystemStatusInternal(CancellationToken.None);
        }

        private void SystemStatusInternal(CancellationToken token)
        {
            if (_guard == null || !_guard.IsReady)
            {
                SafeLog("[DomeStatus] Skipped because serial port not ready (likely disconnecting)", LogLevel.Debug);
                return;
            }

            // Poll a consistent snapshot of dome/home/park status with retries.
            var retryPolicy = new RetryPolicy
            {
                MaxAttempts = _config.statusMaxRetries,
                DelayMs = 100,
                ExponentialBackoff = false
            };

            bool didAnyStatus = false;

            try
            {
                retryPolicy.Execute(() =>
                {
                    if (token.IsCancellationRequested)
                        return true;

                    bool domeOk = ParseDomeStatus();
                    didAnyStatus = true;

                    if (token.IsCancellationRequested)
                        return true;

                    bool homeOk = ParseHomeStatus();

                    if (token.IsCancellationRequested)
                        return true;

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

                if (didAnyStatus)
                {
                    CompleteSystemStatus();
                }
            }
            catch (TimeoutException ex)
            {
                if (token.IsCancellationRequested)
                    return;

                SafeLog($"[DomeStatus] Status check failed after retries - raising alarm: {ex.Message}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Message, $"Dome status check failed after {_config.statusMaxRetries} retries");
                AlarmOn("Status check timeout");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    return;

                SafeLog($"[DomeStatus] Unexpected error during status check: {ex}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Message, $"Dome status check error: {ex.Message}");
                AlarmOn($"Status check error: {ex.Message}");
            }
        }

        public void CompleteSystemStatus()
        {
            // Translate raw state into readable status, publish MQTT, and resolve command completion.
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

                if (_homeBeforeParkActive)
                {
                    domeCurrent = "Parking (Homing)";
                }
                else if (_config.ParkStatus)
                {
                    domeCurrent = "Parked";
                }
                else if (_config.HomePark && _lastIntent == DomeCommandIntent.Park)
                {
                    domeCurrent = "Slewing";
                }
                else if (_config.HomeStatus)
                {
                    domeCurrent = "Home";
                }

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
                    if (completedIntent != DomeCommandIntent.None)
                    {
                        SetSlewGracePeriod();
                    }
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
            // "V" returns a multi-line, tab-delimited status block.
            string? raw = SendAndVerify("V", ResponseMode.Raw).Response;

            if (string.IsNullOrWhiteSpace(raw))
            {
                SafeLog("Empty or null dome response.", LogLevel.Error);
                return false;
            }

            // Validate response length to prevent buffer overflow attacks
            if (raw.Length > 1024)
            {
                SafeLog($"Dome response too long: {raw.Length} characters. Possible corruption.", LogLevel.Error);
                return false;
            }

            // Check for invalid characters (only allow printable ASCII and tabs)
            if (raw.Any(c => c < 32 && c != 9 && c != 10 && c != 13))
            {
                SafeLog("Dome response contains invalid control characters.", LogLevel.Error);
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
            // Tokens are controller status fields; parse and update _config under lock.
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
                    lock (_pollingLock)
                    {
                        _config.HomeStatus = true;
                    }
                    return true;
                }
                else if (trimmed == "0")
                {
                    lock (_pollingLock)
                    {
                        _config.HomeStatus = false;
                    }
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
                    lock (_pollingLock)
                    {
                        _config.ParkStatus = true;
                    }
                    return true;
                }
                else if (trimmed == "0")
                {
                    lock (_pollingLock)
                    {
                        _config.ParkStatus = false;
                    }
                    return true;
                }
            }

            SafeLog($"Unexpected PARK response: '{raw}'", LogLevel.Debug);
            return false;
        }

        public void SlewingStatus()
        {
            // Combine intent, reset state, grace period, and controller status into Slewing.
            if (_config.Resetting || _config.Rebooting)
            {
                _config.SlewingStatus = true;
                return;
            }

            if (_homeBeforeParkActive)
            {
                _config.SlewingStatus = true;
                return;
            }

            if (_lastIntent != DomeCommandIntent.None && !IsCommandComplete())
            {
                _config.SlewingStatus = true;
                return;
            }

            bool forceBusy = _config.ForceBusy || _config.ActionWatchdogRunning;

            if (forceBusy || IsSlewGraceActive())
            {
                _config.SlewingStatus = true;
                return;
            }

            bool isStationary;

            lock (_pollingLock)
            {
                isStationary = (_config.DomeState == 0 && (_config.ShutterStatus == 0 || _config.ShutterStatus == 1));
            }

            _config.SlewingStatus = !isStationary;
        }

        private void SetSlewGracePeriod()
        {
            // Keep Slewing true briefly after actions to let clients settle.
            int graceMs = _config.SlewSettleMs;
            if (graceMs <= 0)
                return;

            long untilTicks = DateTime.UtcNow.AddMilliseconds(graceMs).Ticks;
            Interlocked.Exchange(ref _slewGraceUntilTicks, untilTicks);
        }

        private void ClearSlewGracePeriod()
        {
            Interlocked.Exchange(ref _slewGraceUntilTicks, 0);
        }

        private bool IsSlewGraceActive()
        {
            long untilTicks = Interlocked.Read(ref _slewGraceUntilTicks);
            return untilTicks > 0 && DateTime.UtcNow.Ticks <= untilTicks;
        }

        private bool IsCommandComplete()
        {
            DomeCommandIntent intent = _lastIntent;
            bool homeStatus;
            bool parkStatus;
            int domeState;
            short shutterStatus;
            double azimuth;
            double slewAz;
            bool homeBeforeParkActive = _homeBeforeParkActive;

            lock (_pollingLock)
            {
                homeStatus = _config.HomeStatus;
                parkStatus = _config.ParkStatus;
                domeState = _config.DomeState;
                shutterStatus = _config.ShutterStatus;
                azimuth = _config.Azimuth;
                slewAz = _config.SlewAz;
            }

            return intent switch
            {
                DomeCommandIntent.GoHome => domeState == 0 && homeStatus,
                DomeCommandIntent.SlewAzimuth => domeState == 0 &&
                    AngularDistance(azimuth, slewAz) <= _config.AzimuthTolerance,
                DomeCommandIntent.OpenShutter => shutterStatus == 0,
                DomeCommandIntent.CloseShutter => shutterStatus == 1,
                DomeCommandIntent.Park => !homeBeforeParkActive && domeState == 0 && parkStatus,
                _ => true
            };
        }

        private static double NormalizeAzimuth(double azimuth)
        {
            double normalized = azimuth % 360;
            if (normalized < 0)
                normalized += 360;
            return normalized;
        }

        private static double AngularDistance(double a, double b)
        {
            double diff = Math.Abs(NormalizeAzimuth(a) - NormalizeAzimuth(b));
            return diff > 180 ? 360 - diff : diff;
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

        private string BuildGnsStatusMessage()
        {
            string domeLine = BuildDomeStatusLine();
            string shutterLine = BuildShutterStatusLine();
            return $"{domeLine}{Environment.NewLine}{shutterLine}";
        }

        private string BuildSlewStatusSummary()
        {
            double targetAz = _config.TargetAzimuth;
            if (_lastIntent == DomeCommandIntent.SlewAzimuth && !IsCommandComplete())
                targetAz = _config.SlewAz;

            double normalizedTarget = NormalizeAzimuth(targetAz);
            return $"Slewing to az {normalizedTarget:0}";
        }

        private string BuildDomeStatusLine()
        {
            if (_homeBeforeParkActive)
                return $"Dome = {BuildSlewStatusSummary()}";

            if (_config.ParkStatus)
                return "Dome = Parked";

            if (_config.HomeStatus)
                return "Dome = Home";

            if (_config.DomeState == 9)
                return "Dome = Finding home";

            if (_config.DomeState == 1)
            {
                return $"Dome = {BuildSlewStatusSummary()}";
            }

            return $"Dome = az {_config.Azimuth:0}";
        }

        private string BuildShutterStatusLine()
        {
            string shutterStatus = _config.ShutterStatus switch
            {
                0 => "Open",
                1 => "Closed",
                2 => "Opening",
                3 => "Closing",
                4 => "Error",
                5 => "Unknown",
                _ => "Invalid status"
            };

            return $"Shutter = {shutterStatus}";
        }

        public class RetryPolicy
        {
            // Simple retry helper used for serial reads and status polling.
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
            // Logging is best-effort; never throw from logging during shutdown.
            try { _logger?.Log(message, level); }
            catch { /* suppress logging errors during disposal */ }
        }

        #endregion

        #region Reset

        public async Task ResetRoutineAsync(string reset = "full")
        {
            // Reset flow: stop polling/watchdogs, attempt soft reset then hard reset, replay last command.
            lock (_resetLock)
            {
                if (_config.Resetting)
                {
                    SafeLog("Reset already in progress — skipping duplicate trigger.", LogLevel.Warning);
                    return;
                }

                _config.Resetting = true;
            }

            CancelCurrentActionWatchdog(suppressReset: true);

            TryMQTTPublish("Dome/Debug", "in reset");

            try
            {
                SafeLog("Requesting watchdog cancellation...", LogLevel.Warning);
                _systemWatchdogCts?.Cancel();

                var timeout = Task.Delay(5000);
                while (_config.SystemWatchdogRunning && !timeout.IsCompleted)
                {
                    SafeLog("Waiting for watchdog to exit...", LogLevel.Trace);
                    await Task.Delay(200);
                }

                if (_config.SystemWatchdogRunning)
                {
                    SafeLog("Watchdog did not exit after cancellation - aborting reset.", LogLevel.Error);
                    _GNS.SendGNS(GNSType.Message, "Reset aborted: watchdog failed to exit after cancellation.");
                    lock (_resetLock) { _config.Resetting = false; }
                    return;
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Unexpected error while waiting for watchdog: {ex.Message}", LogLevel.Error);
                _GNS.SendGNS(GNSType.Message, "Reset aborted due to watchdog coordination error.");
                lock (_resetLock) { _config.Resetting = false; }
                return;
            }

            bool softOnly = reset == "soft";
            bool hardOnly = reset == "hard";

            StopPolling(force: true);
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

                            RearmResetFlagsAfterSuccess();

                            lock (_pollingLock)
                            {
                                _config.SlewingStatus = false;
                                _config.DomeState = 0;
                            }

                            StartPolling();
                            Thread.Sleep(_config.serialSettle);
                            await Task.Delay(_config.serialSettle * 4);

                            ReplayLastCommand(allowDuringReset: true);
                            return;
                        }

                        SafeLog("Soft reset failed or rebooting still active.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Soft reset failed: {ex.Message}", LogLevel.Error);
                    }
                }

                int hardResetAttempts = 0;
                const int maxHardResetAttempts = 2;

                if (_config.HardReset || hardOnly)
                {
                    while (hardResetAttempts < maxHardResetAttempts)
                    {
                        hardResetAttempts++;
                        SafeLog($"Performing hard hardware reset (attempt {hardResetAttempts} of {maxHardResetAttempts}).", LogLevel.Warning);
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
                                bool replayOk = ReplayLastCommand(allowDuringReset: true);

                                if (replayOk)
                                {
                                    RearmResetFlagsAfterSuccess();
                                    return;
                                }

                                SafeLog($"Replay failed after hard reset attempt {hardResetAttempts}.", LogLevel.Warning);
                                if (hardResetAttempts < maxHardResetAttempts)
                                {
                                    StopPolling(force: true);
                                    continue;
                                }

                                string replayFail = $"Replay failed after second hard reset attempt for {_lastIntent}";
                                SafeLog(replayFail, LogLevel.Error);
                                PublishFinalAlarm(replayFail);
                                return;
                            }

                            SafeLog("Hard reset failed or rebooting still active.", LogLevel.Info);
                        }
                        catch (Exception ex)
                        {
                            SafeLog($"Hard reset failed: {ex.Message}", LogLevel.Error);
                        }
                    }
                }

                if (hardResetAttempts >= maxHardResetAttempts)
                {
                    string failMessage = $"Second hard reset failed for {_lastIntent}";
                    SafeLog(failMessage, LogLevel.Error);
                    PublishFinalAlarm(failMessage);
                }
            }
            catch (Exception ex)
            {
                string exMessage = $"ResetRoutine encountered unexpected error: {ex.Message}";
                SafeLog(exMessage, LogLevel.Error);
                _GNS.SendGNS(GNSType.Message, exMessage);
            }
            finally
            {
                lock (_resetLock)
                {
                    _config.Resetting = false;
                }
            }
        }

        private void RearmResetFlagsAfterSuccess()
        {
            // Allow future reset attempts after a successful recovery.
            _config.SoftResetAttempted = false;
            _config.HardResetAttempted = false;
        }

        public async Task<bool> HardwareReset(string type)
        {
            try
            {
                // Set Rebooting so other operations pause during the reset window.
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
            // Soft reset asks controller to restart and watches shutter status for reboot signal.
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
            // Hard reset uses external power control to cycle the controller, then reconnects.
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
            // Wait for shutter status to return to a valid operational range.
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
            // Force disconnect with retries so the port is released before power cycling.
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
            // Run external exe that toggles controller power (OFF/ON).
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

        private bool ReplayLastCommand(bool allowDuringReset)
        {
            // After a reset, re-issue the last intent to finish the user-requested action.
            SafeLog($"Replaying last intent after reset: {_lastIntent}", LogLevel.Info);

            if (_lastIntent == DomeCommandIntent.None)
                return true;
            try
            {
                switch (_lastIntent)
                {
                    case DomeCommandIntent.CloseShutter:
                        CloseShutterInternal(allowDuringReset);
                        return true;
                    case DomeCommandIntent.OpenShutter:
                        OpenShutterInternal(allowDuringReset);
                        return true;
                    case DomeCommandIntent.GoHome:
                        FindHomeInternal(allowDuringReset);
                        return true;
                    case DomeCommandIntent.Park:
                        ParkInternal(allowDuringReset);
                        return true;
                    case DomeCommandIntent.SlewAzimuth:
                        SlewToAzimuthInternal(_config.SlewAz, allowDuringReset);
                        return true;
                    default:
                        SafeLog($"No replay logic defined for intent: {_lastIntent}", LogLevel.Error);
                        return false;
                }
            }
            catch (Exception ex)
            {
                SafeLog($"Replay of intent {_lastIntent} failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Gets current driver metrics for monitoring and diagnostics.
        /// </summary>
        /// <returns>A dictionary containing metric names and values.</returns>
        public Dictionary<string, object> GetMetrics()
        {
            var metrics = new Dictionary<string, object>
            {
                ["CommandSuccessCount"] = _commandSuccessCount,
                ["CommandFailureCount"] = _commandFailureCount,
                ["TotalCommands"] = _commandSuccessCount + _commandFailureCount,
                ["SuccessRate"] = _commandSuccessCount + _commandFailureCount > 0 ?
                    (double)_commandSuccessCount / (_commandSuccessCount + _commandFailureCount) : 0.0,
                ["LastCommandTime"] = _lastCommandTime,
                ["AverageCommandTime"] = _commandSuccessCount > 0 ?
                    _totalCommandTime.TotalMilliseconds / _commandSuccessCount : 0.0,
                ["Connected"] = _connected,
                ["ReconnectAttempts"] = _reconnectAttempts,
                ["Azimuth"] = _config.Azimuth,
                ["ShutterStatus"] = _config.ShutterStatus,
                ["DomeState"] = _config.DomeState
            };
            return metrics;
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

        public async Task<bool> StartMQTTAsync()
        {
            SafeLog("StartMQTTAsync entered", LogLevel.Debug);

            try
            {
                // Create the publisher under lock to avoid races with disconnect.
                MqttPublisher publisher;
                lock (_mqttLock)
                {
                    if (_mqttPublisher != null)
                    {
                        try { _mqttPublisher.Dispose(); } catch { }
                        _mqttPublisher = null;
                    }

                    publisher = new MqttPublisher(_logger, _config);
                    _mqttPublisher = publisher;
                }
                SafeLog("MqttPublisher instance created", LogLevel.Debug);

                string mqttIp = _config.MQTTip;
                string mqttPort = _config.MQTTport;
                SafeLog($"Initializing MQTT connection to {mqttIp}:{mqttPort}", LogLevel.Info);
                await publisher.InitializeAsync(mqttIp, mqttPort);

                // Wait for connection with timeout
                var connectedTask = publisher.WaitForConnectedAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
                var completedTask = await Task.WhenAny(connectedTask, timeoutTask);
                
                if (completedTask == timeoutTask || publisher.IsConnected == false)
                {
                    SafeLog("MQTT connection timeout", LogLevel.Error);
                    try { publisher.Dispose(); } catch { }
                    lock (_mqttLock)
                    {
                        if (_mqttPublisher == publisher)
                            _mqttPublisher = null;
                    }
                    return false;
                }

                SafeLog("MQTT connection confirmed", LogLevel.Info);

                string startupMessage = "Pulsar Ascom driver connected";
                SafeLog($"Publishing startup message: '{startupMessage}'", LogLevel.Debug);
                TryMQTTPublish(_mqttStatus, startupMessage);
                SafeLog("Startup message published", LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                SafeLog($"StartMQTTAsync failed: {ex}", LogLevel.Error);
                lock (_mqttLock)
                {
                    try { _mqttPublisher?.Dispose(); } catch { }
                    _mqttPublisher = null;
                }
                return false;
            }
        }
        // Null-safe, non-blocking MQTT publish
        //private void TryMQTTPublish(string topic, string message)
        //{
        //    lock (_mqttLock)
        //    {
        //        if (_config.Rebooting) return;
        //    }

        //    if (_mqttPublisher != null && _mqttPublisher.IsConnected && !string.IsNullOrWhiteSpace(topic))
        //    {
        //        _ = _mqttPublisher.PublishAsync(topic, message);
        //    }
        //}

        // Null-safe, fire-and-forget publish to avoid blocking driver threads.
        private void TryMQTTPublish(string topic, string message)
        {
            MqttPublisher publisher;
            lock (_mqttLock)
            {
                if (_config.Rebooting)
                {
                    SafeLog($"[TryMQTTPublish] Skipped - Rebooting is true", LogLevel.Debug);
                    return;
                }

                publisher = _mqttPublisher;
            }

            if (publisher == null)
            {
                SafeLog($"[TryMQTTPublish] Skipped - publisher is null", LogLevel.Debug);
                return;
            }

            if (!publisher.IsConnected)
            {
                SafeLog($"[TryMQTTPublish] Skipped - not connected", LogLevel.Debug);
                return;
            }

            if (string.IsNullOrWhiteSpace(topic))
            {
                SafeLog($"[TryMQTTPublish] Skipped - topic is empty", LogLevel.Debug);
                return;
            }

            _ = PublishMqttSafely(publisher, topic, message);
        }

        private async Task PublishMqttSafely(MqttPublisher publisher, string topic, string message)
        {
            try
            {
                await publisher.PublishAsync(topic, message);
            }
            catch (ObjectDisposedException)
            {
                SafeLog("[TryMQTTPublish] Publisher disposed during publish.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                SafeLog($"[TryMQTTPublish] Publish failed: {ex.Message}", LogLevel.Debug);
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

            // SerialPortGuard manages exclusive access and tracks CommandInProgress.
            SafeLog($"Dispatching command: {command} (ExpectResponse={expectResponse})", LogLevel.Trace);

            return _guard.Send(command, expectResponse);
        }

        public ResponseResult SendAndVerify(
            string command,
            ResponseMode mode,
            IEnumerable<string>? expectedResponses = null,
            RetryPolicy? retryPolicy = null)
        {
            // Wrap SendCommand with retry + response matching, and update command metrics.
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
                var startTime = DateTime.UtcNow;
                response = SendCommand(command, expectResponse);
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Update metrics
                _totalCommandTime += duration;
                _lastCommandTime = endTime;

                SafeLog($"Loop count - {repeatLoop}", LogLevel.Debug);
                SafeLog($"Sent - {command} \t got - {response}", LogLevel.Debug);

                repeatLoop++;

                if (mode == ResponseMode.Blind)
                {
                    _commandSuccessCount++;
                    return new ResponseResult { Response = null, IsMatch = true, Command = command };
                }

                if (mode == ResponseMode.Raw)
                {
                    _commandSuccessCount++;
                    return new ResponseResult { Response = response, IsMatch = true, Command = command };
                }

                if ((mode == ResponseMode.MatchExact || mode == ResponseMode.MatchAny) && expectedResponses == null)
                {
                    SafeLog($"Expected responses not provided for mode {mode}.", LogLevel.Error);
                    _commandFailureCount++;
                    return new ResponseResult { Response = response, IsMatch = false, Command = command };
                }

                if (mode == ResponseMode.MatchExact && expectedResponses?.Count() == 1)
                {
                    string expected = expectedResponses.First();
                    SafeLog($"Expected response was {expected}", LogLevel.Debug);

                    if (string.Equals(response?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        SafeLog("MatchExact succeeded — returning early", LogLevel.Debug);
                        _commandSuccessCount++;
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
                            _commandSuccessCount++;
                            return new ResponseResult { Response = response, IsMatch = true, Command = command };
                        }
                    }
                }

                _commandFailureCount++;
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

            // Dispose tries hard to clean up all resources; failures are collected.
            var failures = new List<string>();

            void SafeDispose(Action action, string name)
            {
                try { action(); }
                catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
            }

            SafeDispose(() =>
            {
                if (_processExitHandler != null)
                {
                    AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
                    _processExitHandler = null;
                }
                if (_domainUnloadHandler != null)
                {
                    AppDomain.CurrentDomain.DomainUnload -= _domainUnloadHandler;
                    _domainUnloadHandler = null;
                }
            }, "AppDomainHandlers");

            SafeDispose(() => StopPolling(force: true), "StopPolling");
            SafeDispose(() => CancelCurrentActionWatchdog(suppressReset: true), "CancelCurrentActionWatchdog");
            SafeDispose(() => StopWatchdog(), "StopWatchdog");
            SafeDispose(() => StopAutoReconnect(), "StopAutoReconnect");
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

                SafeDispose(() => _commandGate.Dispose(), "CommandGate");

                SafeDispose(() => SafeLog("Driver disposed.", LogLevel.Info), "Logger.Log");
                SafeDispose(() => _GNS.SendGNS(GNSType.Message, "Dome driver disposed"), "GNS.Message");
                SafeDispose(() => _GNS?.Dispose(), "GNS.Dispose");
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
                MqttPublisher publisher = null;
                lock (_mqttLock)
                {
                    publisher = _mqttPublisher;
                    _mqttPublisher = null;
                }

                if (publisher == null)
                    return;

                if (publisher.IsConnected)
                {
                    publisher.DisconnectAsync().GetAwaiter().GetResult();
                }
                publisher.Dispose();
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
                // Slewing is derived from reset state, intent/grace period, and controller status.
                if (_config.Resetting || _config.Rebooting)
                    return true;

                if (_homeBeforeParkActive)
                    return true;

                if (_lastIntent != DomeCommandIntent.None && !IsCommandComplete())
                    return true;

                if (IsSlewGraceActive() || _config.ForceBusy || _config.ActionWatchdogRunning)
                    return true;

                lock (_pollingLock)
                {
                    return !(_config.DomeState == 0 && (_config.ShutterStatus == 0 || _config.ShutterStatus == 1));
                }
            }
        }

        #endregion

        #region Ascom Action helpers

        // Serialize action commands so only one uses the serial link at a time.
        private void EnterCommandGate(string actionLabel)
        {
            SafeLog($"Waiting for command gate: {actionLabel}", LogLevel.Debug);
            _commandGate.Wait();
            SafeLog($"Command gate acquired: {actionLabel}", LogLevel.Trace);
        }

        private void ExitCommandGate(string actionLabel)
        {
            try
            {
                _commandGate.Release();
                SafeLog($"Command gate released: {actionLabel}", LogLevel.Trace);
            }
            catch (SemaphoreFullException)
            {
                SafeLog($"Command gate release skipped for {actionLabel}.", LogLevel.Warning);
            }
            catch (ObjectDisposedException)
            {
                // Ignore during shutdown.
            }
        }

        private void EnsureReadyForCommand(string actionLabel, bool allowDuringReset)
        {
            // Guard against commands during dispose, reset, or while busy.
            if (_disposed)
                throw new ObjectDisposedException(nameof(DomeDriver));

            if (_config.Rebooting)
            {
                SafeLog($"{actionLabel} blocked: reboot in progress.", LogLevel.Warning);
                throw new System.InvalidOperationException($"{actionLabel} blocked: reboot in progress.");
            }

            if (_config.Resetting && !allowDuringReset)
            {
                SafeLog($"{actionLabel} blocked: reset/reboot in progress.", LogLevel.Warning);
                throw new System.InvalidOperationException($"{actionLabel} blocked: reset/reboot in progress.");
            }

            if (!allowDuringReset && Slewing)
            {
                SafeLog($"{actionLabel} blocked: driver busy.", LogLevel.Warning);
                throw new System.InvalidOperationException($"{actionLabel} blocked: driver busy.");
            }
        }

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
 double? gnsTimeoutFactor = null,
 bool allowDuringReset = false)
        {
            // Shared execution path for dome/shutter actions.
            EnterCommandGate(actionLabel);
            try
            {
                EnsureReadyForCommand(actionLabel, allowDuringReset);
                PrepareCommandExecution(message, gnsOverride, gnsTimeoutFactor, timeoutMs, intent);

                try
                {
                    if (!TrySendCommand(command, intent, alreadyAtTarget, actionLabel))
                        return;

                    lock (_pollingLock)
                    {
                        updateConfigBeforeWatchdog();
                    }
                    SetSlewGracePeriod();

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
            finally
            {
                ExitCommandGate(actionLabel);
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

            // Stop polling so command I/O has exclusive serial access.
            bool pollingWasStopped = StopPolling();
            if (!pollingWasStopped)
            {
                SafeLog("Polling stop deferred due to in-flight command. Forcing stop for command execution.", LogLevel.Debug);
                pollingWasStopped = StopPolling(force: true);
            }
            if (pollingWasStopped)
            {
                try
                {
                    if (_pollingTask != null)
                    {
                        bool isPollingTask = Task.CurrentId.HasValue && _pollingTask.Id == Task.CurrentId.Value;
                        if (isPollingTask)
                        {
                            SafeLog("Skipping polling task wait on polling thread.", LogLevel.Debug);
                        }
                        else
                        {
                            _pollingTask.Wait(_config.pollingIntervalMs * 2);
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    SafeLog($"Polling task wait error: {ex.InnerException?.Message ?? ex.Message}", LogLevel.Debug);
                }
                Thread.Sleep(60);
            }
        }

        private bool TrySendCommand(string command, DomeCommandIntent intent, Func<bool> alreadyAtTarget, string actionLabel)
        {
            // Skip sending if polling already reports we're at the target.
            bool atTarget = alreadyAtTarget();
            if (atTarget && intent != DomeCommandIntent.SlewAzimuth)
            {
                SafeLog("Already at target - skipping command and watchdog.", LogLevel.Info);
                _config.ForceBusy = false;
                StartPolling();
                return false;
            }
            if (atTarget && intent == DomeCommandIntent.SlewAzimuth)
            {
                SafeLog("Already at target but sending ABS anyway to avoid stale azimuth state.", LogLevel.Debug);
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
            // Poll until controller reports ready; trigger reset if timeout hits.
            SafeLog($"[WaitForControllerReady] ControllerReady={_config.ControllerReady} at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Trace);
            SafeLog($"[WaitForControllerReady] Entered at {DateTime.UtcNow:HH:mm:ss.fff}", LogLevel.Debug);
            int waitMs = 0;
            while (!_config.ControllerReady && waitMs < _config.controllerTimeout)
            {
                SafeLog($"[WaitForControllerReady] ControllerReady={_config.ControllerReady}, waited={waitMs} ms", LogLevel.Trace);
                try
                {
                    SystemStatus();
                }
                catch (Exception ex)
                {
                    SafeLog($"[WaitForControllerReady] Status refresh failed: {ex.Message}", LogLevel.Debug);
                }

                if (_config.ControllerReady)
                    break;

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
            // Per-action watchdog monitors long-running actions and triggers recovery on timeout.
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
            _GNS.SendGNS(GNSType.Message, alarmMessage);
            SafeLog($"[ALARM] {alarmMessage}", LogLevel.Error);
            _config.ForceBusy = true;
            _ = Task.Run(async () => await ResetRoutineAsync());
        }

        #endregion

        #region Ascom Actions

        public void OpenShutter()
        {
            OpenShutterInternal(allowDuringReset: false);
        }

        private void OpenShutterInternal(bool allowDuringReset)
        {
            string message = "Shutter opening...";
            int timeoutMs = _config.ShutterTimeout * 1000;
            double gnsTimeoutFactor = 600.0 / Math.Max(1.0, timeoutMs / 1000.0);
            LogShutter(message);

            ExecuteDomeCommand(
                command: "OPEN",
                message: message,
                timeoutMs: timeoutMs,
                intent: DomeCommandIntent.OpenShutter,
                alreadyAtTarget: () => _config.ShutterStatus == 0,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.ShutterStatus = 2; // opening
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
                longAction: "Shutter opening...",
                gnsOverride: message,
                gnsTimeoutFactor: gnsTimeoutFactor,
                allowDuringReset: allowDuringReset
            );
        }

        public void CloseShutter()
        {
            CloseShutterInternal(allowDuringReset: false);
        }

        private void CloseShutterInternal(bool allowDuringReset)
        {
            string message = "Closing shutter...";
            int timeoutMs = _config.ShutterTimeout * 1000;
            double gnsTimeoutFactor = 600.0 / Math.Max(1.0, timeoutMs / 1000.0);
            LogShutter(message);

            ExecuteDomeCommand(
                command: "CLOSE",
                message: message,
                timeoutMs: timeoutMs,
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
                longAction: "Shutter closing...",
                gnsOverride: message,
                gnsTimeoutFactor: gnsTimeoutFactor,
                allowDuringReset: allowDuringReset
            );
        }

        public void SlewToAzimuth(double azimuth)
        {
            SlewToAzimuthInternal(azimuth, allowDuringReset: false);
        }

        private void SlewToAzimuthInternal(double azimuth, bool allowDuringReset)
        {
            if (double.IsNaN(azimuth) || double.IsInfinity(azimuth) || azimuth < 0 || azimuth >= 360)
            {
                SafeLog($"Invalid azimuth request: {azimuth}", LogLevel.Warning);
                throw new InvalidValueException($"Invalid Azimuth request of {azimuth} - must be between 0 and less than 360 degrees.");
            }

            changeAzimuth = AngularDistance(azimuth, _config.Azimuth);
            string message = $"Slewing to {azimuth}...";
            string command = $"ABS {azimuth}";
            LogDome(message);

            lock (_pollingLock)
            {
                _config.SlewAz = azimuth;
            }

            ExecuteDomeCommand(
                command: command,
                message: message,
                timeoutMs: _config.RotationTimeout * 1000,
                intent: DomeCommandIntent.SlewAzimuth,
                alreadyAtTarget: () => AngularDistance(_config.Azimuth, azimuth) < _config.AzimuthTolerance,
                updateConfigBeforeWatchdog: () =>
                {
                    _config.DomeState = 1;
                    _config.SlewingStatus = true;
                },
                checkStatus: () =>
                {
                    if (_config.DomeState == 0 && AngularDistance(_config.Azimuth, azimuth) < _config.AzimuthTolerance)
                        return ActionWatchdog.WatchdogResult.Success;

                    if (_config.DomeState == 1 || AngularDistance(_config.Azimuth, azimuth) >= _config.AzimuthTolerance)
                        return ActionWatchdog.WatchdogResult.InProgress;

                    return ActionWatchdog.WatchdogResult.Failure;
                },
                mqttTopic: _mqttDomeStatus,
                mqttSuccess: $"Dome azimuth {azimuth}.",
                mqttFail: $"Dome failed to reach azimuth {azimuth}.",
                actionLabel: "goto azimuth",
                longAction: $"Slewing to {azimuth}...",
                gnsOverride: changeAzimuth > _config.JogSize ? message : null,
                gnsTimeoutFactor: changeAzimuth > _config.JogSize ? 2.5 : null,
                allowDuringReset: allowDuringReset
            );
        }

        public void FindHome()
        {
            FindHomeInternal(allowDuringReset: false);
        }

        private void FindHomeInternal(bool allowDuringReset)
        {
            string message = "Finding Home...";
            int timeoutMs = _config.RotationTimeout * 1000;
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
                gnsTimeoutFactor: 2.5,
                allowDuringReset: allowDuringReset
            );
        }

        public void Park()
        {
            ParkInternal(allowDuringReset: false);
        }

        private void ParkInternal(bool allowDuringReset)
        {
            string message = "Parking...";
            int timeoutMs = _config.RotationTimeout * 1000;
            LogDome(message);

            if (_config.HomePark)
            {
                ExecuteParkWithHome(message, timeoutMs, allowDuringReset);
                return;
            }

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
                gnsTimeoutFactor: 2.5,
                allowDuringReset: allowDuringReset
            );
        }

        private void ExecuteParkWithHome(string message, int timeoutMs, bool allowDuringReset)
        {
            const string actionLabel = "Park";

            EnterCommandGate(actionLabel);
            try
            {
                EnsureReadyForCommand(actionLabel, allowDuringReset);
                PrepareCommandExecution(message, message, 2.5, timeoutMs, DomeCommandIntent.Park);

                try
                {
                    if (_config.ParkStatus)
                    {
                        SafeLog("Already at target - skipping command and watchdog.", LogLevel.Info);
                        _config.ForceBusy = false;
                        return;
                    }

                    if (!_config.HomeStatus)
                    {
                        _homeBeforeParkActive = true;
                        SafeLog("Home-before-park enabled; sending HOME.", LogLevel.Debug);

                        if (!SendAndVerify("GO H", ResponseMode.MatchExact, new[] { _generalResponse }).IsMatch)
                        {
                            _homeBeforeParkActive = false;
                            SafeLog("Home-before-park command failed: No match response.", LogLevel.Error);
                            RaiseAlarmAndReset("Home-before-park command failed (no ACK).");
                            return;
                        }

                        lock (_pollingLock)
                        {
                            _config.DomeState = 1; // moving while homing for park
                            _config.SlewingStatus = true;
                        }
                        SetSlewGracePeriod();

                        StartPolling();

                        Task.Run(() => ContinueParkAfterHome(timeoutMs, allowDuringReset));
                        return;
                    }

                    if (!TrySendCommand("GO P", DomeCommandIntent.Park, () => _config.ParkStatus, actionLabel))
                        return;

                    lock (_pollingLock)
                    {
                        _config.DomeState = 1; // moving to target (park)
                        _config.SlewingStatus = true;
                    }
                    SetSlewGracePeriod();

                    if (!WaitForControllerReady(actionLabel))
                        return;

                    LaunchActionWatchdog(
                        timeout: TimeSpan.FromMilliseconds(timeoutMs),
                        action: actionLabel,
                        longAction: "Parking...",
                        checkStatus: () =>
                        {
                            return _config.ParkStatus
                                ? ActionWatchdog.WatchdogResult.Success
                                : ActionWatchdog.WatchdogResult.InProgress;
                        },
                        mqttTopic: _mqttDomeStatus,
                        mqttSuccess: "Dome parked.",
                        mqttFail: "Dome failed to park."
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
            finally
            {
                ExitCommandGate(actionLabel);
            }
        }

        private void ContinueParkAfterHome(int timeoutMs, bool allowDuringReset)
        {
            const string actionLabel = "Park";

            try
            {
                if (!WaitForHomeReached(timeoutMs))
                {
                    SafeLog("Home-before-park timed out.", LogLevel.Error);
                    RaiseAlarmAndReset("Home-before-park timed out.");
                    return;
                }

                StopPolling();
                Thread.Sleep(60);
            }
            finally
            {
                _homeBeforeParkActive = false;
            }

            EnterCommandGate(actionLabel);
            try
            {
                if (!TrySendCommand("GO P", DomeCommandIntent.Park, () => _config.ParkStatus, actionLabel))
                    return;

                lock (_pollingLock)
                {
                    _config.DomeState = 1; // moving to target (park)
                    _config.SlewingStatus = true;
                }
                SetSlewGracePeriod();

                if (!WaitForControllerReady(actionLabel))
                    return;

                LaunchActionWatchdog(
                    timeout: TimeSpan.FromMilliseconds(timeoutMs),
                    action: actionLabel,
                    longAction: "Parking...",
                    checkStatus: () =>
                    {
                        return _config.ParkStatus
                            ? ActionWatchdog.WatchdogResult.Success
                            : ActionWatchdog.WatchdogResult.InProgress;
                    },
                    mqttTopic: _mqttDomeStatus,
                    mqttSuccess: "Dome parked.",
                    mqttFail: "Dome failed to park."
                );
            }
            catch (Exception ex)
            {
                SafeLog($"{actionLabel} command failed: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                StartPolling();
                ExitCommandGate(actionLabel);
            }
        }

        private bool WaitForHomeReached(int timeoutMs)
        {
            int waitedMs = 0;
            while (!_disposed && !_config.HomeStatus && waitedMs < timeoutMs)
            {
                Thread.Sleep(_config.pollingIntervalMs);
                waitedMs += _config.pollingIntervalMs;
            }

            return _config.HomeStatus;
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
                    ClearSlewGracePeriod();

                    CancelCurrentActionWatchdog(suppressReset: true);

                    _config.ActionWatchdogRunning = false;
                    _lastIntent = DomeCommandIntent.None;

                    _GNS.SendGNS(GNSType.Cease, "Abort succeeded.");
                    TryMQTTPublish(_mqttDomeStatus, "Abort succeeded");
                }
                else
                {
                    SafeLog("Abort STOP failed (no ACK). Initiating recovery.", LogLevel.Error);
                    _GNS.SendGNS(GNSType.Message, "Abort failed (no ACK). Resetting controller.");

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
                _GNS.SendGNS(GNSType.Message, "Abort threw an exception. Resetting controller.");

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
        private void CancelCurrentActionWatchdog(bool suppressReset = false)
        {
            try { _actionWatchdog?.Cancel(suppressReset); } catch { }
            _actionWatchdog = null;

            try { _actionWatchdogCts?.Dispose(); } catch { }
            _actionWatchdogCts = null;

            _config.ActionWatchdogRunning = false;
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



