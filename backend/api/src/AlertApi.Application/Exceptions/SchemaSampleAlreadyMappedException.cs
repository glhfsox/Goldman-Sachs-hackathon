namespace AlertApi.Application.Exceptions;

public sealed class SchemaSampleAlreadyMappedException : ConflictException
{
    public SchemaSampleAlreadyMappedException(string fingerprint)
        : base($"Schema sample '{fingerprint}' already has a mapping, so it cannot be marked as failed.")
    {
    }
}
