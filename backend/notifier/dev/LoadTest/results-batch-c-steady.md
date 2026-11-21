# Notifier load test: batch-c-steady

- Run `20261121020421`, 2026-11-21 02:04 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 300 critical `opened` changes, 10/s (produced in 29.97 s), 3 recipients x [ui, email, sms] = 2700 notifications.
- Database/host clock offset 0.0 ms, corrected.

Rows: 2700 of 2700 (sent 2700).
Duplicates: MailHog received 900 emails for 900 sent email rows (0 extra). SMS duplicates are absorbed by the gateway's idempotency key.
Throughput: 2700 sent in 29.98 s = **90 notifications/s** (10 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 62 | 66 | 68 | 70 |
| email | planned -> sent | 15 | 25 | 32 | 49 |
| email | **produced -> sent** | 76 | 83 | 91 | 98 |
| sms | produced -> planned | 62 | 66 | 68 | 70 |
| sms | planned -> sent | 14 | 24 | 28 | 43 |
| sms | **produced -> sent** | 75 | 82 | 88 | 99 |
| ui | produced -> planned | 62 | 66 | 68 | 70 |
| ui | planned -> sent | 13 | 23 | 28 | 40 |
| ui | **produced -> sent** | 74 | 82 | 90 | 94 |
| all | produced -> planned | 62 | 66 | 68 | 70 |
| all | planned -> sent | 14 | 24 | 30 | 49 |
| all | **produced -> sent** | 75 | 83 | 90 | 99 |
| sms | **produced -> gateway received** | 75 | 82 | 87 | 98 |
