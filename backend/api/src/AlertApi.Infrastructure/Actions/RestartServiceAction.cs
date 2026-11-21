using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class RestartServiceAction : HttpTargetAction
{
    public RestartServiceAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "restart_service";

    public override Risk Risk => Risk.Approval;

    public override string? DefaultTarget => "services";
}
