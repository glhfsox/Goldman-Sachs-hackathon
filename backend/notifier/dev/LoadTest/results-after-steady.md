# Notifier load test: after-steady

- Run `000940`, 2026-11-21 00:10 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 300 critical `opened` changes, 10/s (produced in 31.26 s), 3 recipients x [ui, email, sms] = 2700 notifications.
- Database/host clock offset 0.0 ms, corrected.

Rows: 2700 of 2700 (sent 2700).
Throughput: 2700 sent in 31.26 s = **86 notifications/s** (10 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 7 | 10 | 13 | 18 |
| email | planned -> sent | 10 | 14 | 17 | 604 |
| email | **produced -> sent** | 18 | 23 | 27 | 611 |
| sms | produced -> planned | 7 | 10 | 13 | 18 |
| sms | planned -> sent | 10 | 14 | 17 | 604 |
| sms | **produced -> sent** | 18 | 23 | 27 | 611 |
| ui | produced -> planned | 7 | 10 | 13 | 18 |
| ui | planned -> sent | 10 | 14 | 17 | 604 |
| ui | **produced -> sent** | 18 | 23 | 27 | 611 |
| all | produced -> planned | 7 | 10 | 13 | 18 |
| all | planned -> sent | 10 | 14 | 17 | 604 |
| all | **produced -> sent** | 18 | 23 | 27 | 611 |
