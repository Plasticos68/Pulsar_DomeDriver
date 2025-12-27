using ASCOM.Utilities;
using Pulsar_DomeDriver.Diagnostics;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;

namespace Pulsar_DomeDriver.Config
{
    public enum LogLevel
    {
        None = 0,
        Error = 1,
        Warning = 2,
        Info = 3,
        Debug = 4,
        Trace = 5
    }

    public enum ResponseMode
    {
        Blind,          // 1: No response expected
        Raw,            // 2: Return raw response
        MatchExact,     // 3: Return true/false if response matches expected
        MatchAny        // 4: Return response if it matches one of the valid options
    }

    public enum DomeCommandIntent
    {
        None,
        CloseShutter,
        OpenShutter,
        GoHome,
        Park,
        SlewAzimuth,
    }

    public class ConfigManager

    {
        public readonly Profile _profile;
        private FileLogger _logger;
        public ConfigManager(Profile profile, FileLogger logger = null)
        {
            _profile = profile;
            _logger = logger;
        }

        private string GetProfileValue(string name, string defaultValue = "")
        {
            try
            {
                var value = _profile.GetValue(_driverId, name, "", defaultValue);
                return value ?? defaultValue;
            }
            catch (ASCOM.Utilities.Exceptions.DriverNotRegisteredException)
            {
                return defaultValue;
            }
        }

        private void WriteProfileValue(string name, string value)
        {
            _profile.WriteValue(_driverId, name, value, "");
        }

        public readonly string _driverId = "Pulsar_DomeDriver";
        public readonly string _description = "Pulsar Dome Driver with auto reset";
        public readonly string _driverInfo = "Pulsar DomeDriver";
        public readonly string _driverVersion = "1.0";
        public readonly int _connectRetryCount = 3;
        public readonly int _connectRetryDelay = 2000; // milliseconds
        public string serialPort = "";
        //public readonly string _mqttAlarm = "Dome/alarm";
        public int pollingIntervalMs = 2000;
        public readonly int watchDogSettle = 100;
        public readonly int shutterSettle = 5000;
        public readonly int serialSettle = 60;
        public readonly int initialPingDelay = 250;
        public readonly int controllerTimeout = 10000;
        public readonly int statusMaxRetries = 2;       // number of retries for the domestatus before alarm
        public readonly int sendVerifyMaxRetries = 5;       // number of retries for the send and verify routine
        public readonly int pollingLoopRetries = 5;
        public int SlewSettleMs { get; set; } = 4000;

        public double HomeAzimuth { get; private set; }
        public double ParkAzimuth { get; set; } = 0;
        public double Azimuth { get; set; }
        public double AzimuthTolerance { get; set; } = 0; // allowablw error for azimuth
        //public bool SlewingStatus { get; set; } = false;
        public bool ParkStatus { get; set; } // 0=Not Parked, 1=Parked
        public bool HomeStatus { get; set; } = false; // 0 = Not Homed, 1 = Homed
        public int DomeState { get; set; } // 0=Idle, 1=Moving to target, 9=Going home
        public double Rot { get; set; } // current rotation speed
        public double TargetAzimuth { get; set; } // target azimuth for slewing
        public int MotorDir { get; set; } // motor direction: 1=clockwise, -1=counterclockwise, 0=stopped   
        public short ShutterStatus { get; set; }  // 0 =Open, 1 =Closed, 2 =Opening, 3 =Closing
                                                  // 4 =Error(Most likely both limit switches are active)
                                                  // 5 =Unknown(Neither open or closed), 6 =Not Fitted
        public int ShutterPercentage { get; set; } // 0-100 percentage charge shutter battery
        public int ShutterVoltage { get; set; } // voltage of shutter battery
        public int ShutterCurrent { get; set; } // current of shutter battery
        public int EncoderValue { get; set; } // current encoder value
        public int Temp { get; set; } // temperature in Celsius
        public bool RelayState { get; set; } // state of the relay: 0=off, 1=on

        public enum ShutterTargetState
        {
            Open = 0,
            Closed = 1
        }

        public string SerialPort
        {
            get => GetProfileValue("SerialPort", "") ?? serialPort;
            set => WriteProfileValue("SerialPort", value);
        }

