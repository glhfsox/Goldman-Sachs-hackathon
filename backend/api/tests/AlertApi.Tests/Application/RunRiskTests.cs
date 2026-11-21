using System.Text.Json;

using AlertApi.Application.Exceptions;
using AlertApi.Application.Runbooks;
using AlertApi.Application.Runs;
using AlertApi.Domain.Models;

namespace AlertApi.Tests.Application;

public sealed class RunRiskTests
{
    [Fact]
    public void RunRisk_IsTheMaxOverAllNodes_EvenInABranchAnIfMaySkip()
    {
        var nodes = Graph("""
            {"steps": [
              {"id": "health", "text": "h", "action": "http_get", "params": {"url": "http://counterparty-service/health"}},
              {"id": "retry", "text": "r", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}},
              {"id": "restart", "text": "x", "action": "restart_service", "needs": ["retry"],
               "if": {"step": "retry", "status": "failed"}, "params": {"name": "svc"}}]}
            """);
        var problems = new List<ProblemItem>();

        var risk = RunRisk.Compute(nodes, TestCatalog.Default, problems);

        Assert.Empty(problems);
        Assert.Equal(Risk.Approval, risk);
    }

    [Theory]
    [InlineData("""{"steps": [{"text": "read"}]}""", Risk.ReadOnly)]
    [InlineData("""{"steps": [{"text": "h", "action": "http_get", "params": {"url": "http://counterparty-service/health"}}]}""", Risk.ReadOnly)]
    [InlineData("""{"steps": [{"text": "r", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}}]}""", Risk.Auto)]
    [InlineData("""{"steps": [{"text": "r", "action": "reload_db", "params": {"target": "risky-db"}}]}""", Risk.Approval)]
    public void RunRisk_FollowsTheActionAndTheOperation(string canonical, Risk expected)
    {
        var problems = new List<ProblemItem>();

        Assert.Equal(expected, RunRisk.Compute(Graph(canonical), TestCatalog.Default, problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void UnknownAction_AndUnresolvableStep_AreProblems()
    {
        var problems = new List<ProblemItem>();

        RunRisk.Compute(
            Graph("""{"steps": [{"text": "a", "action": "format_disk"}, {"text": "b", "action": "http_get", "params": {}}]}"""),
            TestCatalog.Default,
            problems);

        Assert.Equal(["steps[0].action", "steps[1].params"], problems.Select(problem => problem.Path));
    }

    [Theory]
    [InlineData(Risk.ReadOnly, RunStartMode.Immediately)]
    [InlineData(Risk.Auto, RunStartMode.AfterCancelWindow)]
    [InlineData(Risk.Approval, RunStartMode.AfterApproval)]
    public void EachRisk_HasItsStartMode(Risk risk, RunStartMode expected)
    {
        Assert.Equal(expected, RunRisk.StartModeOf(risk));
    }

    private static IReadOnlyList<RunbookNode> Graph(string canonical)
    {
        return RunbookGraph.Parse(JsonDocument.Parse(canonical).RootElement);
    }
}
