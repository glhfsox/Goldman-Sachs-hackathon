using System.Text.Json;

using AlertApi.Application.Actions;
using AlertApi.Application.Exceptions;
using AlertApi.Domain.Models;

namespace AlertApi.Tests.Application;

public sealed class ActionTargetResolverTests
{
    [Fact]
    public void StepWithoutTarget_UsesTheActionsDefaultTarget_AndFillsThePathEscaped()
    {
        var resolved = Resolve("retry_workflow", """{"workflowId": "WF 7/a"}""");

        Assert.Equal("POST", resolved.Request.Method);
        Assert.Equal("http://mock-target:8003/workflow-engine/workflows/WF%207%2Fa/retry", resolved.Request.Url);
        Assert.Null(resolved.Request.Body);
        Assert.Equal("workflow-engine", resolved.TargetId);
        Assert.Equal(Risk.Auto, resolved.Risk);
    }

    [Fact]
    public void ParamsThatAreNotPathValues_TravelInTheBody()
    {
        var resolved = Resolve("escalate", """{"team": "counterparty-support"}""");

        Assert.Equal("http://mock-target:8003/escalation/escalations", resolved.Request.Url);
        Assert.Equal("""{"team":"counterparty-support"}""", resolved.Request.Body);
    }

    [Fact]
    public void NamedTargetAndOperation_AreUsed_AndAGetHasNoBody()
    {
        var resolved = Resolve("http_get", """{"target": "orders-db", "operation": "health", "instance": "db-2"}""");

        Assert.Equal("GET", resolved.Request.Method);
        Assert.Equal("http://mock-target:8003/orders-db/instances/db-2/health", resolved.Request.Url);
        Assert.Null(resolved.Request.Body);
        Assert.Equal(Risk.ReadOnly, resolved.Risk);
    }

    [Fact]
    public void UrlOfARegisteredOrigin_GoesToThatTargetsBaseUrl()
    {
        var resolved = Resolve("http_get", """{"url": "http://counterparty-service/health?deep=1"}""");

        Assert.Equal("GET", resolved.Request.Method);
        Assert.Equal("http://mock-target:8003/counterparty-service/health?deep=1", resolved.Request.Url);
        Assert.Equal("counterparty-service", resolved.TargetId);
    }

    [Fact]
    public void OperationRisk_CanOnlyRaiseTheActionsRisk()
    {
        var resolved = Resolve("reload_db", """{"target": "risky-db"}""");

        Assert.Equal(Risk.Approval, resolved.Risk);
    }

    [Theory]
    [InlineData("http_get", """{"url": "http://10.0.0.5/admin"}""", "s.params.url")]
    [InlineData("http_get", """{"url": "http://{{labels.host}}/health"}""", "s.params.url")]
    [InlineData("http_get", """{"url": "ftp://counterparty-service/x"}""", "s.params.url")]
    [InlineData("http_get", """{}""", "s.params")]
    [InlineData("webhook", """{"url": "http://counterparty-service/x", "target": "services"}""", "s.params")]
    [InlineData("reload_db", """{"target": "orders-db"}""", "s.params.instance")]
    public void UnresolvableStep_IsOneProblemAtItsPath(string action, string parameters, string expectedPath)
    {
        var problems = new List<ProblemItem>();

        var resolved = ActionTargetResolver.Resolve(TestCatalog.Actions[action], Parse(parameters), TestCatalog.Targets, "s", problems);

        Assert.Null(resolved);
        Assert.Equal(expectedPath, Assert.Single(problems).Path);
    }

    private static ResolvedAction Resolve(string action, string parameters)
    {
        var problems = new List<ProblemItem>();
        var resolved = ActionTargetResolver.Resolve(TestCatalog.Actions[action], Parse(parameters), TestCatalog.Targets, "s", problems);

        Assert.Empty(problems);

        return resolved!;
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
