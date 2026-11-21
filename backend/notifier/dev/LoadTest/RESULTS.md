# Notifier load test: summary

Measured on 21-11 with `dev/LoadTest` on a laptop (Docker Desktop, 16 CPUs), running inside the compose network
so every timestamp comes from one clock. Notifier stage only: a record produced on `alerts.changes` →
notification planned → sent by the channel adapter (UI, email to MailHog, SMS to the mock gateway).
Each alert is critical and goes to 3 people × [ui, email, sms] = 9 notifications.

| Scenario | Version | Throughput | p50 | p95 | p99 |
|---|---|---|---|---|---|
| Steady, 10 alerts/s for 30 s (2,700 notifications) | before | 85/s (keeps up) | 391 ms | 803 ms | 1,798 ms |
| | **after** | 86/s (keeps up) | **18 ms** | **23 ms** | **27 ms** |
| Burst, 1,000 alerts at once (9,000 notifications) | before | 190/s | 23.6 s | 45.5 s | 46.9 s |
| | **after** | **622/s** (3.3×) | 7.7 s | 13.9 s | 14.3 s |
| | after, 3 replicas | 637/s | 6.9 s | 13.4 s | 13.9 s |

Latency is produced → sent, all channels. In a burst it is mostly queueing time behind the earlier alerts.

### Batch planning (21-11, later)
`alerts.changes` is now consumed up to 100 fetched records at a time, one transaction and one query per lookup
per batch (was ~9 round trips and a commit per alert); EF savepoints are off. Same machine, fresh stack, warmed up.

| Scenario | Throughput | produced → planned p50 / p99 | produced → sent p50 / p95 / p99 |
|---|---|---|---|
| Burst, 1,000 alerts × 1 notification (ui) | planning ~1,100 alerts/s (was 137) | 442 ms / 912 ms | 768 ms / 1.3 s / 1.4 s |
| Burst, 1,000 alerts × 9 notifications | planning ~245 alerts/s (was 70); sent 590/s | 2.2 s / 4.1 s | 9.3 s / 14.7 s / 15.1 s |
| Steady, 10 alerts/s × 9 notifications | 90/s (keeps up) | 62 ms / 68 ms | 75 ms / 83 ms / 90 ms |

Planning is no longer the limit: in the 9-notification burst the rows now wait in front of delivery
(planned → sent p50 7 s), so end-to-end throughput did not move. Steady planning has an unexplained fixed ~60 ms
(was 7 ms in the run above); not investigated yet.

## What changed
1. Rows due now wake the delivery loop right after the commit (no 500 ms poll wait).
2. Sends run in parallel (16 per replica); per-person rate counters are row-locked, so the push limit stays exact.
3. SMTP connections are pooled.
4. Configuration copies are cached in memory and invalidated on `config.changes`; planning one alert takes
   ~5 queries instead of ~30.
5. Kafka offsets are stored after the database commit and committed in the background (still at-least-once).
6. The outbox publishes a whole batch at once.
7. A cleanup loop deletes old processed outbox records, processed change ids and finished notifications.

## Where the limit is now
- With batch planning, delivery is the bottleneck again (~600 notifications/s): each notification costs 2–3
  commits (claim, status update + outbox insert, outbox mark) on the one Postgres, whose backends mostly wait on
  `WALWrite` / `WALSync`. 3 replicas did not raise throughput before batching (622 → 637/s), consistent with the
  shared database being the limit. Next: fewer commits per notification, outbox marked in one statement per batch.
- Retry still works with parallel sends: a 10 s gateway outage delayed 5 SMS by ~14 s; all were sent (up to 4 attempts).

## Reproduce
```bash
docker compose -f backend/notifier/docker-compose.dev.yml down   # no database volume: gives an empty table and an empty gateway log
docker compose -f backend/notifier/docker-compose.dev.yml up -d --build
docker run --rm --network notifier-dev_default -v <repo>/backend/notifier/dev/LoadTest:/src mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c "cd /src && dotnet run -c Release -- --label after-burst --alerts 1000 --rate 0 --brokers kafka:9092 \
  --db 'Host=notifier-db;Port=5432;Database=notifier;Username=alerts;Password=alerts' --out /src \
  --mailhog http://mailhog:8025 --gateway http://mock-gateway:8002"
```
Per-run details: `results-*.md` in this folder.
