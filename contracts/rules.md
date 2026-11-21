# Conditions, schema mappings, rules, preferences

One condition language is used in three places: a mapping's `match`, a rule's `when`,
and a runbook's `matches`. All of these objects are rows edited at runtime; none needs a restart.

## 1. Paths

A path is a dot path, e.g. `severity`, `raw.error.code`, `payload.notional`.
It is resolved against the envelope ([events.md](events.md)); if its first segment is not an
envelope field, it is resolved against `raw`. So `error.code` equals `raw.error.code`,
and `{{workflowId}}` works in a template. A missing path makes a condition false and renders as an empty string.

## 2. Condition language

A node is a group `{"all": [node, ...]}`, a group `{"any": [node, ...]}`, or a leaf
`{"field": path, "op": op, "value": value}`. Groups nest to any depth.

| `op` | True when |
|---|---|
| `eq`, `ne` | equal / not equal |
| `gt`, `gte`, `lt`, `lte` | numeric comparison |
| `in` | field equals one of the array `value` |
| `glob` | string match with `*` wildcards, e.g. `pricing-engine*` |
| `sev_gte` | field is at or above `value` on the severity scale |
| `exists` | the path resolves (no `value`) |

```json
{"all": [
  {"field": "type", "op": "glob", "value": "trade.rejected*"},
  {"any": [
    {"field": "payload.notional", "op": "gt", "value": 10000000},
    {"field": "payload.reason", "op": "in", "value": ["LIMIT_BREACH", "SANCTIONS"]}
  ]}
]}
```

## 3. Schema mapping (table `mappings`)

Turns a raw payload into the envelope. Mappings are tried by `priority`, highest first; the first
whose `match` is true wins. `match` sees only `source` and `raw.*`.

```json
{
  "id": "trade-rejected-v1",
  "status": "active",
  "origin": "llm",
  "priority": 100,
  "match": {"all": [{"field": "raw.meta.schema", "op": "glob", "value": "trade.rejected*"}]},
  "fields": {
    "type":     {"path": "meta.schema"},
    "service":  {"path": "meta.producer"},
    "severity": {"const": "error"},
    "env":      {"const": "prod"},
    "ts":       {"path": "meta.ts", "format": "epoch_ms"}
  },
  "severity_map": {"P1": "critical", "ERROR": "error", "WARN": "warning"},
  "dedup_fields": ["payload.desk", "payload.reason"]
}
```

- A field is `{"path": <path into the payload>}` or `{"const": <value>}`. A field left out, or a path
  that does not resolve, gets the unmapped default from [events.md](events.md).
- `ts.format`: `iso`, `epoch_ms` or `epoch_s`.
- The severity value is looked up in `severity_map` case-insensitively; values already on the scale pass through.
- `dedup_fields` become the envelope's `dedup_hints`.
- `status`: `active` or `disabled`. `origin`: `seed`, `human` or `llm`.
- Onboarding a new schema: the processor publishes one sample per unknown shape to Kafka `schema.samples`
  ([events.md](events.md) §8); the AI worker asks Claude for a mapping object and saves it through the API as `active` if every `path` resolves on
  the sample, the severity is on the scale and `ts` parses. Otherwise the sample is marked `failed` and
  a person writes the mapping in the UI. Only events that arrive after the mapping is active use it;
  earlier ones stay unmapped.

## 4. Rule (table `rules`)

```json
{
  "id": "settlements-workflow-failed",
  "team_id": "settlements",
  "name": "Order settlement failed in prod",
  "enabled": true,
  "when": {"all": [
    {"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
    {"field": "raw.workflowName", "op": "eq", "value": "order-settlement"},
    {"field": "env", "op": "eq", "value": "prod"}
  ]},
  "alert_key": ["raw.workflowName", "raw.error.code"],
  "window_s": 300,
  "route": {"recipients": [{"team": "settlements", "on_call": true}], "channels": ["call", "sms", "ui"]},
  "present": {
    "title": "{{workflowName}} failed at step {{step}}",
    "body": "{{error.message}}",
    "fields": ["step", "error.code", "error.message"],
    "color": "red"
  },
  "runbook_id": "settlement-timeout"
}
```

- `alert_key`: paths whose values, with the rule id, identify one alert. Dedup and grouping are the same
  mechanism: every matching event with the same key increments that alert's `count`. Default when omitted:
  `["type", "service"]` plus the envelope's `dedup_hints`.
- `window_s` (default 300): the alert stays open for events with its key until none has arrived for
  `window_s`; then the processor auto-resolves it. An event within another `window_s` after that reopens it.
- `route.recipients`: `{"team": id}` (all members), `{"team": id, "on_call": true}` or `{"user": id}`.
- `route.channels`: any of `ui`, `email`, `sms`, `call`. When omitted, each recipient's own preferences
  decide. Quiet hours and the per-hour limit from preferences always apply.
- `present.color`: `red`, `orange`, `yellow`, `blue` or `grey`. Templates use `{{path}}`.
- `present` is optional. A rule without `present.title` gets its `name` as the alert title, or
  `{{type}} · {{service}}` when it has no name, so no channel ever shows a blank title.
- A person matched by several rules for one incident gets one message.
- An invalid rule is rejected by the API with `422` and never reaches the processor (see [api.md](api.md)).
- The processor reloads rules and mappings within 5 s of a change (they arrive on Kafka `config.changes`,
  [events.md](events.md) §6).

The other three example rules from the brief, as `when` / `alert_key` / `route` / `present`:

```json
{"id": "pricing-engine-errors", "team_id": "pricing",
 "when": {"all": [{"field": "service", "op": "glob", "value": "pricing-engine*"},
                  {"field": "severity", "op": "sev_gte", "value": "error"}]},
 "alert_key": ["raw.instrument"], "window_s": 300,
 "route": {"recipients": [{"team": "pricing"}], "channels": ["ui"]},
 "present": {"title": "Pricing errors on {{instrument}}", "color": "orange"}}

{"id": "rates-large-rejections", "team_id": "rates",
 "when": {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"},
                  {"field": "payload.desk", "op": "eq", "value": "rates"},
                  {"field": "payload.notional", "op": "gt", "value": 10000000}]},
 "route": {"recipients": [{"team": "rates"}], "channels": ["email", "ui"]},
 "present": {"title": "Trade {{payload.tradeId}} rejected: {{payload.reason}} ({{payload.notional}} {{payload.currency}})"}}

{"id": "anna-payments-critical", "team_id": "payments",
 "when": {"all": [{"field": "service", "op": "eq", "value": "payments-api"},
                  {"field": "severity", "op": "eq", "value": "critical"}]},
 "route": {"recipients": [{"user": "anna"}]}}
```

## 5. Channel preferences (table `preferences`, one per user)

```json
{
  "user_id": "anna",
  "timezone": "Europe/Warsaw",
  "by_severity": {"critical": ["sms"], "error": ["email"], "warning": ["email"], "info": ["ui"]},
  "quiet_hours": {"start": "20:00", "end": "08:00", "downgrade": {"call": "sms", "sms": "email"}},
  "wake_me": false,
  "max_push_per_hour": 10
}
```

Inside quiet hours a channel is replaced by its `downgrade`, never dropped. `wake_me: true` lets
`critical` through unchanged. `max_push_per_hour` counts `sms` and `call`; the excess is folded into one
"and N more" message.
