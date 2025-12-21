using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Diagnostics;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar_DomeDriver.MQTT
{
    public class MqttPublisher : IDisposable
    {
        private IMqttClient? _client;
        private IMqttClientOptions? _options;
        private readonly FileLogger _logger;
        private readonly ConfigManager _config;
        private bool _initialized = false;
        private int _handlerGeneration = 0;
        private volatile bool _disposed = false;
        private readonly object _disposeLock = new();
        private readonly TaskCompletionSource<bool> _connectedTcs = new();
        public Task WaitForConnectedAsync() => _connectedTcs.Task;


        public MqttPublisher(FileLogger logger, ConfigManager config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            SafeLog("[MQTT] MqttPublisher instance created", LogLevel.Trace);
            _config = config;
        }

        public bool IsConnected => _client?.IsConnected == true;

        public async Task InitializeAsync(string brokerIp, string port)
        {
            if (string.IsNullOrWhiteSpace(brokerIp))
            {
                brokerIp = "localhost";
            }

            if (!int.TryParse(port, out int portNumber))
            {
                portNumber = 1883;
                SafeLog($"[MQTT] Invalid port '{port}', defaulting to {portNumber}", LogLevel.Warning);
            }
            if (_disposed) return;
            SafeLog("[MQTT] InitializeAsync entered", LogLevel.Trace);

            if (_initialized)
            {
                SafeLog("[MQTT] Initialization skipped — already initialized", LogLevel.Debug);
                return;
            }

            try
            {
                var factory = new MqttFactory();
                _client = factory.CreateMqttClient();

                _options = new MqttClientOptionsBuilder()
                    .WithClientId("DomeDriver")
                    .WithTcpServer(brokerIp, portNumber)
                    .Build();

                _client.ConnectedHandler = null;
                _client.DisconnectedHandler = null;

                int generation = Interlocked.Increment(ref _handlerGeneration);

                _client.UseConnectedHandler(e =>
                {
                    if (_handlerGeneration != generation) return;

                    SafeLog($"[MQTT] Connected to broker at {brokerIp}:{port}", LogLevel.Info);
                    SafeLog($"[MQTT] reached point 1", LogLevel.Info);

                    _connectedTcs.TrySetResult(true); // ✅ Signal confirmed connection
                });

                SafeLog($"[MQTT] reached point 2", LogLevel.Info);

                _client.UseDisconnectedHandler(e =>
                {
                    SafeLog($"[MQTT] reached point 3", LogLevel.Info);
                    if (_handlerGeneration != generation) return;

                    string reason = e.Exception?.Message ?? "No exception provided";
                    SafeLog($"[MQTT] Disconnected from broker: {reason}", LogLevel.Debug);
                });

                SafeLog($"[MQTT] reached point 4", LogLevel.Info);

                SafeLog("[MQTT] Attempting ConnectAsync...", LogLevel.Debug);
                await _client.ConnectAsync(_options);
                SafeLog("[MQTT] ConnectAsync completed", LogLevel.Info);

                SafeLog($"[MQTT] reached point 5", LogLevel.Info);

                _initialized = true;
            }
            catch (Exception ex)
            {
                SafeLog($"[MQTT] Connection failed: {ex}", LogLevel.Error);
            }
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (_disposed) return;

            if (_config.Rebooting) return;

            SafeLog("[MQTT] PublishAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                SafeLog("[MQTT] Publish failed: client is null", LogLevel.Error);
                return;
            }

            if (!_client.IsConnected)
            {
                SafeLog("[MQTT] Publish skipped — client not connected", LogLevel.Debug);
                return;
            }

            try
            {
                if (_config.Rebooting)
                    payload = "REBOOTING";

                SafeLog($"[MQTT] Preparing message for topic '{topic}'", LogLevel.Debug);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithExactlyOnceQoS()
                    .WithRetainFlag()
                    .Build();

                await _client.PublishAsync(message);
                SafeLog($"[MQTT] Published to '{topic}': {payload}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SafeLog($"[MQTT] Publish error: {ex.Message}", LogLevel.Error);
            }
        }

        public async Task SubscribeAsync(string topic, Action<string> onMessage)
        {
            if (_disposed) return;

            SafeLog("[MQTT] SubscribeAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                SafeLog("[MQTT] Subscription failed: client is null", LogLevel.Error);
                throw new InvalidOperationException("MQTT client is not initialized.");
            }

            if (!_client.IsConnected)
            {
                SafeLog("[MQTT] Subscription failed: client not connected", LogLevel.Error);
                throw new InvalidOperationException("MQTT client is not connected.");
            }

            // Dispose previous handler if applicable
            var oldHandler = _client.ApplicationMessageReceivedHandler;
            _client.ApplicationMessageReceivedHandler = null;
            (oldHandler as IDisposable)?.Dispose();

            int generation = Interlocked.Increment(ref _handlerGeneration);

            _client.UseApplicationMessageReceivedHandler(e =>
            {
                if (_handlerGeneration != generation) return;

                try
                {
                    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload ?? Array.Empty<byte>());
                    SafeLog($"[MQTT] Received on '{e.ApplicationMessage.Topic}': {payload}", LogLevel.Trace);
                    if (e.ApplicationMessage.Topic == topic)
                        onMessage?.Invoke(payload);
                }
                catch (Exception ex)
                {
                    SafeLog($"[MQTT] Message handler error: {ex.Message}", LogLevel.Error);
                }
            });

            try
            {
                SafeLog($"[MQTT] Attempting SubscribeAsync to '{topic}'", LogLevel.Debug);

                var topicFilter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithExactlyOnceQoS()
                    .Build();

                await _client.SubscribeAsync(topicFilter);
                SafeLog($"[MQTT] Subscribed to topic: {topic}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SafeLog($"[MQTT] Subscription error: {ex.Message}", LogLevel.Error);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_disposed) return;

            SafeLog("[MQTT] DisconnectAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                SafeLog("[MQTT] Disconnect skipped: client is null", LogLevel.Debug);
                return;
            }

            if (!_client.IsConnected)
            {
                SafeLog("[MQTT] Disconnect skipped: client not connected", LogLevel.Debug);
                return;
            }

            try
            {
                SafeLog("[MQTT] Attempting DisconnectAsync...", LogLevel.Debug);
                await _client.DisconnectAsync();
                SafeLog("[MQTT] Disconnected cleanly", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SafeLog($"[MQTT] Disconnect error: {ex.Message}", LogLevel.Error);
            }
        }

        private void SafeLog(string message, LogLevel level)
        {
            if (_disposed) return;
            try { _logger?.Log(message, level); }
            catch { /* suppress logging errors during disposal */ }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    if (_client?.IsConnected == true)
                    {
                        _client.DisconnectAsync().GetAwaiter().GetResult();
                        SafeLog("[MQTT] Client disconnected.", LogLevel.Debug);
                    }

                    _client?.Dispose();
                    _client = null;
                }
                catch (Exception ex)
                {
                    SafeLog($"[MQTT] Error during disposal: {ex.Message}", LogLevel.Warning);
                }
            }
        }
    }
}
