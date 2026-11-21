using AlertApi.Application.Actions;
using AlertApi.Domain.Models;

namespace AlertApi.Application.DTOs.Responses;

public sealed record ActionResponse(
    string Name,
    string Risk,
    string? DefaultTarget,
    string UrlMethod,
    int TimeoutS)
{
    public static ActionResponse From(ActionDescriptor action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return new ActionResponse(action.Name, action.Risk.ToName(), action.DefaultTarget, action.UrlMethod, action.TimeoutSeconds);
    }
}
