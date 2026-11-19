# AGENTS.md

Shared rules for every agent (and human) in this repo. Hackathon: the goal is a working PoC/MVP as fast as possible. Speed beats polish, but `dev` always runs, and `main` is the released version.

## Session start
1. Find your operator: `git config team.member` (GitHub login). If it's empty, stop and ask the human to run `git config team.member <login>`. Never set it yourself.
2. Read your `HANDOFF.md` (if present) and `STATE.md`, at least the sections of the zones your task touches.

## Language
Everything in the repo is in English: code, comments, docs, `STATE.md` and `HANDOFF.md` entries, commit messages, PR descriptions. Chat with your operator in whatever language they use.

## Zones and owners
| Folder | Owner | Contents |
|---|---|---|
| `ai/` | glhfsox | models, prompts, inference, eval |
| `data/` | glhfsox, spdsslg | collection, cleaning, datasets, features |
| `backend/` | SviatoslavShpylovyi | API, DB, integrations |
| `frontend/` | oshuptar | UI |
| `presentation/` | oshuptar | slides, demo materials |
| `contracts/ai/`, `contracts/data/`, `contracts/backend/` | owner of that zone | interfaces the zone provides; files directly in `contracts/` are shared |
| root, `.github/`, `.claude/`, `scripts/` | glhfsox | repo infrastructure |

The exact path → owner mapping lives in `.github/CODEOWNERS`. Both GitHub and the hooks read it.

- Write only in your operator's zone. Other zones are read-only. Exception: your operator explicitly asked you to change a specific place in another zone; the Claude hook then asks the human to confirm.
- Need a change in another zone? Don't make it yourself. Tell your operator; on their explicit request, open a PR against the provider's contract (see Contracts), or draft a message for them to send to the owner.
- From another zone, use only what `contracts/` describes, not its internals.
- Each zone has its own dependencies (`pyproject.toml` / `package.json` inside the folder). No shared lock files at the root.
- Zone-specific rules (stack, commands, own agents) go in `<zone>/AGENTS.md`. For Claude Code, add `<zone>/CLAUDE.md` containing one line: `@AGENTS.md`.

## Contracts (`contracts/`)
Everything where zones touch: API endpoints, dataset formats, model input/output, env vars and service ports. Any format works (OpenAPI, JSON Schema, pydantic, markdown), but always include an example request/response or record.
- A contract lives in the folder of the zone that implements it: `contracts/ai/`, `contracts/data/`, `contracts/backend/`. That zone's owner owns the folder (CODEOWNERS); consumers review. Files directly in `contracts/` (e.g. service ports) are shared by everyone.
- A consumer needs a change? Only on your operator's explicit request, open a PR that edits the provider's contract; the hook asks the human to confirm, and the bot merges only after the provider approves. Don't touch the provider's code.
- Changed a contract? Add an entry to your section of `STATE.md` in the same PR (the gate enforces this).
- Breaking change (removed or renamed field or endpoint, changed type or meaning, new required field): your operator agrees it with the consumers first; the bot merges only after a consumer approves (a teammate other than the author who doesn't own that contract; for files directly in `contracts/`, any teammate other than the author).

## STATE.md: shared board of cross-zone changes
Only what another teammate's agent needs to know so their code doesn't break, or so they can use yours.
- Yes: a new or changed contract; a module or service that neighbours will call; a new env var, port or service needed to run things; an architecture decision that constrains others.
- No: bug fixes, refactors inside a zone, styling, tests, perf tweaks without interface changes, WIP.
- Write only in your operator's section, in the same PR as the change. Newest on top.
- It's the current picture, not a log: replace or delete superseded entries (git keeps the history). At most 10 entries per section.
- Format: `- YYYY-MM-DD · **what** — what changed for others. Affects: zones. Where: path.`

## HANDOFF.md: personal state between sessions (gitignored)
Lives at the repo root, one per person, never committed. The agent maintains it.
- Update it after a meaningful step (commit, finished subtask, decision made) and before the session ends. Not after every edit.
- Only what matters: what was done and why, what's next, open questions. Delete what's done or stale. At most 40 lines.
- Structure:
  ```
  # HANDOFF — <login>
  ## Now                 task, branch
  ## Done                what → why
  ## Next
  ## Questions/blockers
  ```

## Git and PRs
- Two long-lived branches: `dev` (default; features land here) and `main` (production; changes only through a release PR `dev` → `main` that the repo owner merges by hand, then tags `vX.Y`).
- Branch `<login>/<short-name>` from a fresh `dev`, PR back into `dev`. Small PRs that live hours, not days. Squash-merge. No direct pushes to `dev` or `main`.
- The `pr-merger` bot (`.claude/agents/pr-merger.md`, run by `.github/workflows/auto-merge.yml`) merges PRs into `dev` automatically once `gate` is green and the zone rules hold. Touched another zone? Its owner approves the PR, then the bot merges. To keep a PR open, make it a draft or add the label `no-automerge`.
- Before opening a PR, run `python3 scripts/team.py pr`. It's the same gate CI runs: checks of changed zones plus the "contract → STATE.md" rule. The Claude hook runs it automatically before `gh pr create`.
- The PR description is the filled-in `.github/pull_request_template.md`, passed with `gh pr create --body` or `--body-file`. In non-interactive mode gh ignores the template, so the Claude hook rejects `--fill` and bodies without the template's sections.
- Never commit secrets or `.env` (keep a `.env.example` in the zone), raw data or model weights.
- No destructive git operations (`reset --hard`, `push --force` to shared branches, deleting others' branches) unless your operator explicitly asks.

## Checks and tests
- Every zone with code has `<zone>/check.sh`: fast checks (lint + unit tests), about 60 s max, no model training. The owner decides what goes in it. It runs from the zone folder.
- `check.sh` sets up its own environment idempotently and must pass on a fresh clone and on a bare `ubuntu-latest` CI runner (python3, node, npm preinstalled; no uv). Examples: `uv run pytest` after `command -v uv || pip install uv`, `[ -d node_modules ] || npm ci`. Installing dependencies may use the network; the tests themselves must not.
- Test what breaks neighbours (contract conformance) and critical logic. Don't chase coverage.
- `python3 scripts/team.py check` runs the checks of zones changed in the working tree. The Claude hook runs them before the agent ends its turn and won't let it finish on red.
- Never claim something works unless you ran the check and saw its output.
