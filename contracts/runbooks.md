# Runbooks, actions and extension points

## 1. Canonical runbook

Every input format (YAML, Markdown, free text, link) is stored as written in `runbooks.source` and
converted once to this form in `runbooks.canonical`. Only `active` runbooks are shown on alerts.

```json
{
  "id": "settlement-timeout",
  "title": "Settlement timeout",
  "matches": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                      {"field": "error.code", "op": "eq", "value": "TIMEOUT"}]},
  "steps": [
    {"text": "Is counterparty-service healthy?", "action": "http_get",
     "params": {"url": "http://counterparty-service/health"}, "approval": "none"},
    {"text": "Retry the workflow", "action": "retry_workflow",
     "params": {"workflowId": "{{workflowId}}"}, "approval": "none"},
    {"text": "Escalate to counterparty-support", "when": "retry fails twice", "action": "escalate",
     "params": {"team": "counterparty-support"}, "approval": "required"},
    {"text": "Post an update in #payments-incidents every 15 minutes"}
  ]
}
```

- `text` is required and is what every channel shows. `action`, `params`, `approval`, `when`, `link` are optional,
  and so are the graph fields `id`, `needs`, `if`, `retries`, `timeout_s`. Any other key is `422 invalid_runbook`.
- `action` must be a registered action name (`GET /api/actions`: `http_get`, `retry_workflow`, `reload_db`,
  `restart_service`, `webhook`, `ticket`, `escalate`), otherwise the api answers `422`; a step without `action` is text
  only. `params` is an object; without an `action` it is ignored.
- Where an action goes is a registered **action target** (§3), never a host the runbook makes up. A step's `params`
  either name one, `{"target": "orders-db", "operation": "reload_db", "instance": "{{instance}}"}` (`operation`
  defaults to the action name), or leave it out and get the action's default target (`retry_workflow` →
  `workflow-engine`, `restart_service` → `services`, `reload_db` → `orders-db`, `ticket` → `ticketing`, `escalate` →
  `escalation`), or give a `url` whose origin a target lists: `{"url": "http://counterparty-service/health"}`. Each
  `{name}` in the operation's path needs a `params` value (a `{{placeholder}}` is fine); other params go in the JSON
  body of a non-GET call. A step that does not resolve to a target is `422` at its `params` path.
- `approval`: `none` or `required`. The api accepts it and ignores it: the action's risk decides how a run starts (#82).
- `when` is shown to the person as a condition; it is not evaluated.
- `id`: `[a-z0-9_-]{1,40}`, unique in the runbook; a step without one is `step-<index>` (from 0).
  `needs`: ids of the steps it waits for; without `needs` a step waits for the step before it, so a list of
  steps runs in order. The needs must not form a cycle.
- `if: {step, status}`: run only when that step (one of `needs`) ended `succeeded` or `failed`.
- A step without `action` is a text step: a person ticks it, and it never starts by itself. A step that waits for it only
  by default (no `needs` written) waits for what the text step waited for, so a note in the middle of a list does not stall
  the run; a `needs` that names a text step is kept and waits for the tick.
- A `{{path}}` in a step's `params` that the alert has no value for makes that step `blocked`: it never runs with an
  empty value. Values are filled once and literally (a value containing `{{...}}` is not expanded again).
- Risk of a run = the highest risk of any step with an action, including steps an `if` may skip.
- `retries`: a whole number 0 to 3; `timeout_s`: 1 to 300.
- `matches` uses the condition language and paths of [rules.md](rules.md). Placeholders are `{{path}}`.
  `matches` may be left out when the source does not say what the runbook applies to; such a runbook
  is shown only on alerts of a rule that names it in `runbook_id`.
- A `link` runbook is stored as one step `{"text": "Open the runbook", "link": "<url>"}`. The URL is never fetched.

Conversion: the canonical form is internal. A person writes and keeps seeing the runbook in their own
format (`source`); the AI worker converts every format except `link` (YAML, Markdown, free text, anything
else) into this JSON through the Claude API, and code checks the result before it is saved: actions are
limited to the registered names, `matches` must be a valid condition, and a step whose action is not
`read_only` gets `approval: required` unless the source says in so many words that it needs none. A
runbook that passes is saved as `active` at once (`PUT /runbooks/{id}`, [api.md](api.md)); one that does
not stays `draft`. A step that failed is never retried or replaced by an LLM.

