# hackathontraining

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
| CI | every PR | PR gate: `check.sh` of changed zones, "contracts/ → STATE.md", warnings about other zones |
| CI | `gate` passed / PR approved | `pr-merger` agent (Sonnet, high effort) squash-merges a PR into `dev` or comments what blocks it; a PR into `main` only from `dev`, as a merge commit |

## Branches and releases
- `dev` is the default branch: feature branches start from it and PR back into it, and the bot merges them.
- `main` is production. Release = a PR `dev` → `main` (`gh pr create -B main -H dev`); the bot merges it with a merge commit (not squash, so `dev` and `main` don't diverge) and refuses PRs into `main` from any other branch. Then tag it: `git tag v0.1 origin/main && git push origin v0.1`.

## GitHub (repo owner, once)
- Default branch: Settings → General → Default branch → `dev`. The bot reads its rules from the default branch.
- Invite teammates: Settings → Collaborators.
- Auto-merge bot: run `claude setup-token` locally, then save the token as the repo secret `CLAUDE_CODE_OAUTH_TOKEN` (`gh secret set CLAUDE_CODE_OAUTH_TOKEN`). Every bot run uses the repo owner's Claude subscription.
- Branch protection (Settings → Branches → `dev` and `main`: Require a pull request + status check `gate`) and automatic CODEOWNERS review requests need a public repo or GitHub Pro. Without them nothing stops a direct push to `dev` or `main`, but the hooks and the bot read CODEOWNERS themselves, so zone rules still hold for PRs.
- If you have branch protection, don't turn on "Require review from Code Owners": a zone owner can't approve their own PR, so everything would stall. The bot already waits for the owner when someone else touches their zone.
