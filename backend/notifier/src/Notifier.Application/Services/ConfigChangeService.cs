using System.Text.Json;

using Microsoft.Extensions.Logging;

using Notifier.Application.Constants;
using Notifier.Application.DTOs.Config;
using Notifier.Application.DTOs.Messages;
using Notifier.Application.Serialization;
using Notifier.Domain.Models;
using Notifier.Domain.Repositories;

namespace Notifier.Application.Services;

// Keeps the read-only copies of the api's configuration current (contracts/schemas/notifier.md, "Copies").
// Upserts by id and ignores a record older than the row, so replaying the compacted topic is safe.
public sealed class ConfigChangeService : IConfigChangeService
{
    private readonly IConfigRepository _config;
    private readonly ILogger<ConfigChangeService> _logger;

    public ConfigChangeService(
        IConfigRepository config,
        ILogger<ConfigChangeService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ApplyAsync(ConfigChangeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var updatedAt = Time.ToUtc(message.UpdatedAt);
        var body = message.Body is { ValueKind: JsonValueKind.Object } element ? element : (JsonElement?)null;

        switch (message.Kind)
        {
            case ConfigKinds.Rule:
                await ApplyRuleAsync(message.Id, body, updatedAt, cancellationToken);
                break;
            case ConfigKinds.Team:
                await ApplyTeamAsync(message.Id, body, updatedAt, cancellationToken);
                break;
            case ConfigKinds.User:
                await ApplyUserAsync(message.Id, body, updatedAt, cancellationToken);
                break;
            case ConfigKinds.Membership:
                await ApplyMembershipAsync(message, body, updatedAt, cancellationToken);
                break;
            case ConfigKinds.Preferences:
                await ApplyPreferencesAsync(message.Id, body, updatedAt, cancellationToken);
                break;
            case ConfigKinds.Runbook:
                await ApplyRunbookAsync(message.Id, body, updatedAt, cancellationToken);
                break;
            default:
                return;
        }

        await _config.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyRuleAsync(string id, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        if (body is not { } element)
        {
            return;
        }

        var dto = element.Deserialize<RuleBodyDto>(NotifierJson.Options);
        var enabled = dto?.Enabled ?? true;
        var rule = await _config.FindRuleAsync(id, cancellationToken);
        if (rule is null)
        {
            await _config.AddAsync(Rule.Create(id, dto?.TeamId, enabled, element.GetRawText(), updatedAt), cancellationToken);

            return;
        }

        rule.Update(dto?.TeamId, enabled, element.GetRawText(), updatedAt);
    }

    private async Task ApplyTeamAsync(string id, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var dto = body?.Deserialize<TeamDto>(NotifierJson.Options);
        var team = await _config.FindTeamAsync(id, cancellationToken);
        if (team is null)
        {
            await _config.AddAsync(Team.Create(id, dto?.Name, updatedAt), cancellationToken);

            return;
        }

        team.Update(dto?.Name, updatedAt);
    }

    private async Task ApplyUserAsync(string id, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var dto = body?.Deserialize<UserDto>(NotifierJson.Options);
        var user = await _config.FindUserAsync(id, cancellationToken);
        if (user is null)
        {
            await _config.AddAsync(User.Create(id, dto?.Name, dto?.Email, dto?.Phone, updatedAt), cancellationToken);

            return;
        }

        user.Update(dto?.Name, dto?.Email, dto?.Phone, updatedAt);
    }

    // Memberships are the one kind that is really deleted (`deleted: true`, id "<team_id>:<user_id>").
    private async Task ApplyMembershipAsync(ConfigChangeMessage message, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var dto = body?.Deserialize<MembershipDto>(NotifierJson.Options);
        var ids = message.Id.Split(':', 2);
        var teamId = dto?.TeamId ?? ids[0];
        var userId = dto?.UserId ?? (ids.Length > 1 ? ids[1] : string.Empty);
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("Membership record {Id} names no team and user; ignoring it", message.Id);

            return;
        }

        var membership = await _config.FindMembershipAsync(teamId, userId, cancellationToken);
        if (message.Deleted)
        {
            if (membership is not null && updatedAt >= membership.UpdatedAt)
            {
                _config.Remove(membership);
            }

            return;
        }

        if (membership is null)
        {
            await _config.AddAsync(Membership.Create(teamId, userId, dto?.OnCall ?? false, updatedAt), cancellationToken);

            return;
        }

        membership.Update(dto?.OnCall ?? false, updatedAt);
    }

    private async Task ApplyPreferencesAsync(string userId, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        if (body is not { } element)
        {
            return;
        }

        var preferences = await _config.FindPreferencesAsync(userId, cancellationToken);
        if (preferences is null)
        {
            await _config.AddAsync(UserPreferences.Create(userId, element.GetRawText(), updatedAt), cancellationToken);

            return;
        }

        preferences.Update(element.GetRawText(), updatedAt);
    }

    private async Task ApplyRunbookAsync(string id, JsonElement? body, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var dto = body?.Deserialize<RunbookBodyDto>(NotifierJson.Options);
        if (dto is null)
        {
            return;
        }

        var canonical = dto.Canonical is { ValueKind: JsonValueKind.Object } value ? value.GetRawText() : null;
        var status = string.IsNullOrWhiteSpace(dto.Status) ? "draft" : dto.Status;
        var runbook = await _config.FindRunbookAsync(id, cancellationToken);
        if (runbook is null)
        {
            await _config.AddAsync(Runbook.Create(id, dto.TeamId, dto.Title, canonical, status, dto.Version, updatedAt), cancellationToken);

            return;
        }

        runbook.Update(dto.TeamId, dto.Title, canonical, status, dto.Version, updatedAt);
    }
}
