using System.Text.Json;

using AlertApi.Application.Validation;

namespace AlertApi.Tests.Application;

public sealed class PreferencesValidatorTests
{
    // The example of contracts/rules.md section 5: it must pass, or the contract and the api disagree.
    private const string Anna = """
        {"user_id": "anna", "timezone": "Europe/Warsaw",
         "by_severity": {"critical": ["sms"], "error": ["email"], "warning": ["email"], "info": ["ui"]},
         "quiet_hours": {"start": "20:00", "end": "08:00", "downgrade": {"call": "sms", "sms": "email"}},
         "wake_me": false, "max_push_per_hour": 10}
        """;

    [Fact]
    public void Validate_ContractExample_HasNoProblems()
    {
        var problems = PreferencesValidator.Validate(Parse(Anna));

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("\"sms\"", "\"pager\"", "by_severity.critical[0]")]
    [InlineData("\"20:00\"", "\"8pm\"", "quiet_hours.start")]
    [InlineData("\"max_push_per_hour\": 10", "\"max_push_per_hour\": 0", "max_push_per_hour")]
    [InlineData("\"max_push_per_hour\": 10", "\"max_push_per_hour\": \"many\"", "max_push_per_hour")]
    [InlineData("\"wake_me\": false", "\"wake_me\": \"no\"", "wake_me")]
    [InlineData("\"timezone\"", "\"zone\"", "zone")]
    public void Validate_WrongValue_NamesThePath(string original, string replacement, string path)
    {
        var problems = PreferencesValidator.Validate(Parse(Anna.Replace(original, replacement)));

        Assert.Contains(problems, problem => problem.Path == path);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }
}
