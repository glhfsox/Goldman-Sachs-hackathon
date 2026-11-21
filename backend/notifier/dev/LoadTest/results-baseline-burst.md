# Notifier load test: baseline-burst

- Run `000251`, 2026-11-21 00:03 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 1000 critical `opened` changes, as fast as possible (produced in 0.07 s), 3 recipients x [ui, email, sms] = 9000 notifications.
- Database/host clock offset 0.0 ms, corrected.

Rows: 9000 of 9000 (sent 9000).
Throughput: 9000 sent in 47.33 s = **190 notifications/s** (21 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 11372 | 19654 | 20307 | 20490 |
| email | planned -> sent | 12583 | 25786 | 26610 | 26829 |
| email | **produced -> sent** | 23614 | 45462 | 46941 | 47284 |
| sms | produced -> planned | 11372 | 19654 | 20307 | 20490 |
| sms | planned -> sent | 12620 | 25800 | 26644 | 26838 |
| sms | **produced -> sent** | 23632 | 45438 | 46946 | 47290 |
| ui | produced -> planned | 11372 | 19654 | 20307 | 20490 |
| ui | planned -> sent | 12622 | 25818 | 26626 | 26878 |
| ui | **produced -> sent** | 23628 | 45469 | 46939 | 47281 |
| all | produced -> planned | 11372 | 19654 | 20307 | 20490 |
| all | planned -> sent | 12614 | 25806 | 26629 | 26878 |
| all | **produced -> sent** | 23632 | 45469 | 46946 | 47290 |