        public bool SoftReset
        {
            get
            {
                string resetString = GetProfileValue("InternalReset", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("InternalReset", value.ToString().ToLower());
            }
        }

        public bool HardReset
        {
            get
            {
                string resetString = GetProfileValue("ExternalReset", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("ExternalReset", value.ToString().ToLower());
            }
        }

        public string LogLocation
        {
            get => GetProfileValue("LogLocation", "") ?? "";
            set => WriteProfileValue("LogLocation", value);
        }

        public string ResetExe
        {
            get => GetProfileValue("ResetExe", "") ?? "";
            set => WriteProfileValue("ResetExe", value);
        }

        public string ResetOnParameters
        {
            get => GetProfileValue("ResetOnParameters", "") ?? "";
            set => WriteProfileValue("ResetOnParameters", value);
        }

        public string ResetOffParameters
        {
            get => GetProfileValue("ResetOffParameters", "") ?? "";
            set => WriteProfileValue("ResetOffParameters", value);
        }

        public int ResetDelay
        {
            get
            {
                string delayString = GetProfileValue("ResetDelay", "");
                bool success = int.TryParse(delayString, out int result);
                return success ? result * 1000: 5;
            }
            set
            {
                WriteProfileValue("ResetDelay", value.ToString());
            }
        }

        public int CycleDelay
        {
            get
            {
                string delayString = GetProfileValue("CycleDelay", "");
                bool success = int.TryParse(delayString, out int result);
                return success ? result * 1000: 5;
            }
            set
            {
                WriteProfileValue("CycleDelay", value.ToString());
            }
        }

        public int ShutterTimeout
        {
            get
            {
                string raw = GetProfileValue("ShutterTimeout", "90");
                if (string.IsNullOrWhiteSpace(raw))
                    raw = "90";
                if (!int.TryParse(raw, out int seconds) || seconds < 10 || seconds > 600)
                {
                    _logger?.Log($"Invalid ShutterTimeout '{raw}', using 90s", LogLevel.Warning);
                    return 90;
                }
                return seconds;
            }
            set
            {
                if (value < 10 || value > 600)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be 10-600 seconds");
                WriteProfileValue("ShutterTimeout", value.ToString());
            }
        }

        public int RotationTimeout
        {
            get
            {
                string raw = GetProfileValue("RotationTimeout", "90");
                if (string.IsNullOrWhiteSpace(raw))
                    raw = "90";
                if (!int.TryParse(raw, out int seconds) || seconds < 10 || seconds > 600)
                {
                    _logger?.Log($"Invalid RotationTimeout '{raw}', using 90s", LogLevel.Warning);
                    return 90;
                }
                return seconds;
            }
            set
            {
                if (value < 10 || value > 600)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be 10-600 seconds");
                WriteProfileValue("RotationTimeout", value.ToString());
            }
        }

        public bool DebugLog
        {
            get
            {
                string resetString = GetProfileValue("DebugLog", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("DebugLog", value.ToString().ToLower());
            }
        }

        public bool TraceLog
        {
            get
            {
                string resetString = GetProfileValue("TraceLog", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("TraceLog", value.ToString().ToLower());
            }
        }

        public bool HomePark
        {
            get
            {
                string resetString = GetProfileValue("HomePark", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("HomePark", value.ToString().ToLower());
            }
        }

        public bool UseMQTT
        {
            get
            {
                string resetString = GetProfileValue("MQTT", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("MQTT", value.ToString().ToLower());
            }
        }

        public string MQTTip
        {
            get
            {
                var value = GetProfileValue("MQTTIP", "localhost");
                return string.IsNullOrWhiteSpace(value) ? "localhost" : value;
            }
            set => WriteProfileValue("MQTTIP", value);
        }

        public string MQTTport
        {
            get
            {
                var value = GetProfileValue("MQTTPort", "1883");
                return string.IsNullOrWhiteSpace(value) ? "1883" : value;
            }
            set => WriteProfileValue("MQTTPort", value);
        }

        public bool MQTTLocalHost
        {
            get
            {
                string resetString = GetProfileValue("MQTTLocalHost", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("MQTTLocalHost", value.ToString().ToLower());
            }
        }

        public bool UseGNS
        {
            get
            {
                string resetString = GetProfileValue("GNS", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                WriteProfileValue("GNS", value.ToString().ToLower());
            }
        }

        public string GNSPath
        {
            get => GetProfileValue("GNSPath", "") ?? "";
            set => WriteProfileValue("GNSPath", value);
        }

        public string GNSDispatcherPath
        {
            get => GetProfileValue("GNSDispatcherPath", "") ?? "";
            set => WriteProfileValue("GNSDispatcherPath", value);
        }

        // These are read/written by multiple threads without locking

        // Backing fields as integers for atomic operations
        private int _rebootingInt = 0;
        private int _resettingInt = 0;
        private int _forceBusyInt = 0;
        private int _controllerReadyInt = 0;
        private int _systemWatchdogRunningInt = 0;
        private int _actionWatchdogRunningInt = 0;
        private int _commandInProgressInt = 0;
        private int _slewingStatusInt = 0;

        public bool SlewingStatus
        {
            get => Interlocked.CompareExchange(ref _slewingStatusInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _slewingStatusInt, value ? 1 : 0);
        }

        public bool CommandInProgress
        {
            get => Interlocked.CompareExchange(ref _commandInProgressInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _commandInProgressInt, value ? 1 : 0);
        }

        public bool Rebooting
        {
            get => Interlocked.CompareExchange(ref _rebootingInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _rebootingInt, value ? 1 : 0);
        }

        public bool Resetting
        {
            get => Interlocked.CompareExchange(ref _resettingInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _resettingInt, value ? 1 : 0);
        }

        public bool ForceBusy
        {
            get => Interlocked.CompareExchange(ref _forceBusyInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _forceBusyInt, value ? 1 : 0);
        }

        public bool ControllerReady
        {
            get => Interlocked.CompareExchange(ref _controllerReadyInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _controllerReadyInt, value ? 1 : 0);
        }

        public bool SystemWatchdogRunning
        {
            get => Interlocked.CompareExchange(ref _systemWatchdogRunningInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _systemWatchdogRunningInt, value ? 1 : 0);
        }

        public bool ActionWatchdogRunning
        {
            get => Interlocked.CompareExchange(ref _actionWatchdogRunningInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _actionWatchdogRunningInt, value ? 1 : 0);
        }

        public bool SoftResetAttempted { get; set; } = false;

        public bool SoftResetSuccess { get; set; } = false;

        public bool HardResetAttempted { get; set; } = false;

        public bool HardResetSuccess { get; set; } = false;

        public bool CommandRecovered { get; set; } = false;

        public double SlewAz { get; set; }

        public double JogSize { get; set; } = 10; // below this level it's a "jog" and not messaged to GNS

        public void RegistryEntries()
        {
            var rootEntries = new List<(string name, string value)>
                {
                    ("SerialPort", ""),
                    ("LogLocation", ""),
                    ("ResetExe", ""),
                    ("ResetParameters", ""),
                    ("ExternalReset", "false"),
                    ("InternalReset", "false"),
                    ("HomePark", "false"),
                    ("ShutterTimeout", "90"),
                    ("RotationTimeout", "90"),
                    ("ResetDelay", "30"),
                    ("CycleDelay", "10"),
                    ("DebugLog", "false"),
                    ("TraceLog", "false"),
                    ("MQTT", "false"),
                    ("GNS", "false"),
                    ("GNSPath", ""),
                    ("GNSDispatcherPath", ""),
                    ("MQTTIP", "10.17.1.92"),
                    ("MQTTPort", "1883"),
                    ("MQTTLocalHost", "false")
                };

            foreach (var (name, value) in rootEntries)
            {
                WriteProfileValue(name, value);
            }
        }

        public void SetLogger(FileLogger logger) => _logger = logger;

        public void SaveDomeSettings()
        {
            //_profile.WriteValue(_driverId, "HomeAzimuth", HomeAzimuth.ToString());
            //_profile.WriteValue(_driverId, "HasShutter", HasShutter.ToString());
            //_logger?.Log("Saved Dome Settings.");
        }

        public LogLevel LogLevel { get; set; } = LogLevel.Info;

    }
}

