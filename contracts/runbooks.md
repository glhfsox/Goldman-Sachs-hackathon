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
- `action` must be a registered action name (today `http_get`, `retry_workflow`, `restart_service`, `webhook`,
  `ticket`, `escalate`), otherwise the api answers `422`; a step without `action` is text only. `params` is an object; without an `action` it is ignored.
- `approval`: `none` or `required`. The api accepts it and ignores it: the action's risk decides how a run starts (#82).
- `when` is shown to the person as a condition; it is not evaluated.
- `id`: `[a-z0-9_-]{1,40}`, unique in the runbook; a step without one is `step-<index>` (from 0).
  `needs`: ids of the steps it waits for; without `needs` a step waits for the step before it, so a list of
  steps runs in order. The needs must not form a cycle.
- `if: {step, status}`: run only when that step (one of `needs`) ended `succeeded` or `failed`.
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
| Action plugin | `backend/api/actions/` | `run(params, context) → {ok, output?, error?}` plus `{name, read_only}` | api action registry |

Channel adapter arguments:

```json
{"recipient": {"user_id": "marek", "name": "Marek", "email": "marek@example.com", "phone": "+48000000000"},
 "message": {"notification_id": "9f2c...", "channel": "sms", "title": "order-settlement failed at step settle",
             "body": "Counterparty service did not respond in 5000 ms", "link": "http://localhost:3000/a/01JD3K8QK2"}}
```

`status` is `sent`, `retry` (temporary failure, the notifier backs off and retries) or `failed`.
`send` must be idempotent on `notification_id`. Initial channels: `ui`, `email` (MailHog), `sms` and
`call` (mock gateway).

Action plugin `context`: `{alert, actor, run_id}`. Initial actions: `http_get` (`read_only`),
`retry_workflow`, `restart_service`, `webhook`, `ticket`, `escalate`; they call `mock-target`.
