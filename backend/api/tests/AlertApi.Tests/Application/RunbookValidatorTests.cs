using System.Text.Json;

using AlertApi.Application.Validation;

namespace AlertApi.Tests.Application;

public sealed class RunbookValidatorTests
{
    // What the ai-worker saves for Appendix B's settlement runbook: linear, no matches, approval set per step.
    private const string Linear = """
        {"id": "settlement-timeout", "title": "Settlement timeout",
         "steps": [{"text": "Is counterparty-service healthy?", "action": "http_get",
                    "params": {"url": "http://counterparty-service/health"}, "approval": "none"},
                   {"text": "Retry the workflow", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"},
                    "approval": "required"},
                   {"text": "Escalate to counterparty-support", "action": "escalate", "params": {"team": "counterparty-support"},
                    "when": "if retry fails twice", "approval": "required", "link": "https://wiki.example.com/settlement"}]}
        """;

    private const string Graph = """
        {"id": "rb", "title": "Graph",
         "steps": [{"id": "health", "text": "Check health", "action": "http_get", "params": {"url": "http://x/health"}},
                   {"id": "retry", "text": "Retry", "action": "retry_workflow", "needs": ["health"], "retries": 2, "timeout_s": 30},
                   {"id": "escalate", "text": "Escalate", "action": "escalate", "needs": ["retry"],
                    "if": {"step": "retry", "status": "failed"}}]}
        """;

    [Theory]
    [InlineData(Linear, "settlement-timeout")]
    [InlineData(Graph, "rb")]
    public void Canonical_Valid_HasNoProblems(string json, string id)
    {
        Assert.Empty(RunbookValidator.ValidateCanonical(Parse(json), id));
    }

    [Theory]
    [InlineData("\"action\": \"retry_workflow\"", "\"action\": \"reboot_planet\"", "canonical.steps[1].action")]
    [InlineData("\"needs\": [\"health\"]", "\"needs\": [\"escalate\"]", "canonical.steps")]
    [InlineData("\"if\": {\"step\": \"retry\"", "\"if\": {\"step\": \"health\"", "canonical.steps[2].if.step")]
    [InlineData("\"status\": \"failed\"", "\"status\": \"maybe\"", "canonical.steps[2].if.status")]
    [InlineData("\"retries\": 2", "\"retries\": 4", "canonical.steps[1].retries")]
    public void Canonical_WithOneWrongField_ReportsOnlyThatPath(string original, string broken, string expectedPath)
    {
        var problems = RunbookValidator.ValidateCanonical(Parse(Graph.Replace(original, broken)), "rb");

        Assert.Equal(expectedPath, Assert.Single(problems).Path);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
