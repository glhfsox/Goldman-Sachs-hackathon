namespace AlertApi.Application.Exceptions;

public abstract class UnprocessableException : AppException
{
    private readonly string _code;
    private readonly IReadOnlyList<ProblemItem> _problems;

    protected UnprocessableException(string code, IReadOnlyList<ProblemItem> problems)
        : base(422, "Unprocessable Entity", $"{problems.Count} problem(s) found.")
    {
        _code = code;
        _problems = problems;
    }

    public override string Code => _code;

    public override IReadOnlyList<ProblemItem> Problems => _problems;
}
