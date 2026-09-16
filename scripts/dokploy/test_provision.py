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

    def __getattr__(self, name):
        raise AttributeError(name)

    def get(self, path):
        if path == "/project.all":
            return self.projects
        if path.startswith("/environment.one?environmentId="):
            wanted = path.split("=", 1)[1]
            for p in self.projects:
                for e in p.get("environments") or []:
                    if e.get("environmentId") == wanted:
                        return e
            return {}
        raise AssertionError(f"unexpected GET {path}")


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


def check(label, ok, detail=""):
    """Boolean form, for assertions that are not about find_environment exits."""
    results.append((bool(ok), label, str(detail)))


def check_env(label, fn, expect_exit=None, expect_env=None):
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

check_env("default name lookup",
      lambda: prov.find_environment(c, "mavera", "production"), expect_env="env-prod")
check_env("non-default project name",
      lambda: prov.find_environment(c, "Other Project", "production"), expect_env="env-other")
check_env("case-insensitive project",
      lambda: prov.find_environment(c, "MAVERA", "production"), expect_env="env-prod")
check_env("case-insensitive environment",
      lambda: prov.find_environment(c, "mavera", "Production"), expect_env="env-prod")
check_env("non-default environment",
      lambda: prov.find_environment(c, "mavera", "staging"), expect_env="env-stage")
check_env("lookup by projectId",
      lambda: prov.find_environment(c, "ignored", "production", "proj-other"),
      expect_env="env-other")
check_env("lookup by environmentId",
      lambda: prov.find_environment(c, "mavera", "ignored", None, "env-stage"),
      expect_env="env-stage")
check_env("unknown project lists candidates",
      lambda: prov.find_environment(c, "nope", "production"),
      expect_exit="no project named")
check_env("unknown environment lists candidates",
      lambda: prov.find_environment(c, "mavera", "nope"),
      expect_exit="has no environment")
check_env("unknown projectId",
      lambda: prov.find_environment(c, "x", "production", "proj-missing"),
      expect_exit="no project with projectId")
check_env("duplicate names are rejected, not guessed",
      lambda: prov.find_environment(FakeClient(DUPES), "mavera", "production"),
      expect_exit="projects are named")
check_env("empty project list",
      lambda: prov.find_environment(FakeClient([]), "mavera", "production"),
      expect_exit="no projects")


# --- the existence check must not silently miss an application ---------------
# Getting this wrong means a re-run creates a duplicate instead of updating.

class BrokenEnvOne(FakeClient):
    """environment.one errors; the nested project.all data must be used."""

    def get(self, path):
        if path.startswith("/environment.one"):
            raise prov.DokployError("GET /environment.one -> 404")
        return super().get(path)


class EmptyEnvOne(FakeClient):
    """environment.one answers but omits applications; fall back rather than
    conclude the environment is empty."""

    def get(self, path):
        if path.startswith("/environment.one"):
            return {"environmentId": "env-prod"}
        return super().get(path)


def existing_names(client, **kw):
    buf = io.StringIO()
    with redirect_stdout(buf):
        _, existing = prov.find_environment(client, "mavera", "production", **kw)
    return set(existing), buf.getvalue()


names, out = existing_names(FakeClient(PROJECTS))
check("environment.one is the primary source",
      names == {"mavera-audit"} and "via environment.one" in out,
      f"{sorted(names)}")

names, out = existing_names(BrokenEnvOne(PROJECTS))
check("environment.one failure falls back to project.all",
      names == {"mavera-audit"} and "fallback" in out,
      f"{sorted(names)}")

names, out = existing_names(EmptyEnvOne(PROJECTS))
check("empty environment.one falls back rather than reporting none",
      names == {"mavera-audit"},
      f"{sorted(names)}")

names, out = existing_names(FakeClient(PROJECTS), environment_id="env-stage")
check("genuinely empty environment reports none",
      names == set() and "no existing applications" in out,
      f"{sorted(names)}")

names, out = existing_names(FakeClient(PROJECTS))
check("existing application names are printed, not just counted",
      "mavera-audit" in out and "mavera-audit-k3f9qz" in out)

for ok, label, detail in results:
    print(f"{'PASS' if ok else 'FAIL'}  {label:45} {detail}")

failures = [r for r in results if not r[0]]
print(f"\n{len(results) - len(failures)}/{len(results)} passed")
sys.exit(1 if failures else 0)
