"""Offline checks for provision.verify() — the post-apply read-back.

The case that matters most: an application left on Dokploy's default
buildType="nixpacks" because saveBuildType failed. Nixpacks ignores
dockerfile/Dockerfile, auto-detects the language and picks its own SDK version,
which surfaces much later as a build installing the wrong .NET major.
"""
import importlib.util
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
_s = importlib.util.spec_from_file_location(
    "prov", REPO_ROOT / "scripts" / "dokploy" / "provision.py"
)
prov = importlib.util.module_from_spec(_s)
_s.loader.exec_module(prov)

SPEC = {
    "name": "mavera-audit",
    "owner": "MaveraDSS",
    "repository": "mavera-audit",
    "branch": "develop",
    "dockerfile": "dockerfile/Dockerfile",
    "appDll": "Mavera-Audit.dll",
    "port": 80,
    "aliases": ["mavera-audit", "mavera-audit.svc.cluster.local"],
    "healthPath": None,
    "domainEnv": None,
    "envFile": "env/mavera-audit.env",
}

CMD, ARGS = prov.entrypoint_spec("inline")

GOOD = {
    "applicationId": "app-1",
    "appName": "mavera-audit-bwqwzr",
    "buildType": "dockerfile",
    "dockerfile": "dockerfile/Dockerfile",
    "dockerContextPath": ".",
    "repository": "mavera-audit",
    "branch": "develop",
    "sourceType": "github",
    "isPreviewDeploymentsActive": False,
    "command": CMD,
    "args": ARGS,
    "networkSwarm": [
        {"Target": "dokploy-network",
         "Aliases": ["mavera-audit", "mavera-audit.svc.cluster.local"]}
    ],
    "env": "APP_DLL=Mavera-Audit.dll\nASPNETCORE_HTTP_PORTS=80",
}


class Client:
    def __init__(self, app):
        self.app = app

    def get(self, path):
        assert path.startswith("/application.one?applicationId="), path
        if isinstance(self.app, Exception):
            raise self.app
        return self.app


results = []


def check(label, ok, detail=""):
    results.append((bool(ok), label, str(detail)))


def run(app, role="production", mode="inline"):
    return prov.verify(Client(app), "app-1", SPEC, role, mode)


# --- the happy path -----------------------------------------------------------
check("fully provisioned app verifies clean", run(GOOD) == [], run(GOOD))


# --- THE regression: left on nixpacks ----------------------------------------
nixpacks = dict(GOOD, buildType="nixpacks")
problems = run(nixpacks)
check("buildType=nixpacks is caught",
      any("buildType" in p and "nixpacks" in p for p in problems), problems)
check("the nixpacks message explains the consequence",
      any("ignores dockerfile/Dockerfile" in p for p in problems), problems)


# --- each other field independently ------------------------------------------
cases = [
    ("wrong dockerfile path", dict(GOOD, dockerfile="Dockerfile"), "dockerfile"),
    # The build context regression: "" makes Dokploy fall back to the
    # Dockerfile's own directory, and COPY <Project>/<Project>.csproj fails.
    ("empty build context", dict(GOOD, dockerContextPath=""), "dockerContextPath"),
    ("build context set to the dockerfile dir",
     dict(GOOD, dockerContextPath="dockerfile"), "dockerContextPath"),
    ("wrong branch", dict(GOOD, branch="main"), "branch"),
    ("wrong repository", dict(GOOD, repository="other"), "repository"),
    ("non-github source", dict(GOOD, sourceType="docker"), "sourceType"),
    ("previews on for a production app",
     dict(GOOD, isPreviewDeploymentsActive=True), "isPreviewDeploymentsActive"),
    ("entrypoint command missing", dict(GOOD, command=None), "command"),
    ("empty env", dict(GOOD, env="   "), "env"),
]
for label, app, field in cases:
    problems = run(app)
    check(label, any(p.startswith(field) for p in problems), problems)

# args dropped by the API
problems = run(dict(GOOD, args=[]))
check("inline entrypoint args dropped is caught",
      any(p.startswith("args") for p in problems), problems)

# aliases: the thing all service discovery depends on
problems = run(dict(GOOD, networkSwarm=[{"Target": "dokploy-network"}]))
check("missing network aliases are caught",
      any("networkSwarm aliases" in p for p in problems), problems)
check("the alias message explains the consequence",
      any("service discovery" in p for p in problems), problems)

problems = run(dict(GOOD, networkSwarm=[
    {"Target": "dokploy-network", "Aliases": ["mavera-audit"]}]))
check("a partial alias list is caught",
      any("networkSwarm aliases" in p for p in problems), problems)


# --- preview role expects the mirror image -----------------------------------
PREVIEW_GOOD = dict(
    GOOD,
    isPreviewDeploymentsActive=True,
    networkSwarm=[{"Target": "dokploy-network"}],
    previewEnv="APP_DLL=Mavera-Audit.dll",
    env="",
)
check("correct preview host verifies clean",
      run(PREVIEW_GOOD, role="preview") == [], run(PREVIEW_GOOD, role="preview"))

# A preview host carrying production aliases is the hijack bug.
hijack = dict(PREVIEW_GOOD, networkSwarm=[
    {"Target": "dokploy-network",
     "Aliases": ["mavera-audit", "mavera-audit.svc.cluster.local"]}])
problems = run(hijack, role="preview")
check("preview host with production aliases is caught",
      any("networkSwarm aliases" in p for p in problems), problems)

problems = run(dict(PREVIEW_GOOD, previewEnv=""), role="preview")
check("empty previewEnv is caught",
      any(p.startswith("previewEnv") for p in problems), problems)


# --- defensive: partial / unreadable responses -------------------------------
# A field the API does not return cannot be checked. For most fields that is
# tolerable and silently skipped. For the few the design rests on, silence is
# how a broken Application gets deployed -- those must be reported as
# NOT CONFIRMED rather than passing.
sparse = {"applicationId": "app-1", "appName": "x"}
problems = run(sparse)
check("critical fields absent from the response are reported, not passed",
      len(problems) == 4 and all("NOT CONFIRMED" in p for p in problems),
      problems)
for field in ("buildType", "command", "args", "networkSwarm"):
    check(f"{field} absent is reported as NOT CONFIRMED",
          any(p.startswith(field) and "NOT CONFIRMED" in p for p in problems),
          problems)
check("non-critical absent fields stay silent",
      not any(p.startswith(("dockerfile:", "branch:", "repository:",
                            "sourceType:", "env:")) for p in problems),
      problems)
check("the unconfirmed-args message points at the bind fallback",
      any("--entrypoint-mode bind" in p for p in problems), problems)
check("unreadable application is reported",
      run(prov.DokployError("boom"))[0].startswith("could not read"),
      run(prov.DokployError("boom")))
check("empty response is reported", run({}) == ["application.one returned nothing"],
      run({}))


# --- the payload we send must satisfy the verifier ---------------------------
check("herokuVersion default is preserved, not nulled",
      prov.HEROKU_VERSION_DEFAULT == "24", prov.HEROKU_VERSION_DEFAULT)
check("railpackVersion default is preserved, not nulled",
      prov.RAILPACK_VERSION_DEFAULT == "0.15.4", prov.RAILPACK_VERSION_DEFAULT)

for ok, label, detail in results:
    print(f"{'PASS' if ok else 'FAIL':5} {label:52} {'' if ok else detail}")

failures = [r for r in results if not r[0]]
print(f"\n{len(results) - len(failures)}/{len(results)} passed")
sys.exit(1 if failures else 0)
