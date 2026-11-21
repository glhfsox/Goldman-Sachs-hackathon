# Notifier load test: after-burst

- Run `000925`, 2026-11-21 00:09 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 1000 critical `opened` changes, as fast as possible (produced in 0.05 s), 3 recipients x [ui, email, sms] = 9000 notifications.
- Database/host clock offset 0.0 ms, corrected.

Rows: 9000 of 9000 (sent 9000).
Throughput: 9000 sent in 14.47 s = **622 notifications/s** (69 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 7722 | 13857 | 14317 | 14428 |
| email | planned -> sent | 26 | 46 | 65 | 129 |
| email | **produced -> sent** | 7744 | 13888 | 14342 | 14445 |
| sms | produced -> planned | 7722 | 13857 | 14317 | 14428 |
| sms | planned -> sent | 26 | 46 | 65 | 127 |
| sms | **produced -> sent** | 7745 | 13888 | 14342 | 14451 |
| ui | produced -> planned | 7722 | 13857 | 14317 | 14428 |
| ui | planned -> sent | 26 | 46 | 65 | 128 |
| ui | **produced -> sent** | 7744 | 13888 | 14342 | 14450 |
| all | produced -> planned | 7722 | 13857 | 14317 | 14428 |
| all | planned -> sent | 26 | 46 | 65 | 129 |
| all | **produced -> sent** | 7745 | 13888 | 14342 | 14451 |
