namespace Notifier.Domain.Exceptions;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(string entity, string id, string from, string action)
        : base($"{entity} '{id}' in status '{from}' cannot {action}.")
    {
    }
}
