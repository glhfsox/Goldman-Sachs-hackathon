using Notifier.Domain.Exceptions;
using Notifier.Domain.Models;
using Notifier.Domain.ValueObjects;

namespace Notifier.Domain.Tests;

public sealed class NotificationTests
{
    [Fact]
    public void Id_is_stable_for_the_same_trigger()
    {
        var first = NotificationId.For("alert-1", "marek", "sms", "change-1");
        var again = NotificationId.For("alert-1", "marek", "sms", "change-1");
        var otherChannel = NotificationId.For("alert-1", "marek", "email", "change-1");

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherChannel);
    }

    [Fact]
    public void Retry_counts_the_attempt_and_moves_send_at()
    {
        var notification = Create();

        notification.ScheduleRetry("gateway down", TestData.Now.AddSeconds(4), TestData.Now);

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal(1, notification.Attempts);
        Assert.Equal(TestData.Now.AddSeconds(4), notification.SendAt);
        Assert.True(notification.IsOpen);
    }

    [Fact]
    public void A_sent_notification_cannot_be_cancelled()
    {
        var notification = Create();
        notification.MarkSent("text", "provider-1", TestData.Now);

        Assert.Throws<InvalidStateTransitionException>(() => notification.Cancel("acked", TestData.Now));
    }

    [Fact]
    public void Reroute_keeps_the_original_reason()
    {
        var notification = Create();

        notification.Reroute(Channel.Email, "quiet hours", TestData.Now);

        Assert.Equal("email", notification.Channel);
        Assert.Contains("rule test", notification.Reason);
        Assert.Contains("quiet hours", notification.Reason);
    }

    [Fact]
    public void An_older_alert_change_does_not_overwrite_a_newer_one()
    {
        var alert = Alert.Create("alert-1", "rule", null, AlertStatus.Acked, "error", "{}", TestData.Now);

        var applied = alert.Apply(AlertStatus.Open, "error", null, "{}", TestData.Now.AddSeconds(-1));

        Assert.False(applied);
        Assert.Equal(AlertStatus.Acked, alert.Status);
    }

    private static Notification Create()
    {
        return Notification.Create(
            NotificationId.For("alert-1", "marek", "sms", "change-1"),
            "alert-1",
            null,
            "alert-1",
            TestData.Recipient(),
            Channel.Sms,
            "change-1",
            0,
            "rule test",
            TestData.Content(),
            TestData.Now,
            TestData.Now);
    }
}
