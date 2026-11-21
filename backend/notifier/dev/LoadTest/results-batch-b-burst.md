# Notifier load test: batch-b-burst

- Run `20261121020348`, 2026-11-21 02:04 UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.
- Load: 1000 critical `opened` changes, as fast as possible (produced in 0.07 s), 3 recipients x [ui, email, sms] = 9000 notifications.
- Database/host clock offset 0.1 ms, corrected.

Rows: 9000 of 9000 (sent 9000).
Duplicates: MailHog received 3000 emails for 3000 sent email rows (0 extra). SMS duplicates are absorbed by the gateway's idempotency key.
Throughput: 9000 sent in 15.24 s = **590 notifications/s** (66 alerts/s fully delivered).

| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| email | produced -> planned | 2240 | 4097 | 4100 | 4102 |
| email | planned -> sent | 7006 | 10563 | 10988 | 11099 |
| email | **produced -> sent** | 9245 | 14655 | 15085 | 15195 |
| sms | produced -> planned | 2240 | 4097 | 4100 | 4102 |
| sms | planned -> sent | 7044 | 10588 | 11016 | 11098 |
| sms | **produced -> sent** | 9282 | 14687 | 15114 | 15195 |
| ui | produced -> planned | 2240 | 4097 | 4100 | 4102 |
| ui | planned -> sent | 7078 | 10555 | 10979 | 11096 |
| ui | **produced -> sent** | 9316 | 14654 | 15077 | 15195 |
| all | produced -> planned | 2240 | 4097 | 4100 | 4102 |
| all | planned -> sent | 7045 | 10565 | 10995 | 11099 |
| all | **produced -> sent** | 9282 | 14662 | 15091 | 15195 |
| sms | **produced -> gateway received** | 9281 | 14687 | 15113 | 15195 |
