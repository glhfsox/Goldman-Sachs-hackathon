using System.Text.Json;

using AlertApi.Application.Validation;

namespace AlertApi.Tests.Application;

// The valid examples are the ones in contracts/rules.md: they must pass, or the contract and the api disagree.
public sealed class ValidatorTests
{
    private const string SettlementsRule = """
        {"id": "settlements-workflow-failed", "team_id": "settlements", "name": "Order settlement failed in prod", "enabled": true,
         "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                          {"field": "raw.workflowName", "op": "eq", "value": "order-settlement"}]},
         "alert_key": ["raw.workflowName", "raw.error.code"], "window_s": 300,
         "route": {"recipients": [{"team": "settlements", "on_call": true}], "channels": ["call", "sms", "ui"]},
         "present": {"title": "{{workflowName}} failed at step {{step}}", "body": "{{error.message}}",
                     "fields": ["step", "error.code"], "color": "red"},
         "runbook_id": "settlement-timeout"}
        """;

    private const string RatesRule = """
        {"id": "rates-large-rejections", "team_id": "rates",
         "when": {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"},
                          {"any": [{"field": "payload.notional", "op": "gt", "value": 10000000},
                                   {"field": "payload.reason", "op": "in", "value": ["LIMIT_BREACH"]},
                                   {"field": "severity", "op": "sev_gte", "value": "error"},
                                   {"field": "raw.amount", "op": "exists"}]}]},
         "route": {"recipients": [{"user": "anna"}]}}
        """;

    private const string TradeMapping = """
        {"id": "trade-rejected-v1", "status": "active", "origin": "llm", "priority": 100,
         "match": {"all": [{"field": "raw.meta.schema", "op": "glob", "value": "trade.rejected*"}]},
         "fields": {"type": {"path": "meta.schema"}, "severity": {"const": "error"}, "ts": {"path": "meta.ts", "format": "epoch_ms"}},
         "severity_map": {"P1": "critical", "WARN": "warning"},
         "dedup_fields": ["payload.desk", "payload.reason"]}
        """;

    [Theory]
    [InlineData(SettlementsRule)]
    [InlineData(RatesRule)]
    public void Rule_FromTheContract_IsValid(string json)
    {
        Assert.Empty(RuleValidator.Validate(Parse(json)));
    }

    [Fact]
    public void Mapping_FromTheContract_IsValid()
    {
        Assert.Empty(MappingValidator.Validate(Parse(TradeMapping)));
    }

    [Fact]
    public void Rule_WithUnknownOperator_ReportsTheContractPath()
    {
        var json = SettlementsRule.Replace("\"op\": \"eq\", \"value\": \"order-settlement\"", "\"op\": \"gtt\", \"value\": 1");

        var problem = Assert.Single(RuleValidator.Validate(Parse(json)));

        Assert.Equal("when.all[1].op", problem.Path);
        Assert.Equal("unknown op 'gtt'", problem.Message);
    }

    [Theory]
    [InlineData("\"window_s\": 300", "\"window_s\": 0", "window_s")]
    [InlineData("\"window_s\": 300", "\"window_s\": \"soon\"", "window_s")]
    [InlineData("\"color\": \"red\"", "\"color\": \"pink\"", "present.color")]
    [InlineData("\"channels\": [\"call\", \"sms\", \"ui\"]", "\"channels\": [\"fax\"]", "route.channels[0]")]
    [InlineData("{\"team\": \"settlements\", \"on_call\": true}", "{\"team\": \"settlements\", \"user\": \"anna\"}", "route.recipients[0]")]
    [InlineData("\"enabled\": true", "\"enabled\": \"yes\"", "enabled")]
    [InlineData("{{error.message}}", "{{error..message}}", "present.body")]
    [InlineData("\"alert_key\": [\"raw.workflowName\", \"raw.error.code\"]", "\"alert_key\": [\"raw..x\"]", "alert_key[0]")]
    [InlineData("\"runbook_id\": \"settlement-timeout\"", "\"runbok_id\": \"settlement-timeout\"", "runbok_id")]
    public void Rule_WithOneWrongField_ReportsOnlyThatPath(string original, string broken, string expectedPath)
    {
        var problems = RuleValidator.Validate(Parse(SettlementsRule.Replace(original, broken)));

        Assert.Equal(expectedPath, Assert.Single(problems).Path);
    }

    [Fact]
    public void Rule_WithoutRequiredFields_ReportsEachOne()
    {
        var problems = RuleValidator.Validate(Parse("{\"id\": \"r1\"}"));

        Assert.Equal(["route", "team_id", "when"], problems.Select(problem => problem.Path).Order());
    }

    [Theory]
    [InlineData("{\"field\": \"a\", \"op\": \"gt\", \"value\": \"x\"}")]
    [InlineData("{\"field\": \"a\", \"op\": \"in\", \"value\": \"x\"}")]
    [InlineData("{\"field\": \"a\", \"op\": \"glob\", \"value\": 1}")]
    [InlineData("{\"field\": \"a\", \"op\": \"sev_gte\", \"value\": \"fatal\"}")]
    [InlineData("{\"field\": \"a\", \"op\": \"exists\", \"value\": 1}")]
    [InlineData("{\"field\": \"a\", \"op\": \"eq\"}")]
    [InlineData("{\"field\": \"a..b\", \"op\": \"eq\", \"value\": 1}")]
    [InlineData("{\"all\": {}}")]
    [InlineData("{\"all\": [], \"any\": []}")]
    public void Condition_WithWrongShape_IsRejected(string json)
    {
        var problems = new List<AlertApi.Application.Exceptions.ProblemItem>();

        ConditionValidator.Validate(Parse(json), "when", problems);

        Assert.NotEmpty(problems);
    }

    [Theory]
    [InlineData("\"status\": \"active\"", "\"status\": \"on\"", "status")]
    [InlineData("\"origin\": \"llm\"", "\"origin\": \"bot\"", "origin")]
    [InlineData("\"priority\": 100", "\"priority\": \"high\"", "priority")]
    [InlineData("\"format\": \"epoch_ms\"", "\"format\": \"unix\"", "fields.ts.format")]
    [InlineData("\"severity\": {\"const\": \"error\"}", "\"severity\": {\"const\": \"error\", \"path\": \"x\"}", "fields.severity")]
    [InlineData("\"WARN\": \"warning\"", "\"WARN\": \"loud\"", "severity_map.WARN")]
    [InlineData("\"payload.desk\"", "\"payload..desk\"", "dedup_fields[0]")]
    public void Mapping_WithOneWrongField_ReportsOnlyThatPath(string original, string broken, string expectedPath)
    {
        var problems = MappingValidator.Validate(Parse(TradeMapping.Replace(original, broken)));

        Assert.Equal(expectedPath, Assert.Single(problems).Path);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
