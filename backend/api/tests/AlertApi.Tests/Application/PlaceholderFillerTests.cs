using System.Text.Json;

using AlertApi.Application.Runs;

namespace AlertApi.Tests.Application;

public sealed class PlaceholderFillerTests
{
    private const string Envelope = """
        {"source": "workflows-kafka", "type": "WORKFLOW_FAILED", "severity": "critical", "env": "prod",
         "raw": {"workflowId": "WF-17", "error": {"code": "TIMEOUT"}, "attempts": 3, "dry": false,
                 "items": [{"id": "a"}], "tricky": "{{type}}"}}
        """;

    [Theory]
    [InlineData("{{type}}", "WORKFLOW_FAILED")]
    [InlineData("{{workflowId}}", "WF-17")]
    [InlineData("{{error.code}}", "TIMEOUT")]
    [InlineData("{{raw.error.code}}", "TIMEOUT")]
    [InlineData("{{ attempts }}", "3")]
    [InlineData("{{dry}}", "false")]
    [InlineData("{{items.0.id}}", "a")]
    [InlineData("retry {{workflowId}} in {{env}}", "retry WF-17 in prod")]
    public void Path_IsReadFromTheEnvelopeThenFromRaw(string template, string expected)
    {
        var missing = new List<string>();

        var text = PlaceholderFiller.FillText(template, Parse(Envelope), missing);

        Assert.Equal(expected, text);
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("{{nope}}")]
    [InlineData("{{error}}")]
    [InlineData("{{items.5.id}}")]
    [InlineData("{{error.code.deeper}}")]
    public void MissingOrNonScalarPath_IsReported_AndLeftInPlace(string template)
    {
        var missing = new List<string>();

        var text = PlaceholderFiller.FillText(template, Parse(Envelope), missing);

        Assert.Equal(template, text);
        Assert.Single(missing);
    }

    [Fact]
    public void ValueThatLooksLikeAPlaceholder_IsNotExpandedAgain()
    {
        var filled = PlaceholderFiller.Fill(Parse("""{"x": "{{tricky}}"}"""), Parse(Envelope));

        Assert.Equal("{{type}}", filled.Params!.Value.GetProperty("x").GetString());
        Assert.Empty(filled.Missing);
    }

    [Fact]
    public void Params_AreFilledInNestedStrings_ButNotInNamesOrOtherValues()
    {
        var filled = PlaceholderFiller.Fill(
            Parse("""{"{{type}}": 1, "a": {"b": ["{{workflowId}}", 7, true, null]}, "c": "{{missing}}"}"""),
            Parse(Envelope));

        Assert.Equal("""{"{{type}}":1,"a":{"b":["WF-17",7,true,null]},"c":"{{missing}}"}""", filled.Params!.Value.GetRawText());
        Assert.Equal(["missing"], filled.Missing);
    }

    [Fact]
    public void NoParams_FillsToNothing()
    {
        var filled = PlaceholderFiller.Fill(null, Parse(Envelope));

        Assert.Null(filled.Params);
        Assert.Empty(filled.Missing);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
