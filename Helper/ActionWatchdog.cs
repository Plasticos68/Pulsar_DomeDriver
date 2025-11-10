using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Diagnostics;
using Pulsar_DomeDriver.MQTT;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar_DomeDriver.Helper
{
    public class ActionWatchdog
    {
        private readonly ConfigManager _config;
        private readonly TimeSpan _timeout;
        private readonly string _action;
        private readonly string _longAction;
        private readonly MqttPublisher _mqttPublisher;
        private readonly string _mqttTopic;
        private readonly string _mqttSuccess;
        private readonly string _mqttFail;
        private readonly string _mqttFailTopic = "Alarm";

        private readonly Func<Task> _resetRoutine;
        private readonly Func<WatchdogResult> _checkStatus;

        private readonly CancellationTokenSource _internalCts;
        private TaskCompletionSource<WatchdogResult> _tcs;

        public bool IsRunning => _tcs != null && !_tcs.Task.IsCompleted;

        public enum WatchdogResult
        {
            InProgress,
            Success,
            Failure,
            Timeout,
            Error
        }

        public ActionWatchdog(
            ConfigManager config,
            TimeSpan timeout,
            string action,
            string longAction,
            Func<WatchdogResult> checkStatus,
            CancellationTokenSource internalCts,
            Func<Task> resetRoutine = null,
            MqttPublisher mqttPublisher = null,
            string mqttTopic = null,
            string mqttSuccess = null,
            string mqttFail = null
        )
        {
            _config = config;
            _timeout = timeout;
            _action = action;
            _longAction = longAction;
            _internalCts = internalCts;
            _mqttPublisher = mqttPublisher;
            _mqttTopic = mqttTopic;
            _mqttSuccess = mqttSuccess;
            _mqttFail = mqttFail;
            _resetRoutine = resetRoutine;
            _checkStatus = checkStatus;
            _tcs = new TaskCompletionSource<WatchdogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task Start()
        {
            _config.WatchdogRunning = true;
            await PublishAsync(_mqttTopic, $"{_longAction}");

            //WatchdogLogger.Log($"[{_action}] Watchdog loop entered");

            var timeoutTask = Task.Delay(_timeout, _internalCts.Token);
            WatchdogResult result = WatchdogResult.InProgress;

            try
            {
                while (!_internalCts.IsCancellationRequested)
                {
                    if (_checkStatus() == WatchdogResult.Success)
                    {
                        _tcs.TrySetResult(WatchdogResult.Success);
                        break;
                    }

                    if (_tcs.Task.IsCompleted) break;
                    if (timeoutTask.IsCompleted)
                    {
                        _tcs.TrySetResult(WatchdogResult.Timeout);
                        break;
                    }

                    await Task.Delay(250, _internalCts.Token);
                }

                result = _tcs.Task.IsCompleted ? _tcs.Task.Result : WatchdogResult.Failure;

                switch (result)
                {
                    case WatchdogResult.Success:
                        _config.ForceBusy = false;
                        await PublishAsync(_mqttTopic, _mqttSuccess);
                        break;

                    case WatchdogResult.Timeout:
                        _config.ForceBusy = false;
                        await PublishAsync(_mqttFailTopic, $"{_longAction} timed out.");
                        if (_resetRoutine != null)
                        {
                            await PublishAsync(_mqttFailTopic, "Reset routine from timeout");
                            await _resetRoutine.Invoke();
                        }
                        break;

                    case WatchdogResult.Failure:
                    case WatchdogResult.Error:
                        _config.ForceBusy = false;
                        await PublishAsync(_mqttFailTopic, _mqttFail);
                        if (_resetRoutine != null)
                        {
                            await PublishAsync(_mqttFailTopic, "Reset routine due to error");
                            await _resetRoutine.Invoke();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                //WatchdogLogger.Log($"[{_action}] Watchdog exception: {ex.Message}");
                await PublishAsync(_mqttTopic, $"{_longAction} failed due to error {ex}.");
            }
            finally
            {
                _config.WatchdogRunning = false;
                _config.ForceBusy = false; // ensure it's always cleared
                Stop();
            }


        }

        public void MarkSuccess()
        {
            //WatchdogLogger.Log($"[{_action}] MarkSuccess called — setting result.");
            _config.ForceBusy = false;
            _tcs?.TrySetResult(WatchdogResult.Success);
        }

        public void MarkFailure()
        {
            _config.ForceBusy = false;
            _tcs?.TrySetResult(WatchdogResult.Failure);
        }

        public void Stop()
        {
            //WatchdogLogger.Log($"[{_action}] Watchdog.Stop() called — cancelling watchdog.");
            _internalCts?.Cancel();
            _internalCts?.Dispose();
        }

        private async Task PublishAsync(string topic, string message)
        {
            if (!_config.UseMQTT)
            { return; }

            if (_mqttPublisher?.IsConnected == true && !string.IsNullOrWhiteSpace(topic))
            {
                try
                {
                    await _mqttPublisher.PublishAsync(topic, message);
                    //WatchdogLogger.Log($"[{_action}] MQTT published: {message}");
                }
                catch (Exception ex)
                {
                    //WatchdogLogger.Log($"[{_action}] MQTT publish failed: {ex.Message}");
                }
            }
            else
            {
                //WatchdogLogger.Log($"[{_action}] MQTT not connected — publish skipped.");
            }
        }

        private async Task AnnounceStartAsync()
        {
            if (_mqttPublisher != null && _mqttTopic != null)
            {
                await _mqttPublisher.PublishAsync(_mqttTopic, _longAction);
            }
        }

        private Task PublishAsync(string payload)
        {
            return PublishAsync(_mqttTopic, payload);
        }

        private void ClearResetFlags()
        {
            _config.SoftResetAttempted = false;
            _config.HardResetAttempted = false;
            _config.SoftResetSuccess = false;
            _config.HardResetSuccess = false;
        }
    }
}
