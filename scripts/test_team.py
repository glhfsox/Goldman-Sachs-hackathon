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

# every zone has its own line in the real CODEOWNERS
patterns = [p for p, _ in team.codeowners()]
for zone in ["ai", "data", "backend", "frontend", "presentation", "contracts"]:
    assert f"/{zone}/" in patterns, zone

print("test_team: ok")
