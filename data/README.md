# data — simulated sources

This zone plays the outside world: systems that send us events over Kafka and HTTP. The interface,
including what each source sends, is in [contracts/data/sources.md](../contracts/data/sources.md).
The `kafka-sources` broker is in [kafka-sources/](kafka-sources/README.md), the simulator in [simulator/](simulator/README.md).

## The story behind the data

**Kestrel Markets** is a fictional mid-sized investment bank. The simulator replays one trading
morning there. Every event belongs to the story, so each requirement in the brief shows up in it.

**The world**

| Who / what | Sends | Through |
|---|---|---|
| Workflow engine: `order-settlement`, `trade-booking`, `payment-release`, `eod-reconciliation` | Workflow failures and recoveries | Kafka `source.workflows` |
| `trade-gateway`, `booking-service` | Booked, amended and rejected trades | Kafka `source.trades` |
| Monitoring (Alertmanager-like), watching `payments-api`, `counterparty-service` and others | Metric alerts: error rate, latency, crash loops | HTTP webhook |
| `pricing-engine-1` … `-4`, pricing `EURUSD`, `UST10Y`, `BUND10Y` and others | Log records | HTTP webhook |

The teams are the brief's own: **Settlements**, **Pricing**, **Rates** and **Payments**, including Anna.

**The morning, about 3 minutes in the demo**

1. **09:00, a quiet start.** Normal traffic: trades get booked, a few info and warning logs come in. Nobody is paged.
2. **10:14, the counterparty service slows down.** It's the root cause of everything that follows.
3. **The settlement storm.** `order-settlement` times out at step `settle` hundreds of times
   (`WORKFLOW_FAILED`, `TIMEOUT`, prod). Settlements' on-call person gets one phone call and one SMS,
   not hundreds. The UI shows a single red alert whose counter keeps going up, with the runbook's
   "retry workflow" step ready to run.
4. **Knock-on damage in payments.** Monitoring reports a 12% error rate on `payments-api` in `eu-west`.
   It's linked to the same incident, because payments depends on the counterparty service. Anna gets
   an SMS, because it's within her hours.
5. **Meanwhile in pricing.** `pricing-engine-3` keeps missing its cache, and EURUSD prices take over a
   second. The errors are grouped into one UI-only alert per instrument. The free-text runbook says:
   restart the instance and wait 2 minutes.
6. **On the rates desk.** Trade `T-88121`, 25M EUR, is rejected for `LIMIT_BREACH`. The Rates team gets
   an email whose title is filled in from the trade.
7. **The surprise.** A source starts sending a shape we have never seen. The event shows up at once as
   "unmapped", and the AI worker proposes a mapping in the background. This is the moment the judges'
   unseen schema plays.
8. **Recovery.** The counterparty service comes back. Workflows report `WORKFLOW_RECOVERED`, monitoring
   sends `resolved`, and the alerts close themselves.

**Pitch in one line:** one bad morning at a bank. A single slow dependency sets off hundreds of
failures across three systems, and the right people get exactly one clear, actionable alert each, on
the channel they prefer.

Behind the scenes: values come from Faker with a fixed seed, so every run of the demo is identical.
The `steady`, `burst` and `load` modes reuse the same world at higher volume.
