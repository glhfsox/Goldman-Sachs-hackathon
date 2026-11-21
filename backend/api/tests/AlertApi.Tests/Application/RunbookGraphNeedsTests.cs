using System.Text.Json;

using AlertApi.Application.Runbooks;

namespace AlertApi.Tests.Application;

public sealed class RunbookGraphNeedsTests
{
    [Fact]
    public void StepAfterATextStep_WaitsForWhatTheTextStepWaitedFor_NotForAPerson()
    {
        var needs = Effective("""
            {"steps": [{"text": "a", "action": "http_get"}, {"text": "read the dashboard"}, {"text": "c", "action": "http_get"}]}
            """);

        Assert.Equal(["step-0"], needs["step-2"]);
        Assert.Equal(["step-0"], needs["step-1"]);
    }

    [Fact]
    public void DeclaredNeedOnATextStep_IsKept()
    {
        var needs = Effective("""
            {"steps": [{"id": "a", "text": "a", "action": "http_get"}, {"id": "t", "text": "tick me", "needs": ["a"]},
                       {"id": "c", "text": "c", "action": "http_get", "needs": ["t"]}]}
            """);

        Assert.Equal(["t"], needs["c"]);
    }

    [Fact]
    public void FirstStepWithoutNeeds_WaitsForNothing()
    {
        Assert.Empty(Effective("""{"steps": [{"text": "a"}, {"text": "b", "action": "http_get"}]}""")["step-1"]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Effective(string canonical)
    {
        return RunbookGraph.EffectiveNeeds(RunbookGraph.Parse(JsonDocument.Parse(canonical).RootElement));
    }
}
