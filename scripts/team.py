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
import shlex
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
COMMIT = re.compile(r"\bgit\b[^|;&]*\bcommit\b")
SET_MEMBER = re.compile(r"team\.member[ \t]+[^\s|;&)]|unset\S*\s+team\.member")  # writes, not `git config team.member`
GH_PR_CREATE = re.compile(r"(?:^|[;&|(\n])\s*gh\s+pr\s+create\b")
HOW_TO_BODY = ("Fill .github/pull_request_template.md and pass it with --body or --body-file "
               "(not a $(...) substitution: the hook can't see its output).")
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


def gh_pr_create_args(cmd):
    """gh's own arguments of a real `gh pr create` call in a shell command; None if the command doesn't make one."""
    if not GH_PR_CREATE.search(cmd):
        return None
    try:
        lex = shlex.shlex(cmd, posix=True, punctuation_chars=True)
        lex.whitespace_split, lex.commenters = True, ""
        tokens, is_op = list(lex), lambda t: set(t) <= set("();<>|&")
    except ValueError:  # e.g. an unbalanced quote: rough split
        tokens, is_op = cmd.split(), lambda t: t[:1] in "();<>|&"
    for i in range(len(tokens) - 2):
        if tokens[i:i + 3] == ["gh", "pr", "create"]:
            args = []
            for t in tokens[i + 3:]:
                if is_op(t):  # the next shell operator (&&, ;, |, <<, ...) ends gh's arguments
                    break
                args.append(t)
            return args
    return None


def pr_body_problem(cmd, args, cwd):
    """gh ignores the PR template in non-interactive mode, so `gh pr create` must carry the filled-in template."""
    if any(a in ("-w", "--web") for a in args):
        return None  # the browser form shows the template
    if any(a in ("-f", "--fill", "--fill-first", "--fill-verbose") for a in args):
        return "--fill skips the PR template. " + HOW_TO_BODY
    text = cmd  # covers --body "..." and heredocs
    files = [v for f, v in zip(args, args[1:]) if f in ("-F", "--body-file")]
    files += [a.split("=", 1)[1] for a in args if a.startswith("--body-file=")]
    for name in files:
        if name == "-":
            continue
        p = Path(cwd, os.path.expandvars(os.path.expanduser(name)))
        if not p.is_file():
            return f"Can't read --body-file {name} (resolved from {cwd}); use an absolute path."
        text += p.read_text(errors="replace")
    template = ROOT / ".github" / "pull_request_template.md"
    headings = [ln.strip() for ln in template.read_text().splitlines() if ln.startswith("## ")] if template.is_file() else []
    missing = [h for h in headings if h not in text]
    if missing:
        return f"PR body is missing template sections: {', '.join(missing)}. " + HOW_TO_BODY
    return None


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
        elif (args := gh_pr_create_args(cmd)) is not None and not {"-h", "--help"} & set(args):
            # shortcut: base is always origin/main, gh's --base flag is ignored
            try:
                body = pr_body_problem(cmd, args, data.get("cwd") or ROOT)
                errors, _ = pr_report("origin/main", me())
                errors = ([body] if body else []) + errors
            except (Exception, SystemExit) as e:  # a crashing hook exits 1, which doesn't block the PR
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
