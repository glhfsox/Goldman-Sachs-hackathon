using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class RetryWorkflowAction : HttpTargetAction
{
    public RetryWorkflowAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "retry_workflow";

    public override Risk Risk => Risk.Auto;

    public override string? DefaultTarget => "workflow-engine";
}
