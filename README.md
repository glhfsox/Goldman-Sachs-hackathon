# hackathontraining

Rules for humans and agents: [AGENTS.md](AGENTS.md). Cross-zone changes: [STATE.md](STATE.md).

## Setup (once after cloning)
```bash
git config team.member <your-github-login>   # same login as in .github/CODEOWNERS
python3 --version                            # 3.9+ is needed for the hooks
```
- Claude Code picks up `CLAUDE.md` and the hooks in `.claude/settings.json` automatically (it asks you to trust the project on first run).
- Other agents (Codex, Cursor, …) read `AGENTS.md` but don't get the Claude hooks. CI catches the same things on the PR.
- Add a `check.sh` to your zone (fast lint + tests). The hooks and CI run it.

## What the hooks do
| Where | When | What |
|---|---|---|
| Claude | file edit | file outside your zone → asks the human to confirm |
| Claude | `git commit` | reminds the agent to update HANDOFF.md / STATE.md |
| Claude | `gh pr create` | requires the filled-in PR template and runs the PR gate; on errors, blocks the PR |
| Claude | end of turn | `check.sh` of changed zones; red → the agent keeps fixing |
| CI | every PR | PR gate: `check.sh` of changed zones, "contracts/ → STATE.md", warnings about other zones |
| CI | `gate` passed / PR approved | `pr-merger` agent (Sonnet, high effort) squash-merges the PR or comments what blocks it |

## GitHub (repo owner, once)
- Invite teammates: Settings → Collaborators.
- Auto-merge bot: run `claude setup-token` locally, then save the token as the repo secret `CLAUDE_CODE_OAUTH_TOKEN` (`gh secret set CLAUDE_CODE_OAUTH_TOKEN`). Every bot run uses the repo owner's Claude subscription.
- Branch protection (Settings → Branches → `main`: Require a pull request + status check `gate`) and automatic CODEOWNERS review requests need a public repo or GitHub Pro. Without them nothing stops a direct push to `main`, but the hooks and the bot read CODEOWNERS themselves, so zone rules still hold for PRs.
- If you have branch protection, don't turn on "Require review from Code Owners": a zone owner can't approve their own PR, so everything would stall. The bot already waits for the owner when someone else touches their zone.
