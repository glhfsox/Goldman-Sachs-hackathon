import team

RULES = [("*", ["@lead"]), ("/data/", ["@lead", "@d"]), ("/backend/", ["@Bob"]), ("/STATE.md", ["@lead", "@Bob"])]

assert team.owners_of("README.md", RULES) == ["@lead"]
assert team.owners_of("backend/app/main.py", RULES) == ["@Bob"]
assert team.owners_of("backendx/main.py", RULES) == ["@lead"]
assert team.owners_of("STATE.md", RULES) == ["@lead", "@Bob"]
assert team.owners_of("data/x.csv", RULES) == ["@lead", "@d"]
assert team.owns("bob", "backend/x.py", RULES)  # logins are case-insensitive
assert not team.owns("d", "backend/x.py", RULES)

errors, warnings = team.gate(["contracts/api.md", "data/x.py"], "lead", RULES)
assert len(errors) == 1 and not warnings  # contract changed without STATE.md
errors, warnings = team.gate(["contracts/api.md", "STATE.md", "backend/x.py"], "lead", RULES)
assert not errors and len(warnings) == 1 and warnings[0].startswith("backend/x.py")
assert team.gate(["backend/x.py"], "Bob", RULES) == ([], [])

for cmd in ["git config team.member x", "git config --local team.member x", "git config --unset team.member"]:
    assert team.SET_MEMBER.search(cmd), cmd
for cmd in ["git config team.member", "git config team.member && ls", 'echo "$(git config team.member)"']:
    assert not team.SET_MEMBER.search(cmd), cmd

# regression: non-ASCII paths and renames must keep their zones (git quotes them without -z)
import subprocess, tempfile
from pathlib import Path
with tempfile.TemporaryDirectory() as tmp:
    run = lambda *a: subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", *a], cwd=tmp, check=True,
                                    capture_output=True)
    run("init", "-q", "-b", "main")
    (Path(tmp) / "contracts").mkdir()
    (Path(tmp) / "contracts" / "x.md").write_text("x")
    run("add", "-A")
    run("commit", "-qm", "base")
    run("mv", "contracts/x.md", "contracts/y.md")
    (Path(tmp) / "data").mkdir()
    (Path(tmp) / "data" / "naïve.csv").write_text("x")
    team.ROOT, root = Path(tmp), team.ROOT
    try:
        assert sorted(team.tree_changes()) == ["contracts/x.md", "contracts/y.md", "data/naïve.csv"], team.tree_changes()
        run("add", "-A")
        run("commit", "-qm", "change")
        assert sorted(team.branch_changes("HEAD~1")) == ["contracts/x.md", "contracts/y.md", "data/naïve.csv"]
    finally:
        team.ROOT = root

# every zone has its own line in the real CODEOWNERS; provider contract folders override the shared contracts/ line
real = team.codeowners()
patterns = [p for p, _ in real]
for zone in ["ai", "data", "backend", "frontend", "presentation", "contracts"]:
    assert f"/{zone}/" in patterns, zone
assert team.owners_of("contracts/backend/api.md", real) == ["@SviatoslavShpylovyi"]
assert team.owners_of("contracts/data/dataset.md", real) == ["@glhfsox", "@spdsslg"]
assert len(team.owners_of("contracts/ports.md", real)) == 4

