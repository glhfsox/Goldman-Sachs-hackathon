using System.Text.Json;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.Validation;

namespace AlertApi.Tests.Application;

public sealed class ActionTargetValidatorTests
{
    [Fact]
    public void ValidTarget_HasNoProblems()
    {
        Assert.Empty(ActionTargetValidator.Validate("orders-db", Request(
            "http://db-admin:8080",
            """["http://orders-db"]""",
            """{"reload_db": {"method": "POST", "path": "/instances/{instance}/reload", "risk": "auto"}}""")));
    }

    [Theory]
    [InlineData("Orders DB", "http://a", "[]", """{"x": {"method": "GET", "path": "/"}}""", "id")]
    [InlineData("t", "not a url", "[]", """{"x": {"method": "GET", "path": "/"}}""", "base_url")]
    [InlineData("t", "http://a", """["http://a/path"]""", """{"x": {"method": "GET", "path": "/"}}""", "origins[0]")]
    [InlineData("t", "http://a", "[]", "{}", "operations")]
    [InlineData("t", "http://a", "[]", """{"x": {"method": "PATCH", "path": "/"}}""", "operations.x.method")]
    [InlineData("t", "http://a", "[]", """{"x": {"method": "GET", "path": "no-slash"}}""", "operations.x.path")]
    [InlineData("t", "http://a", "[]", """{"x": {"method": "GET", "path": "/", "risk": "yolo"}}""", "operations.x.risk")]
    public void OneWrongField_IsReportedAtItsPath(string id, string baseUrl, string origins, string operations, string expectedPath)
    {
        var problems = ActionTargetValidator.Validate(id, Request(baseUrl, origins, operations));

        Assert.Equal(expectedPath, Assert.Single(problems).Path);
    }

    private static PutActionTargetRequest Request(string baseUrl, string origins, string operations)
    {
        return new PutActionTargetRequest(baseUrl, Parse(origins), Parse(operations), SecretRef: null);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
