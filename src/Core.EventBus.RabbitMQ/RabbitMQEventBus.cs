using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Core.EventBus.Abstractions;
using Core.EventBus.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Core.EventBus.RabbitMQ
{
    public sealed class RabbitMQEventBus : IEventBus, IHostedService, IDisposable
    {
        private const string ExchangeName = "medi_ai_exchange";

        private readonly ILogger<RabbitMQEventBus> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _consumerName;
        private readonly EventBusSubscriptionInfo _subscriptionInfo;
        private readonly ResiliencePipeline _pipeline;
        private readonly RabbitMQTelemetry _telemetry;
        private readonly IConnectionFactory _connectionFactory;
        private IConnection _rabbitMQConnection;
        private IModel _consumerChannel;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, IModel> _publishChannels = new Dictionary<string, IModel>();

        public RabbitMQEventBus(
            ILogger<RabbitMQEventBus> logger,
            IServiceProvider serviceProvider,
            IOptions<EventBusOptions> options,
            IOptions<EventBusSubscriptionInfo> subscriptionOptions,
            RabbitMQTelemetry telemetry,
            IConnectionFactory connectionFactory)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _consumerName = options.Value.SubscriptionClientName;
            _subscriptionInfo = subscriptionOptions.Value;
            _pipeline = CreateResiliencePipeline(options.Value.RetryCount);
            _telemetry = telemetry;
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        public async Task PublishAsync(IntegrationEvent @event)
        {
            ensureConnectionOpen();

            var eventName = @event.GetType().Name;
            IModel channel;

            lock (_syncRoot)
            {
                if (!_publishChannels.TryGetValue(eventName, out channel))
                {
                    channel = _rabbitMQConnection.CreateModel();
                    _publishChannels[eventName] = channel;
                }
            }

            channel.ExchangeDeclare(exchange: ExchangeName, type: "direct", durable: true);

            var message = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), _subscriptionInfo.JsonSerializerOptions);
            var activityName = $"{eventName} publish";

            await _pipeline.ExecuteAsync(async token =>
            {
                using var activity = _telemetry.ActivitySource.StartActivity(activityName, ActivityKind.Producer);
                var properties = channel.CreateBasicProperties();
                properties.DeliveryMode = 2;

                _telemetry.Propagator.Inject(new PropagationContext(activity?.Context ?? Activity.Current?.Context ?? default, Baggage.Current), properties, InjectTraceContextIntoBasicProperties);

                channel.BasicPublish(
                    exchange: ExchangeName,
                    routingKey: eventName,
                    mandatory: true,
                    basicProperties: properties,
                    body: message);

                await Task.CompletedTask;
            });
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ensureConnectionOpen();
            setupConsumerChannel();

            foreach (var eventName in _subscriptionInfo.EventTypes.Keys)
            {
                setupConsumer(eventName);
            }

            return Task.CompletedTask;
        }

        private void setupConsumerChannel()
        {
            if (_consumerChannel != null && _consumerChannel.IsOpen)
                return;

            _consumerChannel = _rabbitMQConnection.CreateModel();
            _consumerChannel.ExchangeDeclare(exchange: ExchangeName, type: "direct", durable: true);
            _consumerChannel.CallbackException += (sender, ea) =>
            {
                _logger.LogWarning(ea.Exception, "Recreating RabbitMQ consumer channel");
                _consumerChannel.Dispose();
                setupConsumerChannel();
                foreach (var eventName in _subscriptionInfo.EventTypes.Keys)
                {
                    setupConsumer(eventName);
                }
            };
        }

        private void setupConsumer(string eventName)
        {
            var queueName = getQueueName(eventName);
            _consumerChannel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);
            _consumerChannel.QueueBind(queue: queueName, exchange: ExchangeName, routingKey: eventName);

            var consumer = new AsyncEventingBasicConsumer(_consumerChannel);
            consumer.Received += OnMessageReceived;

            _consumerChannel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
            _logger.LogInformation("Subscribed to event {EventName} with queue {QueueName}", eventName, queueName);
        }

        private string getQueueName(string eventName) =>
            eventName.EndsWith("IntegrationEvent", StringComparison.OrdinalIgnoreCase)
                ? $"{_consumerName}.{eventName[..^"IntegrationEvent".Length]}"
                : $"{_consumerName}.{eventName}";

        private async Task OnMessageReceived(object sender, BasicDeliverEventArgs eventArgs)
        {
            var eventName = eventArgs.RoutingKey;
            var message = Encoding.UTF8.GetString(eventArgs.Body.Span);

            try
            {
                await processEvent(eventName, message);
                await acknowledgeMessageAsync(eventArgs.DeliveryTag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message for event {EventName}", eventName);
                await negativeAcknowledgeMessageAsync(eventArgs.DeliveryTag);
            }
        }

        private async Task acknowledgeMessageAsync(ulong deliveryTag)
        {
            await _pipeline.ExecuteAsync(async _ =>
            {
                _consumerChannel.BasicAck(deliveryTag, multiple: false);
                await Task.CompletedTask;
            });
        }

        private async Task negativeAcknowledgeMessageAsync(ulong deliveryTag)
        {
            await _pipeline.ExecuteAsync(async _ =>
            {
                _consumerChannel.BasicNack(deliveryTag, multiple: false, requeue: false);
                await Task.CompletedTask;
            });
        }

        private async Task processEvent(string eventName, string message)
        {
            if (!_subscriptionInfo.EventTypes.TryGetValue(eventName, out var eventType))
            {
                _logger.LogWarning("No subscription for RabbitMQ event: {EventName}", eventName);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var integrationEvent = JsonSerializer.Deserialize(message, eventType, _subscriptionInfo.JsonSerializerOptions) as IntegrationEvent;

            var handlers = scope.ServiceProvider.GetKeyedServices<IIntegrationEventHandler>(eventType);
            foreach (var handler in handlers)
            {
                await handler.Handle(integrationEvent);
            }
        }

        private void ensureConnectionOpen()
        {
            if (_rabbitMQConnection?.IsOpen ?? false) return;

            lock (_syncRoot)
            {
                if (_rabbitMQConnection?.IsOpen ?? false) return;

                _rabbitMQConnection?.Dispose();
                _rabbitMQConnection = _pipeline.Execute(() => _connectionFactory.CreateConnection());

                _rabbitMQConnection.ConnectionShutdown += OnConnectionShutdown;
                _rabbitMQConnection.CallbackException += OnCallbackException;
                _rabbitMQConnection.ConnectionBlocked += OnConnectionBlocked;

                _logger.LogInformation("RabbitMQ connection is open");
            }
        }

        private void OnConnectionBlocked(object sender, ConnectionBlockedEventArgs e)
        {
            _logger.LogWarning("RabbitMQ connection is blocked. Trying to re-connect...");
            tryConnect();
        }

        private void OnCallbackException(object sender, CallbackExceptionEventArgs e)
        {
            _logger.LogWarning(e.Exception, "RabbitMQ connection throw exception. Trying to re-connect...");
            tryConnect();
        }

        private void OnConnectionShutdown(object sender, ShutdownEventArgs reason)
        {
            _logger.LogWarning("RabbitMQ connection is shut down. Trying to re-connect...");
            tryConnect();
        }

        private void tryConnect()
        {
            _pipeline.Execute(() =>
            {
                ensureConnectionOpen();
                setupConsumerChannel();
            });
        }

        public void Dispose()
        {
            foreach (var channel in _publishChannels.Values)
            {
                channel.Dispose();
            }
            _publishChannels.Clear();
            _consumerChannel?.Dispose();
            _rabbitMQConnection?.Dispose();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        private static ResiliencePipeline CreateResiliencePipeline(int retryCount) =>
            new ResiliencePipelineBuilder()
                .AddRetry(new()
                {
                    MaxRetryAttempts = retryCount,
                    Delay = TimeSpan.FromSeconds(5),
                    UseJitter = true,
                    BackoffType = DelayBackoffType.Exponential
                })
                .Build();

        private static void InjectTraceContextIntoBasicProperties(IBasicProperties props, string key, string value)
        {
            props.Headers ??= new Dictionary<string, object>();
            props.Headers[key] = value;
        }
    }
}