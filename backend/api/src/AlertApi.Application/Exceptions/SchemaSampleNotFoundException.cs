namespace AlertApi.Application.Exceptions;

public sealed class SchemaSampleNotFoundException : NotFoundException
{
    public SchemaSampleNotFoundException(string fingerprint)
        : base($"Schema sample with fingerprint '{fingerprint}' was not found.")
    {
    }
}
