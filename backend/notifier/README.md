# Notifier

Policy and delivery stage of the pipeline: consumes `alerts.changes`, decides who is told, when and on which
channel, and delivers with retry, backoff and a fallback channel. Everything it needs to send arrives in the
Kafka record (`delivery` block, [contracts/events.md](../../contracts/events.md) §4); it owns its own
`notifier` database and never reads rules, users or preferences.

## Run

```bash
docker compose -f backend/notifier/docker-compose.dev.yml up -d --build   # kafka (+topics), notifier-db, mailhog, mock-gateway, notifier
bash backend/notifier/dev/send.sh config.changes backend/notifier/dev/samples/config.jsonl   # teams, users, on-call, rules, runbook; once per fresh stack
bash backend/notifier/dev/send.sh alerts.changes backend/notifier/dev/samples/settlements-opened.json
bash backend/notifier/dev/e2e.sh   # automated check of the whole path; exits non-zero on failure
```
Port 9092 taken by another stack (e.g. processor)? Prefix the first command with `KAFKA_HOST_PORT=19092`.

| Where | What you see |
|---|---|
| http://localhost:8002/deliveries | SMS and calls received by the mock gateway, with timestamps |
| http://localhost:8025 | Email in MailHog |
| `docker compose -f backend/notifier/docker-compose.dev.yml logs -f notifier` | Startup, warnings (unknown rule, retries) |
| `docker compose -f backend/notifier/docker-compose.dev.yml exec notifier-db psql -U alerts -d notifier -c "select user_id, channel, status, reason from notifications"` | Every notification with "why" and "why not" (from the host: `postgres://alerts:alerts@localhost:5435/notifier`) |

Run the worker from the IDE instead: `docker compose -f backend/notifier/docker-compose.dev.yml up -d kafka-init notifier-db mailhog mock-gateway`,
then `dotnet run --project backend/notifier/src/Notifier.Worker` (the launch profile points at localhost:9092 and localhost:5435). Checks: `bash backend/notifier/check.sh`.

## Demo scenarios

| Sample | Shows |
|---|---|
| `settlements-opened.json` | critical: call + SMS + UI at once to the on-call person, runbook in the message |
| `anna-payments-critical.json` | Anna's rule: SMS between 08:00 and 20:00 Warsaw time, email otherwise |
| `rates-warning-opened.json`, then `rates-acked.json` | warning: UI now, email after 1 min (Development; 5 min by default). No call: the on-call call step applies to error and critical only. The ack cancels the pending email: `cancelled`, "suppressed by acked" |
| `curl -X POST "localhost:8002/outage?seconds=60"` before a sample | SMS/call get 503: retried with backoff, then `dead` and a fallback row (call → sms → email → ui) |

Send each sample once per 5 minutes or change its event_id: a reused event id counts as the same incident and its pushes are suppressed.

## How it works

```
alerts.changes ─▶ AlertChangesWorker ─▶ AlertChangeService ─▶ notifications (pending, send_at = now or later)
config.changes ─▶ ConfigChangesWorker ─▶ copies of rules, teams, users, memberships, preferences, runbooks
DeliveryWorker ─▶ claim due rows (FOR UPDATE SKIP LOCKED) ─▶ DeliveryGate ─▶ IChannelAdapter ─▶ sent / retry / dead + fallback
every notification status change ─▶ outbox ─▶ OutboxWorker ─▶ Kafka alerts.timeline
```

Auto-resolve is the processor's job (contracts/events.md §4).

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
| `DATABASE_URL` (required) | `postgres://alerts:alerts@notifier-db:5432/notifier` (host: `localhost:5435`); migrated on start |
| `KAFKA_BROKERS` (required) | `kafka:9092` |
| `SMTP_HOST`, `SMTP_PORT`, `SMS_GATEWAY_URL`, `CALL_GATEWAY_URL`, `PUBLIC_BASE_URL` | see contracts/services.md |

Escalation delays, retry/backoff, fallback map and enabled channels are in `src/Notifier.Worker/appsettings.json`.

Migrations: `dotnet ef migrations add <Name> -p src/Notifier.Infrastructure -s src/Notifier.Worker -o Migrations`.
