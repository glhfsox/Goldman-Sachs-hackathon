namespace AlertApi.Tests.Api;

// Kafka records as the processor and the notifier send them (contracts/events.md sections 4, 7 and 8).
public static class ActivityRecords
{
    public static string AlertChange(
        string changeId,
        string change,
        string alertId = "alert-1",
        string team = "settlements",
        string status = "open",
        string severity = "critical",
        long count = 1,
        string firstSeen = "2026-11-20T10:15:01.000Z",
        string? ackedBy = null,
        string sampleEvent = """{"type": "WORKFLOW_FAILED", "mapped": true}""",
        string? runbookId = "settlement-timeout")
    {
        var runbook = runbookId is null ? "null" : $"\"{runbookId}\"";
        var acked = ackedBy is null ? "null" : $"\"{ackedBy}\"";

        return $$$"""
            {"change_id": "{{{changeId}}}", "change": "{{{change}}}", "at": "2026-11-20T10:15:02.180Z", "actor": "processor",
             "alert": {"id": "{{{alertId}}}", "rule_id": "settlements-workflow-failed", "team_id": "{{{team}}}", "incident_id": null,
                       "status": "{{{status}}}", "severity": "{{{severity}}}", "title": "order-settlement failed at step settle",
                       "body": "Counterparty service did not respond in 5000 ms",
                       "fields": {"step": "settle", "error.code": "TIMEOUT"}, "color": "red", "count": {{{count}}},
                       "first_seen": "{{{firstSeen}}}", "last_seen": "2026-11-20T10:15:01.000Z",
                       "first_received_at": "2026-11-20T10:15:02.123Z", "runbook_id": {{{runbook}}},
                       "acked_by": {{{acked}}}, "resolved_by": null,
                       "sample_event": {{{sampleEvent}}} }}
            """;
    }

    public static string Timeline(string entryId, string kind = "notification_sent", string status = "sent", string alertId = "alert-1")
    {
        return $$$"""
            {"entry_id": "{{{entryId}}}", "alert_id": "{{{alertId}}}", "at": "2026-11-20T10:15:03.412Z", "actor": "notifier",
             "kind": "{{{kind}}}",
             "data": {"notification": {"id": "n-1", "user_id": "marek", "channel": "sms", "status": "{{{status}}}",
                                       "reason": "rule settlements-workflow-failed, on-call",
                                       "send_at": "2026-11-20T10:15:02.200Z", "sent_at": "2026-11-20T10:15:03.410Z", "error": null} }}
            """;
    }

    public static string SchemaSample(string fingerprint = "fp-1")
    {
        return $$$"""
            {"fingerprint": "{{{fingerprint}}}", "source_id": "trades-kafka", "seen_at": "2026-11-20T10:15:02.123Z",
             "sample": {"meta": {"schema": "trade.rejected.v1"}, "payload": {"tradeId": "T-88121", "desk": "rates"} }}
            """;
    }
}
