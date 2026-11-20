Rules for humans and agents: [AGENTS.md](AGENTS.md). Cross-zone changes: [STATE.md](STATE.md).

## Setup (once after cloning)
```bash
git config team.member <your-github-login>   # same login as in .github/CODEOWNERS
python3 --version                            # 3.9+ is needed for the hooks
```
- Claude Code picks up `CLAUDE.md` and the hooks in `.claude/settings.json` automatically (it asks you to trust the project on first run).
- Codex reads `AGENTS.md` and runs the same hooks from `.codex/hooks.json` once you trust the project and approve them in `/hooks` (again after every change to that file).
- Other agents (Cursor, …) read `AGENTS.md` but get no hooks. CI catches the same things on the PR.
- Add a `check.sh` to your zone (fast lint + tests). The hooks and CI run it.

## What the hooks do
| Where | When | What |
|---|---|---|
| Claude, Codex | file edit | file outside your zone → Claude asks the human to confirm; Codex can't ask, so it's blocked |
| Claude, Codex | `git commit` | reminds the agent to update HANDOFF.md / STATE.md |
| Claude, Codex | `gh pr create` | requires the filled-in PR template and runs the PR gate; on errors, blocks the PR |
| Claude, Codex | end of turn | `check.sh` of changed zones; red → the agent keeps fixing |
| Claude, Codex | session start, then every 10 min | board: GitHub issues for your zones (`team.py post` / `board`); at start all open ones, then only new or updated; closes messages idle for 3 h |
| CI | every PR | PR gate: `check.sh` of changed zones, "contracts/ → STATE.md", warnings about other zones |
| CI | `gate` passed | `pr-merger` agent (Sonnet, high effort) squash-merges a PR into `dev` or comments what blocks it; a PR into `main` only from `dev`, as a merge commit |

## Branches and releases
- `dev` is the integration branch: feature branches start from it and PR back into it with `-B dev`, and the bot merges them. GitHub's default branch is `main`, so a PR without `-B` would target it; the hook blocks that.
- `main` is production. Release = a PR `dev` → `main` (`gh pr create -B main -H dev`); the bot merges it with a merge commit (not squash, so `dev` and `main` don't diverge) and refuses PRs into `main` from any other branch. Then tag it: `git tag v0.1 origin/main && git push origin v0.1`.
