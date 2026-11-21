namespace AlertApi.Application.Exceptions;

public sealed class RunNotFoundException : NotFoundException
{
    public RunNotFoundException(string id)
        : base($"Run with id '{id}' was not found.")
    {
    }
}
