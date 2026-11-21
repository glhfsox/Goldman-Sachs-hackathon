namespace AlertApi.Application.Exceptions;

public sealed class ActionTargetInUseException : ConflictException
{
    public ActionTargetInUseException(string id, IReadOnlyList<string> runbookIds)
        : base($"Action target '{id}' would break active runbooks: {string.Join(", ", runbookIds)}.")
    {
    }
}
