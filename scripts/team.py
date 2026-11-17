#!/usr/bin/env python3
"""Team checks and Claude Code hooks. Stdlib only.

  python3 scripts/team.py check                                     # check.sh of zones changed in the working tree
  python3 scripts/team.py pr [--base origin/main] [--author LOGIN]  # PR gate, CI runs the same
  python3 scripts/team.py hook                                      # Claude Code hook entry point (JSON on stdin)
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
COMMIT = re.compile(r"\bgit\b[^|;&]*\bcommit\b")
SET_MEMBER = re.compile(r"team\.member[ \t]+[^\s|;&)]|unset\S*\s+team\.member")  # writes, not `git config team.member`
REMINDER = (
    "Commit done. If this was a meaningful step, update HANDOFF.md (what/why/next, delete stale lines). "
    "If it changed how your zone touches others (contract, API, data format, service), "
    "add an entry to your section of STATE.md."
)


def git(*args, check=True):
    r = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True)
    if check and r.returncode:
        sys.exit(f"git {' '.join(args)}: {r.stderr.strip()}")
    return r.stdout


def me():
    return git("config", "team.member", check=False).strip()


def codeowners():
    rules = []
    for line in (ROOT / ".github" / "CODEOWNERS").read_text().splitlines():
        parts = line.split("#")[0].split()
        if parts:
            rules.append((parts[0], parts[1:]))
    return rules


def owners_of(path, rules):
    # shortcut: understands only "*", "/dir/" and "/file"; switch to fnmatch if glob patterns are needed
    owners = []
    for pattern, who in rules:
        p = pattern.lstrip("/")
        if pattern == "*" or path == p or (p.endswith("/") and path.startswith(p)):
            owners = who  # like GitHub: the last match wins
    return owners


def owns(login, path, rules):
    # GitHub logins are case-insensitive
    return f"@{login}".lower() in (o.lower() for o in owners_of(path, rules))


# -z: no quoting of non-ASCII paths; --no-renames: a moved file counts in both the old and the new zone
def tree_changes():
    out = git("status", "--porcelain", "-z", "--no-renames", "--untracked-files=all")
    return [e[3:] for e in out.split("\0") if e]  # entries are "XY path"


def branch_changes(base):
    return [f for f in git("diff", "--name-only", "-z", "--no-renames", f"{base}...HEAD").split("\0") if f]


def run_checks(files):
    """Run <zone>/check.sh for every changed zone that has one. Returns failure reports."""
    failed = []
    for zone in sorted({f.split("/")[0] for f in files if "/" in f}):
        if not (ROOT / zone / "check.sh").is_file():
            continue
        print(f"== {zone}/check.sh", file=sys.stderr, flush=True)
        r = subprocess.run(["bash", "check.sh"], cwd=ROOT / zone, capture_output=True, text=True)
        output = r.stdout + r.stderr
        print(output, file=sys.stderr, end="", flush=True)
        if r.returncode:
            tail = "\n".join(output.splitlines()[-40:])
            failed.append(f"{zone}/check.sh failed (exit {r.returncode}):\n{tail}")
    return failed


def gate(files, author, rules):
    """Pure part of the PR gate: (errors, warnings)."""
    errors = []
    if any(f.startswith("contracts/") for f in files) and "STATE.md" not in files:
        errors.append("contracts/ changed but STATE.md did not: add an entry to your section of STATE.md.")
    warnings = [
        f"{f}: zone of {' '.join(owners_of(f, rules)) or 'nobody'}, not @{author}; mention the owner in the PR."
        for f in files
        if author and not owns(author, f, rules)
    ]
    return errors, warnings


def pr_report(base, author):
    files = branch_changes(base)
    errors, warnings = gate(files, author, codeowners())
    return errors + run_checks(files), warnings


def cmd_pr(base, author):
    errors, warnings = pr_report(base, author)
    ci = os.environ.get("GITHUB_ACTIONS")
    for w in warnings:
        print(("::warning::" if ci else "warning: ") + w)
    for e in errors:
        print(("::error::" if ci else "error: ") + e)
    if errors:
        sys.exit(1)
    print("PR gate passed")


def pre(decision, reason):
    print(json.dumps({"hookSpecificOutput": {
        "hookEventName": "PreToolUse", "permissionDecision": decision, "permissionDecisionReason": reason}}))


def guard_edit(path):
    if not path:
        return
    try:
        rel = Path(path).resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return  # outside the repo: not our business
    if rel == "HANDOFF.md":
        return
    who = me()
    if not who:
        return pre("deny", "Operator not set. Ask the human to run "
                           "`git config team.member <github-login>` (never set it yourself).")
    rules = codeowners()
    if not owns(who, rel, rules):
        pre("ask", f"{rel} is in the zone of {' '.join(owners_of(rel, rules)) or 'nobody'}, not @{who}. "
                   "Allow only if you explicitly asked for this change.")


def cmd_hook():
    data = json.load(sys.stdin)
    event, tool = data.get("hook_event_name"), data.get("tool_name")
    inp = data.get("tool_input") or {}
    cmd = inp.get("command", "")
    if event == "PreToolUse" and tool == "Bash":
        if SET_MEMBER.search(cmd):
            pre("ask", "Changing the operator identity (team.member) is for the human only.")
        elif "gh pr create" in cmd:
            # shortcut: base is always origin/main, gh's --base flag is ignored
            try:
                errors, _ = pr_report("origin/main", me())
            except SystemExit as e:  # exit 1 would be a non-blocking hook error and let the PR through
                errors = [f"PR gate could not run: {e}"]
            if errors:
                pre("deny", "PR gate failed, fix before creating the PR:\n\n" + "\n\n".join(errors))
    elif event == "PreToolUse":
        guard_edit(inp.get("file_path") or inp.get("notebook_path"))
    elif event == "PostToolUse" and tool == "Bash" and COMMIT.search(cmd):
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": REMINDER}}))
    elif event == "Stop" and not data.get("stop_hook_active"):
        failed = run_checks(tree_changes())
        if failed:
            print(json.dumps({"decision": "block", "reason": (
                "Checks are red:\n\n" + "\n\n".join(failed) +
                "\n\nFix them. If the failure is expected (WIP), say so to your operator and end the turn.")}))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["check", "pr", "hook"])
    ap.add_argument("--base", default="origin/main")
    ap.add_argument("--author")
    a = ap.parse_args()
    if a.cmd == "hook":
        cmd_hook()
    elif a.cmd == "check":
        sys.exit(1 if run_checks(tree_changes()) else 0)
    else:
        cmd_pr(a.base, a.author if a.author is not None else me())
