namespace AlertApi.Application.Exceptions;

public sealed record ProblemItem(
    string Path,
    string Message);
