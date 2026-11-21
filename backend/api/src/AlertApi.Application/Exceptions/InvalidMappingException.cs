namespace AlertApi.Application.Exceptions;

public sealed class InvalidMappingException : UnprocessableException
{
    public InvalidMappingException(IReadOnlyList<ProblemItem> problems)
        : base("invalid_mapping", problems)
    {
    }
}
