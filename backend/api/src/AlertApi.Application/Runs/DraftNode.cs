using System.Text.Json;

using AlertApi.Application.Runbooks;

namespace AlertApi.Application.Runs;

// A runbook step as a run would start it: params filled, request frozen, and the placeholders the alert had no value for.
public sealed record DraftNode(
    RunbookNode Step,
    string Text,
    JsonElement? Params,
    IReadOnlyList<string> Missing,
    string Risk,
    string? TargetId,
    int? TargetVersion,
    string? Request,
    IReadOnlyList<string> Needs);
