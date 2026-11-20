# Notifier

Policy and delivery stage of the pipeline: consumes `alerts.changes`, decides who is told, when and on which
channel, and delivers with retry, backoff and a fallback channel. Everything it needs to send arrives in the
Kafka record (`delivery` block, [contracts/events.md](../../contracts/events.md) §4); it owns its own
`notifier` database and never reads rules, users or preferences.

## Run

```bash
docker compose -f backend/notifier/docker-compose.dev.yml up -d --build   # postgres, redis, kafka, mailhog, mock-gateway, notifier
backend/notifier/dev/send.sh backend/notifier/dev/samples/settlements-opened.json
```

| Where | What you see |
|---|---|
| http://localhost:8002/deliveries | SMS and calls received by the mock gateway, with timestamps |
| http://localhost:8025 | Email in MailHog |
| `docker compose -f backend/notifier/docker-compose.dev.yml logs -f notifier` | Decisions, timeline entries, auto-resolves |
| `psql postgres://alerts:alerts@localhost:5432/notifier -c "select user_id, channel, status, reason from notifications"` | Every notification with "why" and "why not" |

Run the worker from the IDE instead: start the stack without `notifier`, then `dotnet run --project src/Notifier.Worker`
(the launch profile points at the local ports). Checks: `bash check.sh`.

## Demo scenarios

| Sample | Shows |
|---|---|
| `settlements-opened.json` | critical: call + SMS + UI at once to the on-call person, runbook in the message |
| `anna-payments-critical.json` | Anna's rule: SMS between 08:00 and 20:00 Warsaw time, email otherwise |
| `rates-warning-opened.json`, then `rates-acked.json` | warning: UI now, email after 1 min (Development; 5 min by default), call to on-call after another 1 min. The ack cancels both: `cancelled`, "suppressed by acked" |
| `curl -X POST "localhost:8002/outage?seconds=60"` before a sample | SMS/call get 503: retried with backoff, then `dead` and a fallback row (call → sms → email → ui) |
| wait `window_s` after a sample | the alert auto-resolves and a `resolved` change with `actor: auto` is published |

## How it works

```
alerts.changes ─▶ AlertChangesWorker ─▶ AlertChangeService ─▶ notifications (pending, send_at = now or later)
                                                   │
DeliveryWorker ─▶ claim due rows (FOR UPDATE SKIP LOCKED) ─▶ DeliveryGate ─▶ IChannelAdapter ─▶ sent / retry / dead + fallback
SchedulerWorker ─▶ tracked_alerts past window_s ─▶ resolve
every state change ─▶ outbox_messages ─▶ OutboxWorker ─▶ Kafka alerts.changes, Redis ui.alerts, shared alert_events
```

- **Fewer messages:** counter bumps (`count_threshold`) never notify; non-critical alerts go to the UI first
  and reach email/SMS only if nobody acks within `Escalation:PushDelayMinutes`; a person matched by several
  rules of one incident gets one message; `max_push_per_hour` folds the excess into "and N more"; quiet hours
  downgrade a channel instead of dropping it, and `wake_me` lets critical through.
- **No lost or duplicate messages:** Kafka offsets are committed after the database commit; `change_id` and the
  deterministic notification id make redelivery a no-op; the gateway call carries the notification id.
- **Replicas:** any number. Rows are claimed with leases (`send_at` / `locked_until`) and `SKIP LOCKED`.

## Adding a channel or a real provider

One class implementing `IChannelAdapter` in `src/Notifier.Infrastructure/Channels/`, one line in `Program.cs`
(`AddScoped<IChannelAdapter, MyChannel>()`), and its name in `Channels:Enabled`. To use a real SMS provider
instead of the mock, point `SMS_GATEWAY_URL` at it or replace `GatewayClient`.

## What is mocked

`Notifier.MockGateway` (port 8002) stands in for the SMS and phone-call providers: it logs each delivery with
a timestamp, is idempotent on `notification_id`, and can simulate an outage (`POST /outage?seconds=N`,
`DELETE /outage`). Email goes to MailHog over plain SMTP.

## Configuration

| Env | Example |
|---|---|
| `NOTIFIER_DATABASE_URL` (required) | `postgres://alerts:alerts@postgres:5432/notifier`; migrated on start |
| `KAFKA_BROKERS` (required) | `kafka:9092` |
| `REDIS_URL` (required) | `redis://redis:6379/0` |
| `DATABASE_URL` (optional) | shared `alerts` DB; when set, timeline rows go to `alert_events` and auto-resolve updates `alerts`. Without it they are only logged |
| `SMTP_HOST`, `SMTP_PORT`, `SMS_GATEWAY_URL`, `CALL_GATEWAY_URL`, `PUBLIC_BASE_URL` | see contracts/services.md |

Escalation delays, retry/backoff, fallback map and enabled channels are in `src/Notifier.Worker/appsettings.json`.

Migrations: `dotnet ef migrations add <Name> -p src/Notifier.Infrastructure -s src/Notifier.Worker -o Migrations`.
