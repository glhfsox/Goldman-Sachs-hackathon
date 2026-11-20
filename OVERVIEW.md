# OVERVIEW — the hackathon task

> **This file only describes the problem. It is not a task, plan or architecture.**
> It is a summary of the organisers' brief ("Hackathon 2026 — Software Engineering"),
> so that any agent knows what the project is about and what it will be judged on.
> Do not build anything because of this file; act only on what your operator asks.
> Architecture and interfaces live in `contracts/` and `STATE.md`. Sample values were transcribed from photos.

## Product: Flexible Alerting & Notification Service

### The problem
Every system produces errors, warnings and business events: a failed workflow, a breached
threshold, a rejected message. Today these signals are spread across logs, dashboards and
mailboxes. The right people find out late, or they get so many alerts that they stop reading them.

We need a service that gets **the right alert to the right people, on the channel they prefer**,
and helps them **resolve it by following the team's runbooks**.

Cross-cutting qualities: **flexibility** (easy to adapt to any team or system), **scalability**
(high volume must not hurt performance), **UX** (usable by engineers, on-call support, non-engineers).

## Functional requirements

### 2.1 Ingest from many sources
- At least **two different source types**, e.g. a message broker (Kafka, RabbitMQ), an
  HTTP/webhook endpoint, a tailed log file, or a polled API.
- Messages arrive in **different schemas**. No single fixed format may be assumed.
- Adding a new source or schema should need as little code as possible.
- Judges bring a **schema we have never seen** to the demo (see Appendix A).

### 2.2 Teams define what they care about (rules)
- **Filters on any field**: source, workflow, schema, severity, environment, or a value inside
  the message body (`amount > 1,000,000`, `error code in [TIMEOUT, REJECTED]`).
- Rules can be combined with **AND/OR**.
- **Per-rule routing**: who gets notified and on which channel(s).
- **Per-rule presentation**: title/body templates, which fields to show, and severity/colour in the UI.
- Rules can be created and changed **at runtime** (API, UI, or config files reloaded live) without a restart.

### 2.3 Delivery on the preferred channel
- Sample channels: **Email, SMS, phone call, UI**.
- Each team or person sets their own channel preferences, e.g. critical → phone + SMS,
  warning → email, info → UI only. Working hours and on-call schedules are optional.
- External channels **may be mocked**: a fake SMTP server, or an SMS/phone gateway that logs
  deliveries. The judges check that adding a real provider would be a small, isolated change.
- **Delivery must survive failure**: retries, and no lost alerts when a channel is down.

### 2.4 Service UI
- A live feed of a team's alerts, updated in real time (push, not page refresh).
- Acknowledge or resolve an alert.
- Manage rules and channel preferences.
- Propose fixes or enhancements to alerts.
- How an alert looks is controlled by its rule (2.2).

### 2.5 Don't flood people
- **Deduplication**: the same error 500 times a minute is one alert with a counter, not 500 alerts.
- **Throttling or grouping** per rule or per recipient.

### 2.6 Help resolve alerts with runbooks
An alert should say what to do next, not only what went wrong.
- Teams attach runbooks to rules, schemas or alert types. Runbooks come in several formats:
  a Markdown page, a wiki/Confluence link, a checklist, or free text written by a person (Appendix B).
- When an alert fires, the matching runbook is shown, with values from the message filled in
  (workflow ID, service name), on every channel where it fits: full steps in the UI and email,
  a short link in an SMS.
- Steps can be **actions**, not only text: retry a workflow, restart a service, call a webhook,
  open a ticket, escalate to another team.
- **Risky actions need human approval** first (from the UI, or by replying to the alert).
- Record what was done and by whom. Close the alert when the problem goes away.
- Adding a new runbook or a new kind of action should be as easy as adding a rule.
- Optional: record the steps taken during incidents automatically, so the runbook or
  knowledge base updates itself.

## Example rules (from the brief, in plain words)
- **Team Settlements**: any `WORKFLOW_FAILED` where workflowName = order-settlement and
  environment = prod → phone the on-call person + SMS; show the step and error message in red in the UI.
- **Team Pricing**: log lines from `pricing-engine*` at error level or above → UI only; group them
  into one alert per instrument every 5 minutes.
- **Team Rates**: `trade.rejected*` where desk = rates and notional > 10,000,000 → email + UI,
  with a templated title built from payload fields.
- **Anna (a person)**: anything critical from payments-api → SMS, but only 08:00–20:00; otherwise email.

## Appendix A: sample events (three different envelopes)
Workflow failure (message broker):
```json
{"type": "WORKFLOW_FAILED", "workflowId": "WF-2026...", "workflowName": "order-settlement",
 "step": "...", "severity": "critical", "environment": "prod", "timestamp": "2026-...",
 "error": {"code": "TIMEOUT", "message": "Counterparty service did not respond in 5000 ms"}}
```
Metric threshold breach (HTTP webhook, Alertmanager-like):
```json
{"alertname": "HighErrorRate",
 "labels": {"service": "payments-api", "region": "eu-west", "severity": "critical"},
 "annotations": {"summary": "Error rate 12% over the last 5 minutes"},
 "startsAt": "2026-...", "status": "firing"}
```
Business event (message broker, different envelope):
```json
{"meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1782...},
 "payload": {"tradeId": "T-...", "desk": "rates", "notional": 25000000,
             "currency": "EUR", "reason": "LIMIT_BREACH"}}
```

## Appendix B: sample runbooks (three shapes; judges bring a fourth)
Structured steps (YAML, Team Settlements):
```yaml
runbook: settlement-timeout
matches: {type: WORKFLOW_FAILED, "error.code": TIMEOUT}
steps:
  - check: "Is counterparty-service healthy?"
    action: http_get
    url: "http://counterparty-service/health"
  - action: retry_workflow
    workflowId: "{{workflowId}}"
    approval: none
  - if: "retry fails twice"
    action: escalate
    team: counterparty-support
    approval: required
```
Markdown page (payments rule):
```
# HighErrorRate on payments-api
1. Open the payments dashboard and check if errors are in one region only.
2. If one region: fail traffic over to the other region (needs approval from the team lead).
3. If all regions: check the last deployment and roll it back.
4. Post an update in the #payments-incidents channel every 15 minutes.
```
Free text (from a wiki or chat):
> When pricing latency goes over a second, it's usually the cache. Restart the pricing-engine
> instance named in the alert, wait 2 minutes, and if latency is still high, call the pricing on-call person.

## How it is judged
- **Flexibility**: how easy is it to add a new source, schema, channel or rule type? Is it config
  or code? How expressive are the rules?
- **Scalability**: can it scale horizontally? How is state handled (rules, dedup windows,
  delivery status)? Is there an obvious bottleneck or single point of failure?
- **UX**: can someone who has never seen the UI use it? Is the rule editor understandable?
  Are alerts clear and actionable? How many commands and how long from a fresh clone to a
  working demo? Is the README clear? How long until a new team starts getting alerts?
- **Throughput/latency**: messages per second ingested and processed, and end-to-end latency
  from ingest to delivery (p50/p99). **Measured, not estimated.** The load-test script and its
  results go into the presentation.
- **Level of implementation**: a repo link with a clear README that says what is mocked.
- **Innovation**, and the **presentation**.

## Submission
- A **video of up to 5 minutes** that explains and demonstrates the solution. It is the
  **primary basis for judging** in every category.
- If AI tools were used (Copilot, Claude), say how, and how security and privacy concerns were handled.
- Upload to Lockbox in a directory named after the team. Recommended: also a text file with
  links to the Git repo and/or a demo app.
- After the first round, the top five teams present live in the final, followed by Q&A.
