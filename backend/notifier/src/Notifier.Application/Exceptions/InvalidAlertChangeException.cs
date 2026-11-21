namespace Notifier.Application.Exceptions;

public sealed class InvalidAlertChangeException : Exception
{
    public InvalidAlertChangeException(string changeId, string problem)
        : base($"Alert change '{changeId}' is invalid: {problem}.")
    {
    }
}
