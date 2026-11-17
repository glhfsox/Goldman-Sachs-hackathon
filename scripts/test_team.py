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
with tempfile.TemporaryDirectory() as tmp:
    (Path(tmp) / "body.md").write_text(filled)
    assert body("gh pr create -t x --body-file body.md", tmp) is None
    assert body("gh pr create -t x --body-file=body.md", tmp) is None
    assert "Can't read" in body("gh pr create -t x -F missing.md", tmp)
    (Path(tmp) / "bad.md").write_bytes(b"\xff\xfe")
    assert body("gh pr create -t x -F bad.md", tmp)  # undecodable bytes give a reason, not a crash

print("test_team: ok")