# `gh pr create` must carry the filled-in template
filled = "## What and why\nadd x\n## Zones and contracts\n- none\n## How to verify\nrun y"
body = lambda cmd, cwd=".": team.pr_body_problem(cmd, team.gh_pr_create_args(cmd), cwd)
assert team.gh_pr_create_args('grep -n "gh pr create" scripts') is None
assert team.gh_pr_create_args('echo "x; gh pr create"') is None
assert team.gh_pr_create_args("gh pr create --help") == ["--help"]
assert team.gh_pr_create_args("git push -f && gh pr create -t x && rm -f b.md") == ["-t", "x"]
assert body("gh pr create --fill")
assert body("gh pr create -f -t x")
assert body('gh pr create -t x --body "just text"')
assert body(f'gh pr create -t x --body "{filled}"') is None
assert body(f"git push -f origin HEAD && gh pr create -t x --body '{filled}'") is None
assert body(f"gh pr create -t x -F - <<'EOF'\n{filled}\ncurl -f localhost\nEOF") is None
assert body("gh pr create --web") is None
assert team.pr_base(team.gh_pr_create_args("gh pr create -t x")) == "origin/dev"
assert team.pr_base(team.gh_pr_create_args("gh pr create -B main -t x")) == "origin/main"
assert team.pr_base(team.gh_pr_create_args("gh pr create --base=main")) == "origin/main"
with tempfile.TemporaryDirectory() as tmp:
    (Path(tmp) / "body.md").write_text(filled)
    assert body("gh pr create -t x --body-file body.md", tmp) is None
    assert body("gh pr create -t x --body-file=body.md", tmp) is None
    assert "Can't read" in body("gh pr create -t x -F missing.md", tmp)
    (Path(tmp) / "bad.md").write_bytes(b"\xff\xfe")
    assert body("gh pr create -t x -F bad.md", tmp)  # undecodable bytes give a reason, not a crash

# Codex: apply_patch paths are parsed from the patch, and "ask" becomes "deny" (Codex would let an ask through)
patch = ("*** Begin Patch\n*** Add File: ai/new.py\n+x\n*** Update File: backend/app.py\n*** Move to: backend/b.py\n"
         "@@\n-*** Delete File: fake.py\n*** Delete File: data/old.csv\n*** End Patch")
assert team.patch_paths(patch) == ["ai/new.py", "backend/app.py", "backend/b.py", "data/old.csv"]
team.me, team.codeowners = lambda: "lead", lambda: RULES
assert team.edit_problem(["ai/new.py", "../elsewhere.py", "HANDOFF.md"], team.ROOT) is None
assert team.edit_problem(team.patch_paths(patch), team.ROOT)[0] == "ask"
import contextlib, io, json
for codex, want in [(False, "ask"), (True, "deny")]:
    with contextlib.redirect_stdout(io.StringIO()) as out:
        team.pre("ask", "x", codex)
    assert json.loads(out.getvalue())["hookSpecificOutput"]["permissionDecision"] == want

# board: TTL by last activity, only my zones, never my own posts, capped output
from datetime import datetime, timedelta, timezone
assert team.zones(real) == ["ai", "data", "backend", "frontend", "presentation"]
now = datetime(2026, 11, 19, 12, tzinfo=timezone.utc)
issue = lambda n, who, zs, hours: {"number": n, "title": f"t{n}", "author": {"login": who},
                                   "labels": [{"name": "board"}] + [{"name": f"zone:{z}"} for z in zs],
                                   "updatedAt": (now - timedelta(hours=hours)).strftime("%Y-%m-%dT%H:%M:%SZ")}
expired, lines = team.board_view([issue(1, "Bob", ["data"], 1), issue(2, "Bob", ["data"], 4),
                                  issue(3, "Bob", ["frontend"], 1), issue(4, "Lead", ["data"], 1),
                                  issue(5, "Ann", ["backend", "data"], 0.5)], "lead", {"data", "ai"}, now)
assert expired == [2], expired  # stale ones expire even when they aren't for me
assert lines == ["#5 @Ann [backend,data] t5", "#1 @Bob [data] t1"], lines  # newest first
seen = {"1": issue(1, "Bob", ["data"], 1)["updatedAt"], "5": "2026-01-01T00:00:00Z"}
_, lines = team.board_view([issue(1, "Bob", ["data"], 1), issue(5, "Ann", ["data"], 0.5)], "lead", {"data"}, now, seen)
assert lines == ["#5 @Ann [data] t5"], lines  # #1 was already shown; #5 changed since
_, lines = team.board_view([issue(n, "Bob", ["data"], 0) for n in range(15)], "lead", {"data"}, now)
assert len(lines) == team.BOARD_SHOWN + 1 and lines[-1].startswith("(+5 older")

print("test_team: ok")
