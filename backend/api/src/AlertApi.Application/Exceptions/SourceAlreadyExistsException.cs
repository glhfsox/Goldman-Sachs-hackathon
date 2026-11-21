namespace AlertApi.Application.Exceptions;

public sealed class SourceAlreadyExistsException : ConflictException
{
    public SourceAlreadyExistsException(string id)
        : base($"Source with id '{id}' already exists.")
    {
    }
}
