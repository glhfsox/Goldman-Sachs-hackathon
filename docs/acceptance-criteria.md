# Acceptance criteria — Flexible Alerting & Notification Service

Derived from [OVERVIEW.md](../OVERVIEW.md). Use it to check the design and close blind spots.
Thresholds (5 min, N per hour, 1000 msg/s, …) are our proposals, not from the brief.
Status markers: **TBD** = decide later; **Deferred** = see the last section.

## Pipeline

```
Sources → Ingest → Agent Normalization → Rule Engine → Alert Manager (dedup + Event Aggregator) → Notification Policy → Channels
                                                            ↓                                          ↑
                                                       Alert Store ← UI (ack / resolve / approve) → Runbooks / Actions
```

Key decision: **events, alerts and notifications are three different entities.** 500 events collapse into 1 alert;
an alert produces 0..N notifications, and Notification Policy decides how many, not every incoming event.

---

## 1. Ingest (source adapters)
HTTP/webhook + a message broker (Kafka or RabbitMQ). An adapter only receives, enqueues and acks; no logic.
- **AC-1.1** All 3 Appendix A events are accepted: 2 via the broker, 1 via HTTP.
- **AC-1.2** While processing is down, ingest keeps accepting. After processing recovers, every event is processed, 0 lost.
- **AC-1.3** A new source type = one adapter file + config. The core does not change.

## 2. Agent Normalization (schemas)
Maps raw JSON to a common envelope `{source, type, severity, env, service, ts, dedup_hints, raw}`.
`raw` is kept whole so rules can reach any field by path.
- **AC-2.1** The judges' unseen schema is onboarded without code changes or a restart, in ≤5 min.
  An event with no mapping still shows up in the UI as "unmapped" and is never dropped.
- **AC-2.2** Severity is normalized to one scale (`P1` / `ERROR` / `critical` → `critical`); timestamps (epoch ms, ISO) to one
  format; missing fields get defaults.

## 3. Rule Engine
Conditions on any field (`=`, `!=`, `>`, `in`, wildcard, `>= severity`), nested AND/OR. A rule defines routing (who),
presentation (template, fields, colour) and anti-flood settings (dedup key, groupBy, window).
- **AC-3.1** All 4 example rules from the brief are expressed as config, no code.
- **AC-3.2** A rule created or changed via UI/API applies to the next event within ≤5 s on all replicas.
  An invalid rule is rejected with a clear error.

## 4. Alert Manager (dedup, Event Aggregator, lifecycle)
Turns the event stream into a small number of alerts.
- **AC-4.1 Dedup.** 500 identical events in a minute → 1 alert with `count=500`, `first_seen`, `last_seen`.
- **AC-4.2 Event Aggregator.** Related events are merged into one incident, including events linked in a causal chain.
  Example: the DB goes down, then the server fails and clients get 500s → one incident whose root cause is the DB
  (the earliest upstream event), with the server failure and 500s as consequences. Simple grouping is covered too:
  `groupBy: instrument, window: 5m` (Team Pricing) → exactly one alert per instrument.
- **AC-4.3 Lifecycle.** `open → acked → resolved`. Auto-resolve on `status: resolved` or no recurrence for X min.
  A recurrence within Y min after resolve reopens the alert instead of creating a new one (flapping protection).
- **AC-4.4** Dedup and aggregation state is shared across replicas: with 2 replicas AC-4.1 still yields 1 alert.

## 5. Notification Policy — non-intrusive delivery
A separate layer between an alert and the channels: whether to disturb anyone at all, whom, when, on which channel,
and in one message or several.

Principles:
1. **Notify on state changes, not on every event:** new alert, severity up, counter threshold crossed (×10, ×100), resolved.
   Further events only bump the counter in the UI.
2. **Escalation ladder, not a broadcast:** UI immediately → email/SMS after 5 min without ack → call the on-call after 10 more.
3. **Ack cancels everything still pending** (queued SMS and calls) for all recipients.
4. **One message per person, not per rule:** a person matched by 3 rules for one incident gets one notification.
5. **Digest for non-critical:** `info` / `warning` are batched every 15–60 min ("12 new alerts, top 3 …"). Only `critical` goes immediately.
6. **Per-recipient rate limit:** ≤N push notifications (SMS, call) per hour; the rest folds into "and 37 more → link".
7. **Time and preferences:** working hours and the recipient's timezone (Anna's case). Outside the window the message
   is downgraded one step (SMS → email), never dropped. `critical` marked "wake me" breaks through quiet hours.

