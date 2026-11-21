namespace AlertApi.Application.Exceptions;

public sealed class SourceNotFoundException : NotFoundException
{
    public SourceNotFoundException(string id)
        : base($"Source with id '{id}' was not found.")
    {
    }
}
