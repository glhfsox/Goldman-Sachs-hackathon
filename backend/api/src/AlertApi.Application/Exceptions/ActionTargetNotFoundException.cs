namespace AlertApi.Application.Exceptions;

public sealed class ActionTargetNotFoundException : NotFoundException
{
    public ActionTargetNotFoundException(string id)
        : base($"Action target with id '{id}' was not found.")
    {
    }
}
