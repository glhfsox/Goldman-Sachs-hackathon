namespace AlertApi.Application.Actions;

public sealed record ActionResult(
    bool Ok,
    string? Output,
    string? Error)
{
    public static ActionResult Success(string? output)
    {
        return new ActionResult(true, output, null);
    }

    public static ActionResult Failure(string error, string? output = null)
    {
        return new ActionResult(false, output, error);
    }
}
