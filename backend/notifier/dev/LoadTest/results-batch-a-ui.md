# Notifier load test: batch-a-ui

- Run `20261121020333`, 2026-11-21 02:03 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 1000 critical `opened` changes, as fast as possible (produced in 0.07 s), 1 recipients x [ui] = 1000 notifications.
- Database/host clock offset 0.0 ms, corrected.

Rows: 1000 of 1000 (sent 1000).
Duplicates: MailHog received 0 emails for 0 sent email rows (0 extra). SMS duplicates are absorbed by the gateway's idempotency key.
Throughput: 1000 sent in 1.44 s = **696 notifications/s** (696 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| ui | produced -> planned | 442 | 909 | 912 | 914 |
| ui | planned -> sent | 293 | 452 | 475 | 482 |
| ui | **produced -> sent** | 768 | 1329 | 1377 | 1389 |
| all | produced -> planned | 442 | 909 | 912 | 914 |
| all | planned -> sent | 293 | 452 | 475 | 482 |
| all | **produced -> sent** | 768 | 1329 | 1377 | 1389 |
