using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class EscalateAction : HttpTargetAction
{
    public EscalateAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "escalate";

    public override Risk Risk => Risk.Approval;

    public override string? DefaultTarget => "escalation";
}
