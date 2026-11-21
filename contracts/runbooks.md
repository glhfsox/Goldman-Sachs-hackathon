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

Which runbook an alert gets: the rule's `runbook_id`; otherwise the first active runbook of the team (ordered by id)
whose `matches` is true for the alert's `sample_event`; otherwise none. An incident shows the runbooks of
all its alerts, root cause first.

Per channel: UI and email show all steps; SMS is the title plus the short link, 160 characters at most;
a call is the title and first step as text-to-speech.

## 2. Running a runbook

1. A person previews (`GET /alerts/{id}/runs/preview`) and starts a run (`POST /alerts/{id}/runs`, [api.md](api.md)). Only an
   `active` version a person accepted runs, on an open alert of the runbook's team, one live run per alert.
2. Every step's `{{placeholders}}` are filled once from the alert and its request is frozen (§3, action targets). A step the
   alert has no value for is `blocked`. The run's risk is the highest risk of any step; a person starting it by hand is the approval.
3. The planner moves the steps: a step is `ready` when everything it needs has ended, `skipped` when its `if` is false or
   a step it needs was skipped, `blocked` when a step it needs failed. A text-only step waits for a person's tick.
4. The executor, inside every api replica, claims a `ready` step of a `running` run (one claim per step across replicas,
   `FOR UPDATE SKIP LOCKED`), counts the attempt, calls the action plugin with the frozen request and the step's
   `Idempotency-Key`, and records the result `{ok, output?, error?}`. In the same transaction the planner moves the steps behind
   it (a failed step with attempts left becomes `ready` again after a backoff of 2 s times the attempts made) and the run
   ends `done` or `failed`. Every attempt is one `action_finished` row on the timeline. A claim lasts `RUNBOOK_LEASE_S`; a
   step whose worker died is taken over after that and recorded as `failed`, so it follows the same retry rules. A plugin that
   throws is a failed step. A failed run leaves the alert open for a person; nothing in the loop waits for a click. A run can be
   cancelled at any time: a step already running finishes and keeps its result, nothing moves on.

### Automatic start and cancel

When an alert is `opened` or `reopened` (a change not older than 5 minutes), the api looks at the runbook the alert names in `runbook_id`
(it must be `active` and belong to the alert's team) or, when the rule names none, at the team's first `active` runbook, ordered by id, whose `matches` is true for the alert's `sample_event` (a runbook without `matches` is never picked; the first match is taken and, if it cannot start, no other is tried; a rule's `runbook_id` that cannot run is not replaced by a match), drafts the run as for a person and decides by the
run's risk:

| Risk | What happens |
|---|---|
| `read_only` | The run starts at once: `trigger: auto`, status `running`, no `approved_by`. |
| `auto` | Only when a person accepted this version and `runbook_starts` has `auto` for the rule and runbook: the run is `scheduled` with `start_at` = now + `RUNBOOK_CANCEL_WINDOW_S` (30 s). The executor loops start it when `start_at` passes. |
| `approval` | Nothing starts; a person starts it. |

With `RUNBOOK_RISK_CHECK=true` (default) an `auto`-risk run starts at `start_at` only when the ai-worker's risk check cleared it; a `hold` or no verdict at `start_at` makes it `waiting_approval` until a person approves ([api.md](api.md) "Runbook runs"). With `false` it starts at `start_at` unchecked. A broken or unknown runbook is logged and skipped,
never an error for the Kafka record. One run per `opened` record (`trigger_change_id` is unique) and one live run per alert. A `resolved`
alert, by a person (`POST /alerts/{id}/resolve`) or by the processor, cancels the alert's live run.

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
