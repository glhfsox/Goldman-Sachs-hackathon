namespace AlertApi.Application.Exceptions;

public sealed class LiveRunExistsException : ConflictException
{
    public LiveRunExistsException(string alertId)
        : base($"Alert '{alertId}' already has a run in progress; cancel it or wait until it ends.")
    {
    }

    // A second request won the race at the database; the alert id is not known where it is caught.
    public LiveRunExistsException(Exception innerException)
        : base("The alert already has a run in progress; cancel it or wait until it ends.", innerException)
    {
    }
}
