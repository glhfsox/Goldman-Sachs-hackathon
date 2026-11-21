# Notifier load test: after-burst-3-replicas

- Run `001100`, 2026-11-21 00:11 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 1000 critical `opened` changes, as fast as possible (produced in 0.05 s), 3 recipients x [ui, email, sms] = 9000 notifications.
- Database/host clock offset 0.1 ms, corrected.

Rows: 9000 of 9000 (sent 9000).
Throughput: 9000 sent in 14.12 s = **637 notifications/s** (71 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 6810 | 13351 | 13880 | 14074 |
| email | planned -> sent | 60 | 144 | 230 | 1155 |
| email | **produced -> sent** | 6887 | 13401 | 13947 | 14102 |
| sms | produced -> planned | 6810 | 13351 | 13880 | 14074 |
| sms | planned -> sent | 60 | 143 | 222 | 1157 |
| sms | **produced -> sent** | 6887 | 13402 | 13946 | 14102 |
| ui | produced -> planned | 6810 | 13351 | 13880 | 14074 |
| ui | planned -> sent | 61 | 144 | 213 | 1166 |
| ui | **produced -> sent** | 6886 | 13401 | 13945 | 14102 |
| all | produced -> planned | 6810 | 13351 | 13880 | 14074 |
| all | planned -> sent | 60 | 143 | 221 | 1166 |
| all | **produced -> sent** | 6887 | 13402 | 13947 | 14102 |
