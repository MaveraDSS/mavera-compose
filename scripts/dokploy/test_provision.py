"""Offline checks for provision.find_environment against fake project.all data."""
import importlib.util
import io
import sys
from contextlib import redirect_stdout

spec = importlib.util.spec_from_file_location(
    "prov", "scripts/dokploy/provision.py"
)
prov = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prov)


class FakeClient:
    def __init__(self, projects):
        self.projects = projects

    def get(self, path):
        assert path == "/project.all", path
        return self.projects


PROJECTS = [
    {
        "name": "mavera",
        "projectId": "proj-mavera",
        "environments": [
            {
                "name": "production",
                "environmentId": "env-prod",
                "applications": [{"name": "mavera-audit", "appName": "mavera-audit-k3f9qz"}],
            },
            {"name": "staging", "environmentId": "env-stage", "applications": []},
        ],
    },
    {
        "name": "Other Project",
        "projectId": "proj-other",
        "environments": [{"name": "production", "environmentId": "env-other", "applications": []}],
    },
]

DUPES = [
    {"name": "mavera", "projectId": "proj-a", "environments": []},
    {"name": "mavera", "projectId": "proj-b", "environments": []},
]

results = []


def check(label, fn, expect_exit=None, expect_env=None):
    buf = io.StringIO()
    try:
        with redirect_stdout(buf):
            env_id, existing = fn()
    except SystemExit as e:
        msg = str(e)
        if expect_exit and expect_exit.lower() in msg.lower():
            results.append((True, label, f"exited: {msg.splitlines()[0][:70]}"))
        else:
            results.append((False, label, f"UNEXPECTED EXIT: {msg[:150]}"))
        return
    if expect_exit:
        results.append((False, label, f"expected exit containing {expect_exit!r}, got {env_id}"))
    elif expect_env and env_id != expect_env:
        results.append((False, label, f"expected {expect_env}, got {env_id}"))
    else:
        results.append((True, label, f"-> {env_id}, {len(existing)} existing"))


c = FakeClient(PROJECTS)

check("default name lookup",
      lambda: prov.find_environment(c, "mavera", "production"), expect_env="env-prod")
check("non-default project name",
      lambda: prov.find_environment(c, "Other Project", "production"), expect_env="env-other")
check("case-insensitive project",
      lambda: prov.find_environment(c, "MAVERA", "production"), expect_env="env-prod")
check("case-insensitive environment",
      lambda: prov.find_environment(c, "mavera", "Production"), expect_env="env-prod")
check("non-default environment",
      lambda: prov.find_environment(c, "mavera", "staging"), expect_env="env-stage")
check("lookup by projectId",
      lambda: prov.find_environment(c, "ignored", "production", "proj-other"),
      expect_env="env-other")
check("lookup by environmentId",
      lambda: prov.find_environment(c, "mavera", "ignored", None, "env-stage"),
      expect_env="env-stage")
check("unknown project lists candidates",
      lambda: prov.find_environment(c, "nope", "production"),
      expect_exit="no project named")
check("unknown environment lists candidates",
      lambda: prov.find_environment(c, "mavera", "nope"),
      expect_exit="has no environment")
check("unknown projectId",
      lambda: prov.find_environment(c, "x", "production", "proj-missing"),
      expect_exit="no project with projectId")
check("duplicate names are rejected, not guessed",
      lambda: prov.find_environment(FakeClient(DUPES), "mavera", "production"),
      expect_exit="projects are named")
check("empty project list",
      lambda: prov.find_environment(FakeClient([]), "mavera", "production"),
      expect_exit="no projects")

for ok, label, detail in results:
    print(f"{'PASS' if ok else 'FAIL'}  {label:45} {detail}")

failures = [r for r in results if not r[0]]
print(f"\n{len(results) - len(failures)}/{len(results)} passed")
sys.exit(1 if failures else 0)
