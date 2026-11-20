using System.Text.Json;

using Confluent.Kafka;

using Notifier.Application.Exceptions;
using Notifier.Application.Serialization;
using Notifier.Infrastructure.Messaging;

namespace Notifier.Worker.Workers;

// Consume -> handle in a fresh scope -> commit only after the database commit (at-least-once).
// A record that fails for a transient reason is read again; one that can never be applied is logged and skipped.
public abstract class KafkaConsumerWorker<TMessage> : BackgroundService
    where TMessage : class
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaConsumerFactory _consumerFactory;
    private readonly ILogger _logger;

    protected KafkaConsumerWorker(
        IServiceScopeFactory scopeFactory,
        KafkaConsumerFactory consumerFactory,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _consumerFactory = consumerFactory;
        _logger = logger;
    }

    protected abstract string Topic { get; }

    protected abstract Task HandleAsync(IServiceProvider services, TMessage message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume blocks the thread; yielding first lets the host finish starting the other workers.
        await Task.Yield();

        using var consumer = _consumerFactory.Create();
        consumer.Subscribe(Topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    // Typically the topic does not exist yet: kafka-init has not run.
                    _logger.LogWarning("Consuming {Topic} failed: {Reason}", Topic, exception.Error.Reason);
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                if (await TryHandleAsync(result.Message.Value, stoppingToken))
                {
                    consumer.Commit(result);
                }
                else
                {
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping the {Topic} consumer", Topic);
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> TryHandleAsync(string? value, CancellationToken cancellationToken)
    {
        TMessage? message;
        try
        {
            message = value is null ? null : JsonSerializer.Deserialize<TMessage>(value, NotifierJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Skipping a malformed {Topic} record", Topic);

            return true;
        }

        if (message is null)
        {
            return true;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await HandleAsync(scope.ServiceProvider, message, cancellationToken);

            return true;
        }
        catch (InvalidAlertChangeException exception)
        {
            _logger.LogError(exception, "Skipping an invalid {Topic} record", Topic);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Handling a {Topic} record failed; it will be retried", Topic);

            return false;
        }
    }
}
