namespace AlertApi.Application.Exceptions;

public sealed class InvalidSchemaSampleException : UnprocessableException
{
    public InvalidSchemaSampleException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_schema_sample", problems)
    {
    }
}
