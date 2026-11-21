namespace AlertApi.Application.Exceptions;

public sealed class AlertStateConflictException : ConflictException
{
    public AlertStateConflictException(string id, string status, string action)
        : base($"Alert '{id}' is {status}, so it cannot be {action}.")
    {
    }
}
