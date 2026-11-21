namespace AlertApi.Application.Exceptions;

public sealed class AlertNotFoundException : NotFoundException
{
    public AlertNotFoundException(string id)
        : base($"Alert with id '{id}' was not found.")
    {
    }
}
