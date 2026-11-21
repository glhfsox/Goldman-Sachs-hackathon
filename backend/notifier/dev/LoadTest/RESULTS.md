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
- Delivery is no longer the bottleneck: in the burst, planned → sent went from p50 12.6 s to 26 ms.
- Planning (consuming `alerts.changes`) caps at ~65–75 alerts/s on this machine, and 3 replicas did not raise it
  (partitions were split 2/2/2). During the burst each replica used ~2 CPUs and Postgres ~3.6 CPUs, with
  backends waiting on `WALWrite`; turning off `synchronous_commit` did not help (583/s). The likely limit is
  the single shared Postgres and per-statement overhead, not fsync. Not investigated further.
- Retry still works with parallel sends: a 10 s gateway outage delayed 5 SMS by ~14 s; all were sent (up to 4 attempts).

## Reproduce
```bash
docker compose -f backend/notifier/docker-compose.dev.yml up -d --build
docker run --rm --network notifier-dev_default -v <repo>/backend/notifier/dev/LoadTest:/src mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c "cd /src && dotnet run -c Release -- --label after-burst --alerts 1000 --rate 0 --brokers kafka:9092 \
  --db 'Host=notifier-db;Port=5432;Database=notifier;Username=alerts;Password=alerts' --out /src"
```
Per-run details: `results-*.md` in this folder.
