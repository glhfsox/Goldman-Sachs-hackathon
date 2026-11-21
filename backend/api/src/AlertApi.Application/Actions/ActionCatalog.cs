namespace AlertApi.Application.Actions;

// What a runbook step may use: the registered actions and the current targets.
public sealed record ActionCatalog(
    IReadOnlyDictionary<string, ActionDescriptor> Actions,
    IReadOnlyDictionary<string, TargetDefinition> Targets);
