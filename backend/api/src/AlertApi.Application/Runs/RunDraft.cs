namespace AlertApi.Application.Runs;

public sealed record RunDraft(
    string Risk,
    IReadOnlyList<DraftNode> Nodes);
