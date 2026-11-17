# STATE

Cross-zone changes: only what other teammates' agents need to know. Rules are in AGENTS.md, section "STATE.md". Write only in your own section, newest on top, delete what's superseded.

Format: `- YYYY-MM-DD · **what** — what changed for others. Affects: zones. Where: path.`

## glhfsox — ai, data
- 2026-11-17 · **Auto-merge bot** — the `pr-merger` agent squash-merges a PR once `gate` is green and zone rules hold; PRs touching another zone wait for that owner's approval. Draft or label `no-automerge` to opt out. Affects: all. Where: `.claude/agents/pr-merger.md`, `.github/workflows/auto-merge.yml`.
- 2026-11-17 · **Repo infrastructure** — zones by folder; cross-zone interfaces only through `contracts/<provider-zone>/` (the provider owns it, consumers review); Claude Code hooks (PRs must carry the filled-in template) and a CI gate on PRs. Everyone: run `git config team.member <login>` and add a `check.sh` to your zone. Affects: all. Where: `AGENTS.md`, `scripts/team.py`.

## spdsslg — data

## SviatoslavShpylovyi — backend

## oshuptar — frontend, presentation
