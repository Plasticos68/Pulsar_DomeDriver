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
    public class MqttPublisher
    {
        private IMqttClient _client;
        private IMqttClientOptions _options;
        private readonly FileLogger _logger;
        private readonly ConfigManager _config;
        private bool _initialized = false; // Prevent multiple initializations
        private int _handlerGeneration = 0;

        public MqttPublisher(FileLogger logger, ConfigManager config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger?.Log("[MQTT] MqttPublisher instance created", LogLevel.Trace);
            _config = config;
        }

        public bool IsConnected => _client?.IsConnected == true;

        public async Task InitializeAsync(string brokerIp, int port = 1883)
        {
            _logger?.Log("[MQTT] InitializeAsync entered", LogLevel.Trace);

            if (_initialized)
            {
                _logger?.Log("[MQTT] Initialization skipped — already initialized", LogLevel.Debug);
                return;
            }

            try
            {
                var factory = new MqttFactory();
                _client = factory.CreateMqttClient();

                _options = new MqttClientOptionsBuilder()
                    .WithClientId("DomeDriver")
                    .WithTcpServer(brokerIp, port)
                    .Build();

                // Clear any previously assigned handlers
                _client.ConnectedHandler = null;
                _client.DisconnectedHandler = null;

                int generation = Interlocked.Increment(ref _handlerGeneration);

                _client.UseConnectedHandler(e =>
                {
                    if (_handlerGeneration != generation) return;
                    _logger?.Log($"[MQTT] Connected to broker at {brokerIp}:{port}", LogLevel.Info);
                });

                _client.UseDisconnectedHandler(e =>
                {
                    if (_handlerGeneration != generation) return;
                    string reason = e.Exception?.Message ?? "No exception provided";
                    _logger?.Log($"[MQTT] Disconnected from broker: {reason}", LogLevel.Debug);
                });

                _logger?.Log("[MQTT] Attempting ConnectAsync...", LogLevel.Debug);
                await _client.ConnectAsync(_options);
                _logger?.Log("[MQTT] ConnectAsync completed", LogLevel.Info);

                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger?.Log($"[MQTT] Connection failed: {ex}", LogLevel.Error);
            }
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (_config.Rebooting)
            {
                return;
            }
            _logger?.Log("[MQTT] PublishAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                _logger?.Log("[MQTT] Publish failed: client is null", LogLevel.Error);
                return;
            }

            if (!_client.IsConnected)
            {
                _logger?.Log($"[MQTT] Publish skipped — client not connected", LogLevel.Debug);
                return;
            }

            try
            {
                _logger?.Log($"[MQTT] Preparing message for topic '{topic}'", LogLevel.Debug);

                if (_config.Rebooting)
                {
                    payload = "REBOOTING";
                }

                _logger?.Log($"[MQTT] Attempting PublishAsync to '{topic}'", LogLevel.Debug);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithExactlyOnceQoS()
                    .WithRetainFlag()
                    .Build();

                await _client.PublishAsync(message);
                _logger?.Log($"[MQTT] Published to '{topic}': {payload}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[MQTT] Publish error: {ex.Message}", LogLevel.Error);
            }
        }

        public async Task SubscribeAsync(string topic, Action<string> onMessage)
        {
            _logger?.Log("[MQTT] SubscribeAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                _logger?.Log("[MQTT] Subscription failed: client is null", LogLevel.Error);
                throw new InvalidOperationException("MQTT client is not initialized.");
            }

            if (!_client.IsConnected)
            {
                _logger?.Log("[MQTT] Subscription failed: client not connected", LogLevel.Error);
                throw new InvalidOperationException("MQTT client is not connected.");
            }

            // Clear previous handler
            _client.ApplicationMessageReceivedHandler = null;

            int generation = Interlocked.Increment(ref _handlerGeneration);

            _client.UseApplicationMessageReceivedHandler(e =>
            {
                if (_handlerGeneration != generation) return;

                try
                {
                    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                    _logger?.Log($"[MQTT] Received on '{e.ApplicationMessage.Topic}': {payload}", LogLevel.Trace);
                    onMessage?.Invoke(payload);
                }
                catch (Exception ex)
                {
                    _logger?.Log($"[MQTT] Message handler error: {ex.Message}", LogLevel.Error);
                }
            });

            try
            {
                _logger?.Log($"[MQTT] Attempting SubscribeAsync to '{topic}'", LogLevel.Debug);

                var topicFilter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithExactlyOnceQoS()
                    .Build();

                await _client.SubscribeAsync(topicFilter);
                _logger?.Log($"[MQTT] Subscribed to topic: {topic}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[MQTT] Subscription error: {ex.Message}", LogLevel.Error);
            }
        }

        public async Task DisconnectAsync()
        {
            _logger?.Log("[MQTT] DisconnectAsync entered", LogLevel.Trace);

            if (_client == null)
            {
                _logger?.Log("[MQTT] Disconnect skipped: client is null", LogLevel.Debug);
                return;
            }

            if (!_client.IsConnected)
            {
                _logger?.Log("[MQTT] Disconnect skipped: client not connected", LogLevel.Debug);
                return;
            }

            try
            {
                _logger?.Log("[MQTT] Attempting DisconnectAsync...", LogLevel.Debug);
                await _client.DisconnectAsync();
                _logger?.Log("[MQTT] Disconnected cleanly", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[MQTT] Disconnect error: {ex.Message}", LogLevel.Error);
            }
        }
    }
}
