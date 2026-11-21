using Notifier.Domain.Models;
using Notifier.Domain.Services;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Tests;

public sealed class RecipientResolverTests
{
    private static readonly Dictionary<string, User> Users = new()
    {
        ["marek"] = User.Create("marek", "Marek", "marek@example.com", "+48000000001", TestData.Now),
        ["ola"] = User.Create("ola", "Ola", "ola@example.com", "+48000000002", TestData.Now),
        ["anna"] = User.Create("anna", "Anna", "anna@example.com", "+48000000003", TestData.Now)
    };

    private static readonly List<Membership> Memberships =
    [
        Membership.Create("settlements", "marek", onCall: true, TestData.Now),
        Membership.Create("settlements", "ola", onCall: false, TestData.Now)
    ];

    private readonly RecipientResolver _resolver = new();

    [Fact]
    public void On_call_route_picks_only_the_on_call_member_with_the_rule_channels()
    {
        var route = new RouteSpec([new RecipientSpec("settlements", null, onCallOnly: true)], ["call", "sms", "ui"]);

        var recipients = _resolver.Resolve("settlements-workflow-failed", route, Severity.Critical, Memberships, Users, Preferences());

        var marek = Assert.Single(recipients);
        Assert.Equal("marek", marek.UserId);
        Assert.True(marek.OnCall);
        Assert.Equal(["call", "sms", "ui"], marek.Channels);
        Assert.Contains("on-call", marek.Why);
    }

    [Fact]
    public void Without_route_channels_each_person_preferences_decide()
    {
        var route = new RouteSpec([new RecipientSpec(null, "anna", onCallOnly: false)], channels: null);
        var preferences = Preferences(("anna", new PreferenceSettings(
            "Europe/Warsaw",
            new Dictionary<string, IReadOnlyList<string>> { ["critical"] = ["sms"] },
            TestData.EveningQuietHours(),
            wakeMe: false,
            maxPushPerHour: 10)));

        var anna = Assert.Single(_resolver.Resolve("anna-payments-critical", route, Severity.Critical, Memberships, Users, preferences));

        Assert.Equal(["sms"], anna.Channels);
        Assert.NotNull(anna.QuietHours);
        Assert.Equal(10, anna.MaxPushPerHour);
    }

    [Fact]
    public void A_person_reached_twice_is_one_recipient()
    {
        var route = new RouteSpec(
            [new RecipientSpec("settlements", null, onCallOnly: false), new RecipientSpec(null, "marek", onCallOnly: false)],
            ["email"]);

        var recipients = _resolver.Resolve("rule", route, Severity.Error, Memberships, Users, Preferences());

        Assert.Equal(2, recipients.Count);
        Assert.Single(recipients, recipient => recipient.UserId == "marek");
    }

    [Fact]
    public void No_preferences_and_no_route_channels_means_ui()
    {
        var route = new RouteSpec([new RecipientSpec(null, "ola", onCallOnly: false)], channels: null);

        var ola = Assert.Single(_resolver.Resolve("rule", route, Severity.Warning, Memberships, Users, Preferences()));

        Assert.Equal(["ui"], ola.Channels);
    }

    private static Dictionary<string, PreferenceSettings> Preferences(params (string UserId, PreferenceSettings Settings)[] entries) =>
        entries.ToDictionary(entry => entry.UserId, entry => entry.Settings);
}