- **AC-5.1** **TBD.** Storm test: 1000 events in 2 min across 3 rules for one team → the on-call gets ≤2 push notifications
  (new incident + severity up) and 1 digest; the rest is UI only.
- **AC-5.2** **TBD.** Ack within 5 min: no SMS or call is sent, and the log shows "suppressed by ack".
- **AC-5.3** A person in two rules that fire on one incident gets exactly 1 message.
- **AC-5.4** 20 `warning` alerts in an hour → 1 email digest, not 20 emails.
- **AC-5.5** Anna, 22:00, `critical` from payments-api → email, no SMS. At 09:00 the same event → SMS.
- **AC-5.6** Every notification in the UI explains "why you got this" (rule, ladder step) and "why not" (suppressed by dedup / ack / limit).

## 6. Channels (delivery)
One interface `send(recipient, rendered_message) → status`. Mocks: SMTP (MailHog), SMS and call write to a log.
A queue with retries, backoff and a DLQ.
- **AC-6.1** The message is adapted per channel: UI and email get the full runbook; SMS ≤160 chars (title + short link);
  a call is mocked as TTS text in the log.
- **AC-6.2** Mock SMTP down for 60 s → after recovery 100% of emails are delivered, each delivery status is visible on the alert.
  Killing the delivery process causes no duplicate SMS (idempotency key per notification).
- **AC-6.3** If a channel is unavailable for longer than X, the fallback channel is used (SMS → email).
- **AC-6.4** A real provider = one file + config. Show that diff in the video.

## 7. Runbooks and Actions
Runbooks attach to a rule, schema or alert type. Formats: YAML, Markdown, free text, link.
Actions are plugins (`http_get`, `retry_workflow`, `escalate`, `webhook`, `ticket`) with an `approval` flag.
- **AC-7.1** Runbook placeholders (`{{workflowId}}`, service name, …) are filled with values from the event.
- **AC-7.2** **TBD — decide after we present our design.** `retry_workflow` runs in one click; `escalate` waits for approval
  from the UI or a reply to the message (a button link in email/SMS).
- **AC-7.3** Audit: what was done, by whom, when, with what result, visible in the alert timeline.
- **AC-7.4** A new action type = one plugin + registration in config.

## 8. UI
- **AC-8.1** Live feed: a new alert appears in ≤2 s without a page reload; ack/resolve sync across tabs; filter by team.
- **AC-8.2** Alert card: template from the rule, counter and aggregated events, runbook with action buttons,
  timeline "events → notifications → actions".
- **AC-8.3** Form-based rule editor (not only JSON). A per-person channel preferences screen.
- **AC-8.4** "Propose an improvement" from an alert (threshold, template, runbook); the rule owner accepts or rejects it.

## 9. Platform: state, scale, load
- **AC-9.1** Services are stateless. Rules, dedup windows and delivery statuses live in external storage (Redis, Postgres).
  Processing runs in ≥2 replicas; killing one under load loses and duplicates nothing.
- **AC-9.2** The load-test script is in the repo. The report gives msg/s and p50/p99 ingest → delivery latency (to a mock channel),
  on 1 and 2 replicas. Proposed target: ≥1000 msg/s, p99 < 1 s.
- **AC-9.3** Fresh clone → working demo in ≤3 commands and ≤5 min. README lists what is mocked and has a section on AI use and
  security/privacy. A new team starts receiving alerts via the UI in ≤10 min.

---

## Deferred (do last, if time allows)
- **AC-3.3 "Test rule".** Shows which of the last N events would match a rule and how many notifications that would produce.
- **AI runbook assist** (idea by glhfsox): turn free-text runbooks into structured steps; propose runbook updates from the steps
  actually taken during an incident.

## Likely blind spots
Notification Policy as a separate layer (AC-5.x), the causal-chain Event Aggregator (AC-4.2), shared dedup state across replicas
(AC-4.4), and "propose an improvement" (AC-8.4).
