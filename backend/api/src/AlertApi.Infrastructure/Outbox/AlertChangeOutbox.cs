using System.Text.Json;

using AlertApi.Application.Constants;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Outbox;

public sealed class AlertChangeOutbox : IAlertChangeOutbox
{
    private readonly ApiDbContext _context;
    private readonly TimeProvider _timeProvider;

    public AlertChangeOutbox(ApiDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    // The record format of contracts/events.md section 4; the key is the alert id, like the processor's records.
    public void Add(string changeId, string change, DateTimeOffset at, string actor, AlertResponse alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var payload = JsonSerializer.Serialize(
            new { ChangeId = changeId, Change = change, At = at, Actor = actor, Alert = alert },
            RecordJson.Options);

        _context.Outbox.Add(OutboxMessage.Create(Topics.AlertsChanges, alert.Id, payload, _timeProvider.GetUtcNow()));
    }
}
