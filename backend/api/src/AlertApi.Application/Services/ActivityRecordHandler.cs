using System.Text.Json;

using AlertApi.Application.Constants;
using AlertApi.Application.DTOs.Messages;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;

namespace AlertApi.Application.Services;

public sealed class ActivityRecordHandler : IActivityRecordHandler
{
    private readonly IActivityService _activity;
    private readonly ISchemaSampleService _schemaSamples;

    public ActivityRecordHandler(IActivityService activity, ISchemaSampleService schemaSamples)
    {
        _activity = activity;
        _schemaSamples = schemaSamples;
    }

    public async Task HandleAsync(string topic, string value, CancellationToken cancellationToken = default)
    {
        switch (topic)
        {
            case Topics.AlertsChanges:
                await _activity.ApplyAlertChangeAsync(Parse<AlertChangeMessage>(value), cancellationToken);
                break;
            case Topics.AlertsTimeline:
                await _activity.ApplyTimelineEntryAsync(Parse<TimelineEntryMessage>(value), cancellationToken);
                break;
            case Topics.SchemaSamples:
                await _schemaSamples.ApplySampleAsync(Parse<SchemaSampleMessage>(value), cancellationToken);
                break;
            default:
                throw new InvalidRecordException($"Topic '{topic}' is not handled.");
        }
    }

    private static T Parse<T>(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, RecordJson.Options)
                ?? throw new InvalidRecordException("The record is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidRecordException($"The record is not valid: {exception.Message}", exception);
        }
    }
}
