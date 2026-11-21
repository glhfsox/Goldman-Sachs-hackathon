# Notifier load test: baseline-steady

- Run `000339`, 2026-11-21 00:04 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 300 critical `opened` changes, 10/s (produced in 31.23 s), 3 recipients x [ui, email, sms] = 2700 notifications.
- Database/host clock offset 0.1 ms, corrected.

Rows: 2700 of 2700 (sent 2700).
Throughput: 2700 sent in 31.73 s = **85 notifications/s** (9 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 7 | 9 | 9 | 764 |
| email | planned -> sent | 381 | 781 | 1791 | 2152 |
| email | **produced -> sent** | 388 | 795 | 1798 | 2159 |
| sms | produced -> planned | 7 | 9 | 9 | 764 |
| sms | planned -> sent | 387 | 795 | 1788 | 2147 |
| sms | **produced -> sent** | 394 | 804 | 1795 | 2154 |
| ui | produced -> planned | 7 | 9 | 9 | 764 |
| ui | planned -> sent | 382 | 796 | 1783 | 2108 |
| ui | **produced -> sent** | 390 | 807 | 1790 | 2115 |
| all | produced -> planned | 7 | 9 | 9 | 764 |
| all | planned -> sent | 383 | 791 | 1791 | 2152 |
| all | **produced -> sent** | 391 | 803 | 1798 | 2159 |
