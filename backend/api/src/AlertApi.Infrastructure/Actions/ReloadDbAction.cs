using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class ReloadDbAction : HttpTargetAction
{
    public ReloadDbAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "reload_db";

    public override Risk Risk => Risk.Auto;

    public override string? DefaultTarget => "orders-db";
}
