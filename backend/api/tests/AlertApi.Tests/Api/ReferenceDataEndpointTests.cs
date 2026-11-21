using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AlertApi.Infrastructure.Data;
using AlertApi.Tests.Infrastructure;

namespace AlertApi.Tests.Api;

[Trait("Category", "Integration")]
public sealed class ReferenceDataEndpointTests : IClassFixture<PostgresFixture>
{
    private const string Preferences = """
        {"user_id": "elena", "timezone": "Europe/Warsaw", "by_severity": {"critical": ["sms", "email"]}, "wake_me": true}
        """;

    private readonly PostgresFixture _postgres;

    public ReferenceDataEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task GetTeams_ReturnsSeededTeamsWithMembersAndOnCall()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var teams = await client.GetFromJsonAsync<JsonElement>("/api/teams");

        Assert.Equal(["payments", "pricing", "rates", "settlements"], teams.EnumerateArray().Select(team => team.GetProperty("id").GetString()));
        var settlements = teams.EnumerateArray().Single(team => team.GetProperty("id").GetString() == "settlements");
        var members = settlements.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["boris", "elena"], members.Select(member => member.GetProperty("user_id").GetString()));
        Assert.True(members[0].GetProperty("on_call").GetBoolean());
        Assert.False(members[1].GetProperty("on_call").GetBoolean());
    }

    [Fact]
    public async Task GetUsers_ReturnsSeededUsers()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var users = await client.GetFromJsonAsync<JsonElement>("/api/users");

        Assert.Equal(["anna", "boris", "clara", "dmytro", "elena"], users.EnumerateArray().Select(user => user.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task PutPreferences_ThenGet_ReturnsThemAndQueuesOneConfigRecord()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var before = await client.GetAsync("/api/users/elena/preferences");
        var saved = await client.PutAsync("/api/users/elena/preferences", Json(Preferences));
        var after = await client.GetFromJsonAsync<JsonElement>("/api/users/elena/preferences");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(after.GetProperty("wake_me").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Outbox
            .Where(message => message.Key == "preferences:elena")
            .ToListAsync();
        var record = Assert.Single(records);
        Assert.Equal("config.changes", record.Topic);
        Assert.True(JsonDocument.Parse(record.Payload).RootElement.GetProperty("body").GetProperty("wake_me").GetBoolean());
    }

    [Fact]
    public async Task PutPreferences_ReplacesSeededOnes()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var saved = await client.PutAsync("/api/users/anna/preferences", Json(Preferences.Replace("elena", "anna")));
        var after = await client.GetFromJsonAsync<JsonElement>("/api/users/anna/preferences");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(after.GetProperty("wake_me").GetBoolean());
    }

    [Fact]
    public async Task PutPreferences_InvalidOrForeign_Returns422WithPaths()
    {
        using var factory = ApiFactory.Create(_postgres, "Production", enableSwagger: false);
        using var client = factory.CreateClient();

        var invalid = await client.PutAsync("/api/users/elena/preferences", Json(Preferences.Replace("\"sms\"", "\"pager\"")));
        var foreign = await client.PutAsync("/api/users/elena/preferences", Json(Preferences.Replace("elena", "anna")));
        var unknownUser = await client.PutAsync("/api/users/nobody/preferences", Json(Preferences));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_preferences", body.GetProperty("error").GetString());
        Assert.Equal("by_severity.critical[0]", body.GetProperty("details")[0].GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownUser.StatusCode);
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
