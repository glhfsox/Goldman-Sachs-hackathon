using System.Text.Json;

using AlertApi.Application.Conditions;

namespace AlertApi.Tests.Application;

public sealed class ConditionEvaluatorTests
{
    private const string Envelope = """
        {"type": "trade.rejected.v1", "severity": "error", "env": "prod", "service": "pricing-engine-2",
         "raw": {"payload": {"notional": 25000000, "desk": "rates", "flag": true, "tags": ["a", "b"], "none": null},
                 "error": {"code": "TIMEOUT"}, "count": 1}}
        """;

    [Theory]
    [InlineData("""{"field": "type", "op": "eq", "value": "trade.rejected.v1"}""", true)]
    [InlineData("""{"field": "type", "op": "ne", "value": "trade.rejected.v1"}""", false)]
    [InlineData("""{"field": "error.code", "op": "eq", "value": "TIMEOUT"}""", true)]
    [InlineData("""{"field": "raw.error.code", "op": "eq", "value": "TIMEOUT"}""", true)]
    [InlineData("""{"field": "payload.notional", "op": "gt", "value": 10000000}""", true)]
    [InlineData("""{"field": "payload.notional", "op": "gte", "value": 25000000}""", true)]
    [InlineData("""{"field": "payload.notional", "op": "lt", "value": 25000000}""", false)]
    [InlineData("""{"field": "payload.notional", "op": "lte", "value": 25000000.0}""", true)]
    [InlineData("""{"field": "payload.desk", "op": "gt", "value": 1}""", false)]
    [InlineData("""{"field": "payload.desk", "op": "in", "value": ["fx", "rates"]}""", true)]
    [InlineData("""{"field": "payload.desk", "op": "in", "value": ["fx"]}""", false)]
    [InlineData("""{"field": "service", "op": "glob", "value": "pricing-engine*"}""", true)]
    [InlineData("""{"field": "service", "op": "glob", "value": "*engine-2"}""", true)]
    [InlineData("""{"field": "service", "op": "glob", "value": "pri*eng*2"}""", true)]
    [InlineData("""{"field": "service", "op": "glob", "value": "pricing"}""", false)]
    [InlineData("""{"field": "service", "op": "glob", "value": "engine*"}""", false)]
    [InlineData("""{"field": "severity", "op": "sev_gte", "value": "warning"}""", true)]
    [InlineData("""{"field": "severity", "op": "sev_gte", "value": "critical"}""", false)]
    [InlineData("""{"field": "severity", "op": "sev_gte", "value": "loud"}""", false)]
    [InlineData("""{"field": "payload.none", "op": "exists"}""", true)]
    [InlineData("""{"field": "payload.missing", "op": "exists"}""", false)]
    [InlineData("""{"field": "payload.missing", "op": "ne", "value": "x"}""", false)]
    public void Leaf_FollowsTheConditionLanguage(string condition, bool expected)
    {
        Assert.Equal(expected, ConditionEvaluator.Evaluate(Parse(condition), Parse(Envelope)));
    }

    [Theory]
    [InlineData("""{"field": "payload.flag", "op": "eq", "value": true}""", true)]
    [InlineData("""{"field": "payload.flag", "op": "eq", "value": 1}""", false)]
    [InlineData("""{"field": "count", "op": "eq", "value": true}""", false)]
    [InlineData("""{"field": "count", "op": "eq", "value": 1.0}""", true)]
    [InlineData("""{"field": "payload.tags", "op": "eq", "value": ["a", "b"]}""", true)]
    [InlineData("""{"field": "payload.tags", "op": "eq", "value": ["b", "a"]}""", false)]
    [InlineData("""{"field": "payload.none", "op": "eq", "value": null}""", true)]
    [InlineData("""{"field": "payload.flag", "op": "gt", "value": 0}""", false)]
    public void Equality_IsJsonEquality(string condition, bool expected)
    {
        Assert.Equal(expected, ConditionEvaluator.Evaluate(Parse(condition), Parse(Envelope)));
    }

    [Fact]
    public void Groups_NestAndShortCircuitLikeAllAndAny()
    {
        var condition = Parse("""
            {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"},
                     {"any": [{"field": "payload.notional", "op": "gt", "value": 99000000},
                              {"field": "payload.desk", "op": "in", "value": ["rates"]}]}]}
            """);

        Assert.True(ConditionEvaluator.Evaluate(condition, Parse(Envelope)));
    }

    [Fact]
    public void EmptyGroups_AreTrueForAllAndFalseForAny()
    {
        Assert.True(ConditionEvaluator.Evaluate(Parse("""{"all": []}"""), Parse(Envelope)));
        Assert.False(ConditionEvaluator.Evaluate(Parse("""{"any": []}"""), Parse(Envelope)));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"all": 3}""")]
    [InlineData("""{"field": 1, "op": "eq", "value": 1}""")]
    [InlineData("""{"field": "type", "op": "like", "value": "x"}""")]
    [InlineData("""{"field": "type", "op": "eq"}""")]
    [InlineData("""{"field": "type", "op": "in", "value": "x"}""")]
    public void AMalformedNode_IsFalse_AndNeverThrows(string condition)
    {
        Assert.False(ConditionEvaluator.Evaluate(Parse(condition), Parse(Envelope)));
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
