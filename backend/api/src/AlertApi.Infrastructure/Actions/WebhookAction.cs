using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class WebhookAction : HttpTargetAction
{
    public WebhookAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "webhook";

    public override Risk Risk => Risk.Approval;

    public override string? DefaultTarget => null;
}
