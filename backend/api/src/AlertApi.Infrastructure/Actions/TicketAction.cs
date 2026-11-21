using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class TicketAction : HttpTargetAction
{
    public TicketAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "ticket";

    public override Risk Risk => Risk.Approval;

    public override string? DefaultTarget => "ticketing";
}