Learning from incidents: when an alert is resolved after people ran actions on it, the AI worker reads
that alert's timeline and files a proposal of kind `runbook` ([api.md](api.md)) whose `matches` is the
alert's `type` and `env` and whose steps are the actions that were run. The team accepts or rejects it.

Which runbook an alert gets: the rule's `runbook_id`; otherwise the first active runbook of the team
whose `matches` is true for the alert's `sample_event`; otherwise none. An incident shows the runbooks of
all its alerts, root cause first.

Per channel: UI and email show all steps; SMS is the title plus the short link, 160 characters at most;
a call is the title and first step as text-to-speech.

## 2. Running an action

1. The UI posts `POST /alerts/{id}/actions` ([api.md](api.md)).
2. `approval: required` → the run waits in `waiting_approval` until someone approves or rejects it.
   Being replaced (#82): the api ignores a step's `approval`; the action's risk decides, and runs replace this section.
3. The API runs the plugin, stores the result in `action_runs`, appends to `alert_events`.
4. On failure the run is `failed` and the alert stays open for a person. Nothing runs without a click.

## 3. Extension points: one file plus one config entry each

Signatures are language-neutral; arguments and results are JSON-shaped.

| Kind | Lives in | Interface | Registered by |
|---|---|---|---|
| Source adapter | `backend/ingest/adapters/` | `start(config, emit)`; calls `emit(payload_bytes)` per message | a `sources` row with that `kind` |
| Channel adapter | `backend/notifier/channels/` | `send(recipient, message) → {status, provider_id?, error?}` | notifier channel config |
| Action plugin | `backend/api/src/AlertApi.Infrastructure/Actions/` | `run(request, context) → {ok, output?, error?}` plus `{name, risk, default_target, url_method, timeout}` | one DI line in the api's `Program.cs` |
| Action target | `action_targets` table of the api, `PUT /api/action-targets/{id}` | `{base_url, origins, operations: {name: {method, path, risk?}}}` | a row; no code, no restart |

Channel adapter arguments:

```json
{"recipient": {"user_id": "marek", "name": "Marek", "email": "marek@example.com", "phone": "+48000000000"},
 "message": {"notification_id": "9f2c...", "channel": "sms", "title": "order-settlement failed at step settle",
             "body": "Counterparty service did not respond in 5000 ms", "link": "http://localhost:3000/a/01JD3K8QK2"}}
```

`status` is `sent`, `retry` (temporary failure, the notifier backs off and retries) or `failed`.
`send` must be idempotent on `notification_id`. Initial channels: `ui`, `email` (MailHog), `sms` and
`call` (mock gateway).

Action plugin: `request` is `{method, url, body}`, built from the step and its target when a run starts (the person
approves exactly these requests; a target edited later changes only later runs). `context` is
`{alert_id, run_id, node_id, idempotency_key, actor, timeout}`; the plugin sends `Idempotency-Key` with every call and
never retries (retries are steps of the run, so every attempt is on the timeline). Output is cut to 4 KB.

Risk: `read_only` (reads only), `auto` (changes something, safe to repeat), `approval` (anything else). Initial actions:
`http_get` (`read_only`), `retry_workflow` and `reload_db` (`auto`), `restart_service`, `webhook`, `ticket`,
`escalate` (`approval`). An operation's `risk` can raise the action's risk for that target, never lower it.

Action target example (seeded by migration `AddActionTargets`, all pointing at `mock-target` in the demo):

```json
{"id": "orders-db", "base_url": "http://mock-target:8003/orders-db", "origins": [],
 "operations": {"health": {"method": "GET", "path": "/instances/{instance}/health", "risk": "read_only"},
                "reload_db": {"method": "POST", "path": "/instances/{instance}/reload"},
                "restart_service": {"method": "POST", "path": "/instances/{instance}/restart"}},
 "secret_ref": null, "version": 1}
```

The host of a call always comes from `base_url`: an alert's values only fill path segments (escaped) and the body,
and a `url` is accepted only when its origin is listed in a target's `origins` (`http://counterparty-service` →
`http://mock-target:8003/counterparty-service`). Seeded targets: `workflow-engine`, `services`, `orders-db`,
`webhooks`, `ticketing`, `escalation`, `counterparty-service`, `payments-api`, `pricing-engine`.
