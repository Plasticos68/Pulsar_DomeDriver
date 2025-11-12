using ASCOM.Utilities;
using Pulsar_DomeDriver.Diagnostics;
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
        public ConfigManager(Profile profile, FileLogger logger)
        {
            _profile = profile;
            _logger = logger;
        }

        public readonly string _driverId = "Pulsar_DomeDriver";
        public readonly string _description = "Pulsar Dome Driver with auto reset";
        public readonly string _driverInfo = "Pulsar DomeDriver";
        public readonly string _driverVersion = "1.0";
        public readonly int _connectRetryCount = 3;
        public readonly int _connectRetryDelay = 2000; // milliseconds
        public string serialPort = "";
        //public readonly string _mqttAlarm = "Dome/alarm";
        public int pollingIntervalMs = 500;
        public readonly int watchDogSettle = 100;
        public readonly int shutterSettle = 5000;
        public readonly int serialSettle = 60;
        public readonly int controllerTimeout = 10000;
        public readonly int statusMaxRetries = 2;       // number of retries for the domestatus before alarm

        public double HomeAzimuth { get; private set; }
        public double ParkAzimuth { get; set; } = 0;
        public double Azimuth { get; set; }
        public double AzimuthTolerance { get; set; } = 0; // allowablw error for azimuth
        public bool SlewingStatus { get; set; } = false;
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
            get => _profile.GetValue(_driverId, "SerialPort", "") ?? serialPort;
            set => _profile.WriteValue(_driverId, "SerialPort", value, "");
        }

        public bool SoftReset
        {
            get
            {

                string resetString = _profile.GetValue(_driverId, "InternalReset", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "InternalReset", value.ToString().ToLower());
            }
        }

        public bool HardReset
        {
            get
            {
                string resetString = _profile.GetValue(_driverId, "ExternalReset", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "ExternalReset", value.ToString().ToLower());
            }
        }

        public string LogLocation
        {
            get => _profile.GetValue(_driverId, "LogLocation", "") ?? "";
            set => _profile.WriteValue(_driverId, "LogLocation", value, "");
        }

        public string ResetExe
        {
            get => _profile.GetValue(_driverId, "ResetExe", "") ?? "";
            set => _profile.WriteValue(_driverId, "ResetExe", value, "");
        }

        public string ResetOnParameters
        {
            get => _profile.GetValue(_driverId, "ResetOnParameters", "") ?? "";
            set => _profile.WriteValue(_driverId, "ResetOnParameters", value, "");
        }

        public string ResetOffParameters
        {
            get => _profile.GetValue(_driverId, "ResetOffParameters", "") ?? "";
            set => _profile.WriteValue(_driverId, "ResetOffParameters", value, "");
        }

        public int ResetDelay
        {
            get
            {
                string delayString = _profile.GetValue(_driverId, "ResetDelay", "");
                bool success = int.TryParse(delayString, out int result);
                return success ? result : 5;
            }
            set
            {
                _profile.WriteValue(_driverId, "ResetDelay", value.ToString());
            }
        }

        public int CycleDelay
        {
            get
            {
                string delayString = _profile.GetValue(_driverId, "CycleDelay", "");
                bool success = int.TryParse(delayString, out int result);
                return success ? result : 5;
            }
            set
            {
                _profile.WriteValue(_driverId, "CycleDelay", value.ToString());
            }
        }

        public int ShutterTimeout
        {
            get
            {
                string timeoutString = _profile.GetValue(_driverId, "ShutterTimeout", "");
                bool success = int.TryParse(timeoutString, out int result);
                if (success)
                {
                    return (result);
                }
                else
                {
                    return 90000;
                }
            }
            set
            {
                _profile.WriteValue(_driverId, "ShutterTimeout", value.ToString());
            }
        }

        public int RotationTimeout
        {
            get
            {
                string timeoutString = _profile.GetValue(_driverId, "RotationTimeout", "");
                bool success = int.TryParse(timeoutString, out int result);
                if (success)
                {
                    return (result);
                }
                else
                {
                    return 90000;
                }
            }
            set
            {
                _profile.WriteValue(_driverId, "RotationTimeout", value.ToString());
            }
        }

        public bool DebugLog
        {
            get
            {
                string resetString = _profile.GetValue(_driverId, "DebugLog", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "DebugLog", value.ToString().ToLower());
            }
        }

        public bool TraceLog
        {
            get
            {
                string resetString = _profile.GetValue(_driverId, "TraceLog", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "TraceLog", value.ToString().ToLower());
            }
        }

        public bool UseMQTT
        {
            get
            {
                string resetString = _profile.GetValue(_driverId, "MQTT", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "MQTT", value.ToString().ToLower());
            }
        }

        public bool UseGNS
        {
            get
            {
                string resetString = _profile.GetValue(_driverId, "GNS", "");
                bool success = bool.TryParse(resetString, out bool result);
                return success ? result : false;
            }
            set
            {
                _profile.WriteValue(_driverId, "GNS", value.ToString().ToLower());
            }
        }

        public string GNSPath
        {
            get => _profile.GetValue(_driverId, "GNSPath", "") ?? "";
            set => _profile.WriteValue(_driverId, "GNSPath", value, "");
        }

        public string GNSDispatcherPath
        {
            get => _profile.GetValue(_driverId, "GNSDispatcherPath", "") ?? "";
            set => _profile.WriteValue(_driverId, "GNSDispatcherPath", value, "");
        }

        // These are read/written by multiple threads without locking

        // Backing fields as integers for atomic operations
        private int _rebootingInt = 0;
        private int _resettingInt = 0;
        private int _forceBusyInt = 0;
        private int _controllerReadyInt = 0;
        private int _watchdogRunningInt = 0;

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

        public bool WatchdogRunning
        {
            get => Interlocked.CompareExchange(ref _watchdogRunningInt, 0, 0) == 1;
            set => Interlocked.Exchange(ref _watchdogRunningInt, value ? 1 : 0);
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
                    ("ShutterTimeout", "90"),
                    ("RotationTimeout", "90"),
                    ("ResetDelay", "30"),
                    ("CycleDelay", "10"),
                    ("DebugLog", "false"),
                    ("TraceLog", "false"),
                    ("MQTT", "false"),
                    ("GNS", "false"),
                    ("GNSPath", ""),
                    ("GNSDispatcherPath", "")
                };

            foreach (var (name, value) in rootEntries)
            {
                _profile.WriteValue(_driverId, name, value);
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
