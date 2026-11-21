using AlertApi.Application.Actions;
using AlertApi.Domain.Models;

namespace AlertApi.Tests.Application;

// The registered actions and a few of the seeded targets (migration AddActionTargets), without a database.
public static class TestCatalog
{
    public static readonly IReadOnlyDictionary<string, ActionDescriptor> Actions = new[]
    {
        new ActionDescriptor("http_get", Risk.ReadOnly, null, "GET", 10),
        new ActionDescriptor("retry_workflow", Risk.Auto, "workflow-engine", "POST", 10),
        new ActionDescriptor("reload_db", Risk.Auto, "orders-db", "POST", 10),
        new ActionDescriptor("restart_service", Risk.Approval, "services", "POST", 10),
        new ActionDescriptor("webhook", Risk.Approval, null, "POST", 10),
        new ActionDescriptor("ticket", Risk.Approval, "ticketing", "POST", 10),
        new ActionDescriptor("escalate", Risk.Approval, "escalation", "POST", 10)
    }.ToDictionary(action => action.Name);

    public static readonly IReadOnlyDictionary<string, TargetDefinition> Targets = new[]
    {
        Target("workflow-engine", [], ("retry_workflow", "POST", "/workflows/{workflowId}/retry", null)),
        Target("services", [], ("restart_service", "POST", "/restart", null)),
        Target("orders-db", [],
            ("health", "GET", "/instances/{instance}/health", Risk.ReadOnly),
            ("reload_db", "POST", "/instances/{instance}/reload", null)),
        Target("escalation", [], ("escalate", "POST", "/escalations", null)),
        Target("ticketing", [], ("ticket", "POST", "/tickets", null)),
        Target("counterparty-service", ["http://counterparty-service"], ("http_get", "GET", "/health", null)),
        Target("risky-db", [], ("reload_db", "POST", "/reload", Risk.Approval))
    }.ToDictionary(target => target.Id);

    public static readonly ActionCatalog Default = new(Actions, Targets);

    private static TargetDefinition Target(string id, string[] origins, params (string Name, string Method, string Path, Risk? Risk)[] operations)
    {
        return new TargetDefinition(
            id,
            $"http://mock-target:8003/{id}",
            origins,
            operations.ToDictionary(operation => operation.Name, operation => new OperationDefinition(operation.Method, operation.Path, operation.Risk)),
            Version: 1);
    }
}
