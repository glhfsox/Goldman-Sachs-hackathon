# Frontend

The web UI: React + TypeScript + Vite. The design is the approved v3 concept (Slack layout, Revolut look).
It talks only to the API service ([contracts/api.md](../contracts/api.md)): REST under `/api` and live updates on `/ws`.

## Run

The whole system, UI included, from the repo root: `docker compose up -d --build`, then http://localhost:3000.

For working on the UI (hot reload):

```bash
cd backend/api && docker compose up -d --build     # the API on :8000 (with its database and Kafka)
cd frontend && npm install && npm run dev           # the UI on http://localhost:3000
```

The dev server forwards `/api` and `/ws` to `VITE_API_URL` (default `http://localhost:8000`), so the browser never
makes a cross-origin call and the API needs no CORS. In a container, nginx does the same (`Dockerfile`, `nginx.conf`,
`docker-compose.yml`, upstream `API_UPSTREAM`, default `api:8000`); alone against an API on this machine:
`API_UPSTREAM=host.docker.internal:8000 docker compose up -d --build` here.

## Who you are

No login in the PoC (agreed in OVERVIEW): the sign-in screen lists the seeded people from `GET /api/users`; the one
you pick is kept in the browser and sent as `X-User` on every request.

## What is real and what is not yet

| Screen | Uses |
|---|---|
| Alerts | `GET /alerts`, `GET /alerts/{id}`, ack, resolve, live `/ws`, proposals, runs (preview, start, cancel, step done) |
| Proposals | `GET /proposals`, accept, reject |
| Rules, rule editor | `GET/POST/PUT /rules`, runbooks for the runbook picker |
| Runbooks | `GET/POST /runbooks`, `PUT` (edit steps), accept, `GET /actions` (risk badges) |
| Sources | `GET/POST/PUT /sources`, `GET /schema-samples`, `GET/PUT /mappings` |
| My notifications | `GET/PUT /users/{id}/preferences`, users, teams |

- **Runbook runs are real** (issue #82): preview, start (the person's start is the approval), progress, cancel and
  ticking off text-only steps. Automatic start (step 4) is not served yet, so every run is started by a person.
- Not shown because the API has no data for it yet: the repeats chart on an alert, per-source statistics,
  discarding a runbook, editing a runbook's source, and "always wait for a person" on a rule.

## Demo data

```bash
python3 frontend/dev/seed.py      # rules, runbooks, alerts, new event shapes and proposals in the local API
```

Then sign in as Boris Nowak (Settlements, on call).

## Check

```bash
bash check.sh      # type check + unit tests
```
