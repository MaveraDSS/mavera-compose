#!/usr/bin/env python3
"""Create and update the Mavera Dokploy Applications from the generated manifest.

Run generate-manifest.py first. Then:

    export DOKPLOY_URL=https://dokploy.example.com
    export DOKPLOY_API_KEY=...            # Settings -> Profile -> API/CLI
    export DOKPLOY_PROJECT=mavera         # or pass --project

The Dokploy project and environment default to "mavera" / "production". Override
with --project / --environment (or DOKPLOY_PROJECT / DOKPLOY_ENVIRONMENT), or
with --project-id / --environment-id when names are ambiguous. The project must
already exist -- create it in the UI. Run with --list to see what is there.

    python scripts/dokploy/provision.py --list                 # what exists now
    python scripts/dokploy/provision.py                        # dry run, all apps
    python scripts/dokploy/provision.py --only mavera-audit    # dry run, one app
    python scripts/dokploy/provision.py --only mavera-audit --apply
    python scripts/dokploy/provision.py --only mavera-audit --role preview --apply

By default config/entrypoint.sh is sent inline as the container's args, so
nothing has to be placed on the Docker host. Pass --entrypoint-mode bind to
bind-mount it from --entrypoint-host-path instead. See DEPLOY.md Phase 8a.

Two roles, because Dokploy previews inherit the parent Application's network
aliases and would otherwise answer to the production DNS name and steal live
east-west traffic (see README "Why two Applications per repo"):

  production  aliases on dokploy-network, previews OFF, auto-deploy ON
  preview     no aliases, previews ON, auto-deploy OFF, never itself deployed

The script is idempotent: it matches existing Applications by name within the
target environment and updates them rather than creating duplicates. It does
not deploy anything -- deploy from the Dokploy UI once a service looks right.

Dokploy generates each Application's real Swarm service name (appName) with a
random suffix and then refuses to change it, so the generated names are
recorded in build/dokploy/state.json for later log/rollback tooling.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]

NEWLINE = "\n"

# --- how config/entrypoint.sh reaches the container ---------------------------
#
# Every app image's ENTRYPOINT (`dotnet Foo.dll`) has to be replaced with
# "envsubst the appsettings template, then exec dotnet". Dokploy's `command` maps
# to ContainerSpec.Command (a real ENTRYPOINT override) and `args` to
# ContainerSpec.Args, and both are inherited by preview deployments.
#
# "inline" (default) passes the whole script as a single Args element:
#     Command: ["/bin/sh"]   Args: ["-c", "<contents of config/entrypoint.sh>"]
# Dokploy splits `command` on spaces with no quote handling, which is why the
# command itself is the bare "/bin/sh" -- but `args` is a genuine string array,
# so the script survives verbatim, newlines and all. Nothing has to exist on the
# host, which also means nothing to get wrong on a multi-node Swarm.
#
# "bind" is the fallback: the script is placed on the Docker host and
# bind-mounted in. Use it if you would rather keep the script out of the Dokploy
# database, or if inline args ever misbehave. Deliberately NOT under
# /etc/dokploy: that is Dokploy's own data root (host bind mount, 1:1 into the
# container) and it prunes paths inside it -- removeDirectoryCode rm -rf's
# /etc/dokploy/applications/<appName> when an app or preview is torn down.
# Build context for every service: the repo root, because each
# dockerfile/Dockerfile does `COPY <Project>/<Project>.csproj .` relative to it.
DOCKER_CONTEXT_PATH = "."

# Defaults of the matching Dokploy columns (db/schema/application.ts). Only
# relevant to buildpack builders; we send them because saveBuildType requires
# the keys.
HEROKU_VERSION_DEFAULT = "24"
RAILPACK_VERSION_DEFAULT = "0.15.4"

ENTRYPOINT_SOURCE = REPO_ROOT / "config" / "entrypoint.sh"
DEFAULT_ENTRYPOINT_HOST_PATH = "/srv/mavera/entrypoint.sh"
ENTRYPOINT_MOUNT_PATH = "/entrypoint.sh"
# Invoked *through* /bin/sh in bind mode too: Dokploy writes mounted files 0644
# and never chmod +x, so exec'ing the script directly would be permission denied.
BIND_RUN_COMMAND = f"/bin/sh {ENTRYPOINT_MOUNT_PATH}"


def entrypoint_spec(mode: str) -> tuple[str, list[str]]:
    """(command, args) for the chosen mode."""
    if mode == "bind":
        return BIND_RUN_COMMAND, []
    script = ENTRYPOINT_SOURCE.read_text(encoding="utf-8")
    if "APP_DLL" not in script:
        sys.exit(f"{ENTRYPOINT_SOURCE} does not look like the entrypoint script")
    return "/bin/sh", ["-c", script]

# Endpoints this script relies on, checked against the instance's own OpenAPI
# document before anything is written.
REQUIRED_ENDPOINTS = [
    "/application.create",
    "/application.saveGithubProvider",
    "/application.saveBuildType",
    "/application.saveEnvironment",
    "/application.update",
    "/mounts.create",
    "/domain.create",
]


class DokployError(RuntimeError):
    pass


class Dokploy:
    def __init__(self, base_url: str, api_key: str, dry_run: bool) -> None:
        self.base = base_url.rstrip("/") + "/api"
        self.api_key = api_key
        self.dry_run = dry_run
        # Set by check_api(). Dokploy's tRPC input schemas gain required fields
        # between releases -- saveBuildType picked up herokuVersion and
        # railpackVersion, for instance -- so rather than hard-coding a payload
        # per version, fill whatever this instance says it requires.
        self.spec: dict | None = None

    def _request(self, method: str, path: str, payload: dict | None = None) -> Any:
        url = f"{self.base}{path}"
        data = None
        headers = {"x-api-key": self.api_key, "Accept": "application/json"}
        if payload is not None:
            data = json.dumps(payload).encode()
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req) as response:
                body = response.read().decode() or "null"
        except urllib.error.HTTPError as error:
            detail = error.read().decode()[:600]
            raise DokployError(f"{method} {path} -> {error.code}\n{detail}") from None
        except urllib.error.URLError as error:
            raise DokployError(f"{method} {path} -> {error.reason}") from None
        return json.loads(body)

    def get(self, path: str) -> Any:
        return self._request("GET", path)

    def required_fields(self, path: str) -> dict:
        """{field: schema} for fields this endpoint requires, from the spec."""
        if not self.spec:
            return {}
        node = (self.spec.get("paths") or {}).get(path) or {}
        schema = (
            node.get("post", {})
            .get("requestBody", {})
            .get("content", {})
            .get("application/json", {})
            .get("schema", {})
        )
        props = schema.get("properties") or {}
        return {f: props.get(f, {}) for f in (schema.get("required") or [])}

    def field_schemas(self, path: str) -> dict:
        if not self.spec:
            return {}
        node = (self.spec.get("paths") or {}).get(path) or {}
        return (
            node.get("post", {})
            .get("requestBody", {})
            .get("content", {})
            .get("application/json", {})
            .get("schema", {})
            .get("properties")
            or {}
        )

    def validate(self, path: str, payload: dict) -> None:
        """Check the payload against the spec before sending it.

        An enum or type mismatch comes back as an opaque 400 with a zodError
        buried in it, potentially half-way through a 29-service run. Reading the
        schema first turns that into a precise message before anything is sent.
        """
        props = self.field_schemas(path)
        if not props:
            return
        problems = []
        for key, value in payload.items():
            schema = props.get(key)
            if schema is None:
                problems.append(f"{key}: not in this endpoint's schema")
                continue
            variants = schema.get("anyOf") or schema.get("oneOf") or [schema]
            enums = [e for v in variants for e in (v.get("enum") or [])]
            if enums and value not in enums:
                problems.append(f"{key}={value!r} is not one of {enums}")
                continue
            if value is None:
                if not any(v.get("type") == "null" for v in variants):
                    problems.append(f"{key}: null, but the schema does not allow it")
                continue
            types = {v.get("type") for v in variants if v.get("type")}
            if types and not _type_ok(value, types):
                problems.append(
                    f"{key}: sending {type(value).__name__}, schema wants {sorted(types)}"
                )
        if problems:
            sys.exit(
                f"payload for {path} does not match this Dokploy's schema:\n  "
                + "\n  ".join(problems)
                + "\n\nNothing was sent. This usually means Dokploy changed the "
                "endpoint; update provision.py."
            )

    def post(self, path: str, payload: dict) -> Any:
        """Write call. Printed and skipped unless --apply was passed."""
        for field, schema in self.required_fields(path).items():
            if field in payload:
                continue
            payload[field] = schema_default(schema)
            print(f"      (auto-filled {field}={payload[field]!r}: required by "
                  f"this Dokploy, not set by us)")
        self.validate(path, payload)
        if self.dry_run:
            redacted = redact(payload)
            print(f"    POST {path}")
            for key, value in redacted.items():
                print(f"      {key}: {value}")
            return {"dryRun": True}
        return self._request("POST", path, payload)


def _type_ok(value: Any, types: set) -> bool:
    """JSON-schema type check. bool before int, since bool is an int in Python."""
    if isinstance(value, bool):
        return "boolean" in types
    if isinstance(value, str):
        return "string" in types
    if isinstance(value, int) or isinstance(value, float):
        return bool(types & {"number", "integer"})
    if isinstance(value, list):
        return "array" in types
    if isinstance(value, dict):
        return "object" in types
    return True


def schema_default(schema: dict) -> Any:
    """A harmless value of the right shape for a required field we do not set.

    Prefers null wherever the schema allows it, which is what Dokploy's own UI
    sends for the build-type fields that do not apply to the chosen builder.
    """
    variants = schema.get("anyOf") or schema.get("oneOf") or [schema]
    types = {v.get("type") for v in variants}
    if "null" in types:
        return None
    if "string" in types:
        return ""
    if "boolean" in types:
        return False
    if "array" in types:
        return []
    if "integer" in types or "number" in types:
        return 0
    if "object" in types:
        return {}
    return None


SECRET_HINTS = ("pass", "secret", "key", "token", "crypto", "iv")


def redact(payload: dict) -> dict:
    """Shorten and mask a payload for printing. Env blocks are the big one."""
    out = {}
    for key, value in payload.items():
        if key in ("env", "previewEnv") and isinstance(value, str):
            out[key] = f"<{len(value.splitlines())} lines, secrets masked>"
        elif key == "args" and value:
            # The inline entrypoint script is one multi-line element; summarise
            # it rather than dumping 44 lines per service.
            shown = [
                f"<{len(v.splitlines())}-line script>"
                if isinstance(v, str) and len(v.splitlines()) > 1
                else v
                for v in value
            ]
            out[key] = json.dumps(shown)
        elif isinstance(value, str) and any(h in key.lower() for h in SECRET_HINTS):
            out[key] = "***"
        elif isinstance(value, (dict, list)):
            out[key] = json.dumps(value)
        else:
            out[key] = value
    return out


def check_api(client: Dokploy) -> None:
    """Fail early and loudly if this Dokploy does not expose what we need.

    Also caches the spec so post() can fill in required fields this version has
    added. Cheaper than discovering them one 400 at a time, half-way through a
    29-service run.
    """
    try:
        spec = client.get("/settings.getOpenApiDocument")
    except DokployError as error:
        sys.exit(f"could not read the OpenAPI document:\n{error}")

    client.spec = spec
    paths = spec.get("paths") or {}
    missing = [p for p in REQUIRED_ENDPOINTS if p not in paths]
    if missing:
        sys.exit(
            "this Dokploy does not expose: "
            + ", ".join(missing)
            + "\nUpgrade Dokploy, or provision those parts through the UI."
        )

    # networkSwarm and command are the two fields the whole design hangs on.
    update = (
        paths["/application.update"]
        .get("post", {})
        .get("requestBody", {})
        .get("content", {})
        .get("application/json", {})
        .get("schema", {})
        .get("properties", {})
    )
    for field in ("networkSwarm", "command", "args"):
        if field not in update:
            sys.exit(
                f"application.update on this Dokploy has no '{field}' field. "
                "The alias/entrypoint approach in the README will not work as written."
            )
    print("api check: all required endpoints and fields present")


def _match(items: list[dict], wanted: str, key: str) -> list[dict]:
    """Exact match if there is one, else case-insensitive. Dokploy does not
    enforce unique project names, so this can legitimately return several."""
    exact = [i for i in items if i.get(key) == wanted]
    if exact:
        return exact
    return [i for i in items if (i.get(key) or "").lower() == wanted.lower()]


def find_environment(
    client: Dokploy,
    project: str,
    environment: str,
    project_id: str | None = None,
    environment_id: str | None = None,
) -> tuple[str, dict]:
    """Resolve the target environment to (environmentId, {app name: app}).

    Names are how people refer to these, but ids are what is unambiguous --
    pass --project-id / --environment-id to skip the lookup entirely.
    """
    projects = client.get("/project.all")
    if not projects:
        sys.exit(
            "this Dokploy has no projects, or the API key cannot see any. "
            "Create the project in the UI first."
        )

    if project_id:
        candidates = [p for p in projects if p.get("projectId") == project_id]
        if not candidates:
            sys.exit(
                f"no project with projectId {project_id!r}. Available:\n"
                + _project_listing(projects)
            )
    else:
        candidates = _match(projects, project, "name")
        if not candidates:
            sys.exit(
                f"no project named {project!r}. Available:\n"
                + _project_listing(projects)
                + "\n\nPass --project <name> (or --project-id) to pick one."
            )
        if len(candidates) > 1:
            sys.exit(
                f"{len(candidates)} projects are named {project!r}. "
                "Disambiguate with --project-id:\n"
                + _project_listing(candidates)
            )

    found = candidates[0]
    environments = found.get("environments") or []

    if environment_id:
        matches = [e for e in environments if e.get("environmentId") == environment_id]
        if not matches:
            sys.exit(
                f"project {found.get('name')!r} has no environment with "
                f"environmentId {environment_id!r} "
                f"(found: {[e.get('environmentId') for e in environments]})"
            )
    else:
        matches = _match(environments, environment, "name")
        if not matches:
            sys.exit(
                f"project {found.get('name')!r} has no environment "
                f"{environment!r} (found: {[e.get('name') for e in environments]})"
            )
        if len(matches) > 1:
            sys.exit(
                f"project {found.get('name')!r} has {len(matches)} environments "
                f"named {environment!r} - disambiguate with --environment-id: "
                f"{[e.get('environmentId') for e in matches]}"
            )

    env = matches[0]
    env_id = env["environmentId"]
    print(
        f"target: project {found.get('name')!r} ({found.get('projectId')}) / "
        f"environment {env.get('name')!r} ({env_id})"
    )

    # project.all's response shape is not described in Dokploy's OpenAPI
    # document, so do not rely on it nesting applications: ask the endpoint the
    # UI uses for an environment page, and only fall back if that fails. Getting
    # this wrong means the existence check silently misses an application and a
    # re-run creates a duplicate instead of updating.
    existing: dict = {}
    source = "environment.one"
    try:
        detail = client.get(f"/environment.one?environmentId={env_id}")
        existing = {
            app["name"]: app for app in ((detail or {}).get("applications") or [])
        }
    except DokployError as error:
        print(f"  environment.one failed ({error}); falling back to project.all")
        source = "project.all (fallback)"
        existing = {app["name"]: app for app in (env.get("applications") or [])}
    if not existing and (env.get("applications") or []):
        source = "project.all (environment.one returned none)"
        existing = {app["name"]: app for app in env["applications"]}

    if existing:
        print(f"  {len(existing)} existing application(s) via {source}:")
        for name in sorted(existing):
            app = existing[name]
            print(f"    {name:45} {app.get('appName')}")
    else:
        print(f"  no existing applications reported (via {source})")
    return env_id, existing


def _project_listing(projects: list[dict]) -> str:
    rows = []
    for p in projects:
        envs = ", ".join(
            (e.get("name") or "?") for e in (p.get("environments") or [])
        )
        rows.append(f"  {p.get('name')!r:35} {p.get('projectId')}  [{envs}]")
    return "\n".join(rows)


def github_id(client: Dokploy, owner: str) -> str:
    providers = client.get("/github.githubProviders")
    if not providers:
        sys.exit(
            "no GitHub provider is connected to this Dokploy. Connect the GitHub "
            "App first (Settings -> Git Providers); preview deployments only work "
            "for sourceType 'github'."
        )
    for provider in providers:
        if (provider.get("githubUsername") or "").lower() == owner.lower():
            return provider["githubId"]
    # Single provider and no name match: assume it is the org install.
    if len(providers) == 1:
        return providers[0]["githubId"]
    sys.exit(
        f"several GitHub providers connected and none matches owner {owner!r}: "
        f"{[p.get('githubUsername') for p in providers]}"
    )


def network_swarm(aliases: list[str] | None) -> list[dict]:
    """TaskTemplate.Networks, passed to the Docker Engine API verbatim.

    Naming this REPLACES Dokploy's default attachment, so dokploy-network has
    to be listed explicitly or Traefik loses the route.
    """
    entry: dict[str, Any] = {"Target": "dokploy-network"}
    if aliases:
        entry["Aliases"] = aliases
    return [entry]


def health_check_swarm(path: str | None) -> dict | None:
    """Mirror the compose healthcheck. None for the services that have none."""
    if not path:
        return None
    return {
        "Test": ["CMD-SHELL", f"wget -q --spider http://localhost{path} || exit 1"],
        "Interval": 30_000_000_000,
        "Timeout": 5_000_000_000,
        "Retries": 5,
        # .NET cold start plus first-request JIT on a busy VM easily exceeds 90s.
        "StartPeriod": 120_000_000_000,
    }


def provision(
    client: Dokploy,
    spec: dict,
    role: str,
    environment_id: str,
    existing: dict,
    gh_id: str,
    env_block: str,
    preview_limit: int,
    preview_wildcard: str | None,
    domains: dict,
    entrypoint_mode: str,
    entrypoint_host_path: str,
    update_only: bool = False,
) -> dict:
    is_preview = role == "preview"
    name = f"{spec['name']}-pr" if is_preview else spec["name"]
    print(f"\n{name}  ({role})")

    app = existing.get(name)
    if app:
        application_id = app["applicationId"]
        print(f"    exists: appName={app.get('appName')} - updating in place")
    elif update_only:
        sys.exit(
            f"{name} does not exist and --update-only was given. Drop the flag "
            "to create it."
        )
    else:
        created = client.post(
            "/application.create",
            {
                "name": name,
                # Dokploy appends its own random 6-char suffix to this prefix
                # and then never changes it.
                "appName": name,
                "environmentId": environment_id,
                "description": (
                    f"PR preview host for {spec['repository']} - not deployed itself"
                    if is_preview
                    else f"{spec['repository']} ({spec['appDll']})"
                ),
            },
        )
        application_id = created.get("applicationId", "<dry-run>")
        print(f"    created: appName={created.get('appName', '<dry-run>')}")

    client.post(
        "/application.saveGithubProvider",
        {
            "applicationId": application_id,
            "owner": spec["owner"],
            "repository": spec["repository"],
            # A PR only produces a preview when its BASE branch equals this.
            "branch": spec["branch"],
            "buildPath": "/",
            "githubId": gh_id,
            "watchPaths": [],
            "enableSubmodules": False,
            "triggerType": "push",
        },
    )

    client.post(
        "/application.saveBuildType",
        {
            "applicationId": application_id,
            "buildType": "dockerfile",
            "dockerfile": spec["dockerfile"],
            # MUST be "." (the repo root), not "". Dokploy does
            #     getDockerContextPath(app) || <the Dockerfile's own directory>
            # and treats "" as unset, so an empty value silently builds with
            # dockerfile/ as the context -- where the .csproj files are not.
            # This mirrors compose's `context: <repo root>` +
            # `dockerfile: dockerfile/Dockerfile`, which is the combination the
            # 29 Dockerfiles are written against.
            "dockerContextPath": DOCKER_CONTEXT_PATH,
            "dockerBuildStage": "",
            # Required by the endpoint even for buildType=dockerfile, where
            # neither is used. Omitting them fails with "expected nonoptional,
            # received undefined". These are Dokploy's own column defaults --
            # passed back rather than null so switching an app to a buildpack
            # builder later still finds sane values.
            "herokuVersion": HEROKU_VERSION_DEFAULT,
            "railpackVersion": RAILPACK_VERSION_DEFAULT,
        },
    )

    client.post(
        "/application.saveEnvironment",
        {
            "applicationId": application_id,
            # A preview's env REPLACES the parent's rather than merging, so the
            # full block goes into previewEnv as well.
            "env": "" if is_preview else env_block,
            "buildArgs": "",
            "buildSecrets": "",
            "createEnvFile": False,
        },
    )

    command, args = entrypoint_spec(entrypoint_mode)
    update: dict[str, Any] = {
        "applicationId": application_id,
        # ContainerSpec.Command, i.e. the ENTRYPOINT override: envsubst renders
        # appsettings.json, then exec dotnet $APP_DLL. Inherited by previews.
        "command": command,
        "args": args,
        "networkSwarm": network_swarm(None if is_preview else spec["aliases"]),
        "autoDeploy": not is_preview,
        "isPreviewDeploymentsActive": is_preview,
    }
    health = health_check_swarm(spec["healthPath"])
    if health:
        update["healthCheckSwarm"] = health
    if is_preview:
        update["previewEnv"] = env_block
        update["previewLimit"] = preview_limit
        update["previewPort"] = spec["port"]
        update["previewPath"] = "/"
        update["previewHttps"] = bool(preview_wildcard)
        if preview_wildcard:
            update["previewWildcard"] = preview_wildcard
    client.post("/application.update", update)

    if entrypoint_mode == "bind":
        # Bind mount, not file mount: Dokploy builds a file mount's source path
        # from the *preview's* appName while writing the content under the
        # parent's, so a preview would get an empty directory at
        # /entrypoint.sh. Bind mounts take an explicit hostPath and resolve
        # identically for both.
        client.post(
            "/mounts.create",
            {
                "type": "bind",
                "hostPath": entrypoint_host_path,
                "mountPath": ENTRYPOINT_MOUNT_PATH,
                "serviceType": "application",
                "serviceId": application_id,
            },
        )

    if not is_preview and spec["domainEnv"]:
        host = domains.get(spec["domainEnv"])
        if not host:
            print(
                f"    !! {spec['domainEnv']} not set, skipping the public domain. "
                f"Add it in the UI or re-run with {spec['domainEnv']}=..."
            )
        else:
            client.post(
                "/domain.create",
                {
                    "host": host,
                    "path": "/",
                    "port": spec["port"],
                    "https": True,
                    "applicationId": application_id,
                    "domainType": "application",
                    "certificateType": "letsencrypt",
                },
            )
            print(f"    domain: https://{host}")

    return {"name": name, "role": role, "applicationId": application_id}


def verify(
    client: Dokploy,
    application_id: str,
    spec: dict,
    role: str,
    entrypoint_mode: str,
) -> list[str]:
    """Read the application back and confirm the state actually landed.

    Every call in provision() can return 200 while leaving something unset --
    and a half-applied Application fails much later, in a confusing way. The
    canonical example: if saveBuildType does not take, buildType stays at
    Dokploy's default of "nixpacks", which ignores dockerfile/Dockerfile
    entirely, auto-detects the language and picks its own SDK version. That
    surfaces as a build installing the wrong .NET major, nowhere near the cause.
    """
    is_preview = role == "preview"
    try:
        app = client.get(f"/application.one?applicationId={application_id}")
    except DokployError as error:
        return [f"could not read the application back: {error}"]
    if not isinstance(app, dict) or not app:
        return ["application.one returned nothing"]

    problems = []

    def expect(field, wanted, why="", critical=False):
        """Compare one field. `critical` means "unconfirmable is also a problem".

        A field the API does not return cannot be checked. For most fields that
        is tolerable. For the few the whole design rests on it is not: reporting
        success because a field was absent is how a broken Application gets
        deployed. The entrypoint command is the canonical case -- if it does not
        land, the image's own ENTRYPOINT runs, appsettings.json is never
        rendered, and the app reads literal $Placeholder values.
        """
        if field not in app:
            if critical:
                problems.append(
                    f"{field}: NOT CONFIRMED - application.one did not return "
                    f"this field, so it cannot be checked"
                    + (f". {why}" if why else "")
                )
            return
        actual = app.get(field)
        if actual != wanted:
            suffix = f" - {why}" if why else ""
            problems.append(
                f"{field}: expected {wanted!r}, got {actual!r}{suffix}"
            )

    expect("buildType", "dockerfile",
           "nixpacks ignores dockerfile/Dockerfile and picks its own SDK version",
           critical=True)
    expect("dockerfile", spec["dockerfile"])
    expect("dockerContextPath", DOCKER_CONTEXT_PATH,
           "an empty context builds from the Dockerfile's own directory, where "
           "the .csproj files are not")
    expect("repository", spec["repository"])
    expect("branch", spec["branch"])
    expect("sourceType", "github", "previews only run for github sources")
    expect("isPreviewDeploymentsActive", is_preview)

    command, args = entrypoint_spec(entrypoint_mode)
    expect("command", command,
           "without this the image ENTRYPOINT runs, appsettings.json is never "
           "rendered, and the app reads literal $Placeholder values",
           critical=True)
    if entrypoint_mode == "inline":
        if "args" not in app:
            problems.append(
                "args: NOT CONFIRMED - application.one did not return this "
                "field. The inline entrypoint depends on it; use "
                "--entrypoint-mode bind if it turns out not to be stored."
            )
        else:
            actual = app.get("args") or []
            if list(actual) != args:
                problems.append(
                    f"args: expected the entrypoint script ({len(args)} "
                    f"elements), got {len(actual)} element(s) - the inline "
                    "entrypoint will not run; try --entrypoint-mode bind"
                )

    if "networkSwarm" not in app:
        problems.append(
            "networkSwarm: NOT CONFIRMED - application.one did not return this "
            "field, so the DNS aliases all service discovery depends on cannot "
            "be checked"
        )
    if "networkSwarm" in app:
        aliases = []
        for entry in (app.get("networkSwarm") or []):
            aliases.extend(entry.get("Aliases") or [])
        wanted = [] if is_preview else spec["aliases"]
        if sorted(aliases) != sorted(wanted):
            problems.append(
                f"networkSwarm aliases: expected {sorted(wanted)}, "
                f"got {sorted(aliases)}"
                + ("" if is_preview else " - service discovery depends on these")
            )

    env_field = "previewEnv" if is_preview else "env"
    if env_field in app and not (app.get(env_field) or "").strip():
        problems.append(f"{env_field} is empty - the app will see no configuration")

    return problems


# Fields worth seeing when an Application does not behave as provisioned.
INSPECT_FIELDS = [
    "applicationId", "appName", "name", "sourceType", "repository", "owner",
    "branch", "buildType", "dockerfile", "dockerContextPath", "dockerBuildStage",
    "buildPath", "command", "args", "networkSwarm", "networkIds",
    "detachDokployNetwork", "autoDeploy", "isPreviewDeploymentsActive",
    "previewLimit", "previewPort", "applicationStatus",
]


def inspect(client: Dokploy, application_id: str, name: str) -> None:
    """Print what the API actually returns for an Application.

    Exists because "the field came back null" and "the API does not return that
    field" look identical through the verifier, and they need opposite fixes:
    one means the write did not stick, the other means the state may be fine and
    the running container simply predates it.
    """
    print(f"\n{name}  (applicationId={application_id})")
    try:
        app = client.get(f"/application.one?applicationId={application_id}")
    except DokployError as error:
        print(f"    application.one failed: {error}")
        return
    if not isinstance(app, dict):
        print(f"    unexpected response type: {type(app).__name__}")
        return

    for field in INSPECT_FIELDS:
        if field not in app:
            print(f"    {field:28} <NOT RETURNED by this Dokploy>")
            continue
        value = app[field]
        if field == "args" and isinstance(value, list):
            shown = [
                f"<{len(v.splitlines())}-line script>"
                if isinstance(v, str) and len(v.splitlines()) > 1 else v
                for v in value
            ]
            print(f"    {field:28} {shown!r}")
        else:
            print(f"    {field:28} {value!r}")

    # Key names only -- never values. buildArgs in particular matters: build
    # args are recorded in image history, so anything secret landing there is
    # baked into the image for anyone who can pull it.
    for field in ("env", "previewEnv", "buildArgs", "buildSecrets"):
        if field not in app:
            print(f"    {field:28} <NOT RETURNED by this Dokploy>")
            continue
        value = app[field] or ""
        lines = [ln for ln in value.splitlines() if ln.strip()]
        keys = [ln.split("=", 1)[0].strip() for ln in lines if "=" in ln]
        other = [ln for ln in lines if "=" not in ln]
        print(f"    {field:28} <{len(lines)} lines, {len(keys)} key=value>")
        if keys:
            head = ", ".join(keys[:6])
            tail = ", ".join(keys[-3:]) if len(keys) > 9 else ""
            print(f"        keys: {head}"
                  + (f" ... {tail}" if tail else ""))
        if other:
            print(f"        {len(other)} line(s) with no '=': "
                  f"{other[:3]!r}")

    # buildArgs/buildSecrets should be empty for these services. If they carry
    # the environment, say so loudly rather than leaving it in a line count.
    env_keys = set()
    if isinstance(app.get("env"), str):
        env_keys = {ln.split("=", 1)[0].strip()
                    for ln in app["env"].splitlines() if "=" in ln}
    for field in ("buildArgs", "buildSecrets"):
        raw = app.get(field)
        if not isinstance(raw, str) or not raw.strip():
            continue
        keys = {ln.split("=", 1)[0].strip()
                for ln in raw.splitlines() if "=" in ln}
        overlap = keys & env_keys
        print(f"    !! {field} is NOT empty ({len(keys)} keys)"
              + (f", {len(overlap)} of them also in env" if overlap else ""))
        if overlap:
            print(f"    !! build args are recorded in image history - anything "
                  f"secret here is baked into the image. Clear it in the UI "
                  f"(Environment -> Build Args) or re-run provisioning.")

    extra = sorted(set(app) - set(INSPECT_FIELDS)
                   - {"env", "previewEnv", "buildArgs", "buildSecrets"})
    print(f"    ---- {len(app)} fields returned; others: {', '.join(extra) or 'none'}")


# Everything written through application.update, which accepts the call and
# persists nothing on the Dokploy version this was built against. These have to
# be set in the UI; provision.py prints the checklist rather than pretending.
UPDATE_ONLY_FIELDS = [
    "command", "args", "networkSwarm", "autoDeploy",
    "isPreviewDeploymentsActive", "previewEnv", "previewLimit", "previewPort",
    "previewPath", "previewHttps", "previewWildcard", "healthCheckSwarm",
]


def manual_steps(
    specs: list[dict],
    role: str,
    env_dir,
    entrypoint_mode: str,
    entrypoint_host_path: str,
    preview_limit: int,
    preview_wildcard: str | None,
) -> None:
    """Print what still has to be done by hand, and why."""
    print("\n" + "=" * 78)
    print("STILL TO DO IN THE DOKPLOY UI")
    print("=" * 78)
    print(
        "application.update accepts these fields and stores none of them on this\n"
        "Dokploy, so the script cannot set them. Confirm afterwards with --inspect."
    )

    if role != "preview":
        print(
            "\nFor a production Application you would need Run Command and the\n"
            "Swarm network aliases. That is why production runs as the\n"
            "docker-compose.apps.yml stack instead -- see README 'Topology on Dokploy'."
        )
        return

    for spec in specs:
        name = f"{spec['name']}-pr"
        env_file = env_dir / spec["envFile"]
        print(f"\n--- {name} " + "-" * (70 - len(name)))
        print("  1. Preview Deployments: turn it ON")
        print(f"     Limit            {preview_limit}")
        print(f"     Port             {spec['port']}   <- NOT the 3000 default; "
              "the app listens on 80")
        print("     Path             /")
        if preview_wildcard:
            print(f"     Wildcard         {preview_wildcard}")
            print("     HTTPS            on")
        else:
            print("     Wildcard         leave blank to use Dokploy's sslip.io default")
        print("  2. Preview Deployments -> Environment: paste the whole of")
        print(f"     {env_file}")
        print(f"     ({_line_count(env_file)} lines). A preview uses previewEnv "
              "INSTEAD of the")
        print("     Environment tab, not merged with it, so the Environment tab")
        print("     being empty is correct and this paste is not optional.")
        print("  3. Advanced -> Run Command")
        if entrypoint_mode == "bind":
            print(f"     /bin/sh {ENTRYPOINT_MOUNT_PATH}")
            print("  4. Volumes/Mounts -> add a BIND mount")
            print(f"     host       {entrypoint_host_path}")
            print(f"     container  {ENTRYPOINT_MOUNT_PATH}")
            print("     The host file must exist and be a file, not a directory.")
        else:
            print("     /bin/sh          (Command)")
            print("     -c               (first argument)")
            print(f"     ...the whole of config/entrypoint.sh as the second "
                  "argument, which the UI makes painful. Prefer "
                  "--entrypoint-mode bind here.")
        print("  5. Advanced -> Swarm Settings -> Network: leave EMPTY.")
        print("     No aliases is the point: a preview inherits its parent's and")
        print("     would otherwise answer to a production DNS name.")

    print("\nThen verify, and only then open a PR:")
    only = " ".join(f"--only {s['name']}" for s in specs)
    print(f"  python scripts/dokploy/provision.py --role preview {only} --inspect")
    print(f"  python scripts/dokploy/provision.py --role preview {only} --verify-only")
    print("=" * 78)


def _line_count(path) -> int:
    try:
        return len(path.read_text(encoding="utf-8").strip().splitlines())
    except OSError:
        return 0


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default="build/dokploy/manifest.json")
    parser.add_argument(
        "--project",
        default=os.environ.get("DOKPLOY_PROJECT", "mavera"),
        help="Dokploy project name, as shown in the UI. Env: DOKPLOY_PROJECT. "
        "Default: mavera",
    )
    parser.add_argument(
        "--project-id",
        default=os.environ.get("DOKPLOY_PROJECT_ID"),
        help="use instead of --project when names are ambiguous. "
        "Env: DOKPLOY_PROJECT_ID",
    )
    parser.add_argument(
        "--environment",
        default=os.environ.get("DOKPLOY_ENVIRONMENT", "production"),
        help="environment within the project. Env: DOKPLOY_ENVIRONMENT. "
        "Default: production",
    )
    parser.add_argument(
        "--environment-id",
        default=os.environ.get("DOKPLOY_ENVIRONMENT_ID"),
        help="use instead of --environment. Env: DOKPLOY_ENVIRONMENT_ID",
    )
    parser.add_argument(
        "--role",
        choices=["production", "preview"],
        default="production",
        help="production Applications carry the DNS aliases; preview hosts carry "
        "previews. Run once per role.",
    )
    parser.add_argument(
        "--only",
        action="append",
        help="service name, repeatable. Preview hosts should be opted in this way "
        "rather than created for all 29 repos at once.",
    )
    parser.add_argument("--preview-limit", type=int, default=2)
    parser.add_argument(
        "--entrypoint-mode",
        choices=["inline", "bind"],
        default=os.environ.get("MAVERA_ENTRYPOINT_MODE", "inline"),
        help="inline (default) passes config/entrypoint.sh as container args, so "
        "nothing needs to exist on the host. bind expects it at "
        "--entrypoint-host-path on the Docker host.",
    )
    parser.add_argument(
        "--entrypoint-host-path",
        default=os.environ.get(
            "MAVERA_ENTRYPOINT_HOST_PATH", DEFAULT_ENTRYPOINT_HOST_PATH
        ),
        help="absolute path to config/entrypoint.sh ON THE DOCKER HOST "
        f"(default: {DEFAULT_ENTRYPOINT_HOST_PATH})",
    )
    parser.add_argument(
        "--preview-wildcard",
        help="e.g. '*.preview.example.com'. Omit to use Dokploy's sslip.io default.",
    )
    parser.add_argument(
        "--inspect",
        action="store_true",
        help="print the raw fields application.one returns for each selected "
        "service, then exit. Use this when the verifier and the running "
        "container disagree.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="read the applications back and report anything that does not "
        "match the manifest, without writing. Needs no --apply.",
    )
    parser.add_argument(
        "--no-verify",
        action="store_true",
        help="skip the post-apply read-back check (not recommended)",
    )
    parser.add_argument(
        "--update-only",
        action="store_true",
        help="never create; fail if an application is missing. Use this to "
        "safely resume after a run failed part-way through.",
    )
    parser.add_argument("--list", action="store_true", help="show what exists, then exit")
    parser.add_argument(
        "--selftest",
        action="store_true",
        help="print the payloads for every service without contacting Dokploy at "
        "all, so they can be reviewed before touching a real server",
    )
    parser.add_argument("--apply", action="store_true", help="actually write (default: dry run)")
    args = parser.parse_args()

    base_url = os.environ.get("DOKPLOY_URL")
    api_key = os.environ.get("DOKPLOY_API_KEY")
    if not args.selftest and (not base_url or not api_key):
        sys.exit("set DOKPLOY_URL and DOKPLOY_API_KEY")

    manifest_path = REPO_ROOT / args.manifest
    if not manifest_path.exists():
        sys.exit(f"{manifest_path} not found - run generate-manifest.py first")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    env_dir = manifest_path.parent

    client = Dokploy(base_url or "", api_key or "", dry_run=not args.apply)

    if args.selftest:
        if args.apply:
            sys.exit("--selftest never writes; drop --apply")
        environment_id, existing = "<environmentId>", {}
        print(f"SELF TEST - no network calls. Payloads for "
              f"{args.project}/{args.environment}, role {args.role}." + NEWLINE)
    else:
        environment_id, existing = find_environment(
            client, args.project, args.environment,
            args.project_id, args.environment_id,
        )

    if args.list:
        if existing:
            print()
            for name in sorted(existing):
                app = existing[name]
                print(f"  {name:45} appName={app.get('appName')} "
                      f"previews={app.get('isPreviewDeploymentsActive')} "
                      f"buildType={app.get('buildType')}")
        print("\nThis is the same listing the provisioning run uses to decide "
              "create-vs-update.")
        return

    if args.apply:
        check_api(client)
        if args.role == "preview" and args.preview_wildcard:
            print(
                "note: --preview-wildcard is written through application.update, "
                "which does not persist here. Set the wildcard in the UI "
                "(step 1 below)."
            )
    elif not args.selftest:
        print("\nDRY RUN - no writes. Re-run with --apply once the calls look right.\n")

    if args.entrypoint_mode == "bind":
        if not args.entrypoint_host_path.startswith("/"):
            sys.exit(
                "--entrypoint-host-path must be absolute: it is a Docker "
                f"bind-mount source on the host, got {args.entrypoint_host_path!r}"
            )
        print(f"entrypoint: bind mount {args.entrypoint_host_path} -> "
              f"{ENTRYPOINT_MOUNT_PATH} (must exist on the Docker host)")
    else:
        lines = len(ENTRYPOINT_SOURCE.read_text(encoding="utf-8").splitlines())
        print(f"entrypoint: inline, {ENTRYPOINT_SOURCE.name} ({lines} lines) "
              "passed as container args - nothing needed on the host")

    specs = manifest["applications"]
    if args.only:
        wanted = set(args.only)
        specs = [s for s in specs if s["name"] in wanted]
        unknown = wanted - {s["name"] for s in specs}
        if unknown:
            sys.exit(f"not in the manifest: {', '.join(sorted(unknown))}")
    elif args.role == "preview":
        sys.exit(
            "refusing to create preview hosts for all 29 repos at once - each one "
            "costs up to --preview-limit containers plus a .NET SDK build on the "
            "shared VM. Opt in per repo with --only."
        )

    gh_id = "<githubId>" if args.selftest else github_id(client, specs[0]["owner"])
    domains = {
        key: os.environ.get(key) for key in ("GATEWAY_HOST", "IDENTITY_HOST")
    }

    if args.inspect:
        for spec in specs:
            name = f"{spec['name']}-pr" if args.role == "preview" else spec["name"]
            app = existing.get(name)
            if not app:
                print(f"\n{name}  NOT PROVISIONED in this environment")
                continue
            inspect(client, app["applicationId"], name)
        print()
        return

    if args.verify_only:
        problem_count = 0
        for spec in specs:
            name = f"{spec['name']}-pr" if args.role == "preview" else spec["name"]
            app = existing.get(name)
            print(f"\n{name}")
            if not app:
                print("    MISSING - not provisioned in this environment")
                problem_count += 1
                continue
            problems = verify(
                client, app["applicationId"], spec, args.role, args.entrypoint_mode
            )
            if problems:
                problem_count += len(problems)
                for p in problems:
                    print(f"    PROBLEM  {p}")
            else:
                print(f"    ok  appName={app.get('appName')}")
        print()
        if problem_count:
            sys.exit(
                f"{problem_count} problem(s) found. Re-run without --verify-only "
                "and with --apply to fix them."
            )
        print("all verified")
        return

    results = []
    for spec in specs:
        env_block = (env_dir / spec["envFile"]).read_text(encoding="utf-8").strip()
        results.append(
            provision(
                client, spec, args.role, environment_id, existing, gh_id,
                env_block, args.preview_limit, args.preview_wildcard, domains,
                args.entrypoint_mode, args.entrypoint_host_path,
                args.update_only,
            )
        )

    if args.apply:
        state_path = env_dir / "state.json"
        state = {}
        if state_path.exists():
            state = json.loads(state_path.read_text(encoding="utf-8"))
        # Re-read so the recorded appNames are the ones Dokploy actually assigned.
        _, refreshed = find_environment(
            client, args.project, args.environment,
            args.project_id, args.environment_id,
        )
        for result in results:
            app = refreshed.get(result["name"], {})
            state[result["name"]] = {
                "role": result["role"],
                "project": args.project,
                "environment": args.environment,
                "environmentId": environment_id,
                "applicationId": result["applicationId"],
                "appName": app.get("appName"),
            }
        state_path.write_text(
            json.dumps(state, indent=2, sort_keys=True) + "\n",
            encoding="utf-8", newline="\n",
        )
        print(f"\n{len(results)} application(s) written. appNames recorded in {state_path}")

        if args.no_verify:
            print("read-back verification skipped (--no-verify).")
        else:
            print("\nverifying what actually landed:")
            problem_count = 0
            for spec, result in zip(specs, results):
                problems = verify(
                    client, result["applicationId"], spec, args.role,
                    args.entrypoint_mode,
                )
                if problems:
                    problem_count += len(problems)
                    print(f"  {result['name']}")
                    for p in problems:
                        print(f"    PROBLEM  {p}")
            if problem_count:
                print(
                    f"\n{problem_count} problem(s): the API accepted the calls "
                    "but the state is not what the manifest says. Expect the "
                    "application.update fields below to be among them - they are "
                    "the ones the UI has to set."
                )
            print(f"  all {len(results)} verified")

        print("Nothing has been deployed yet - deploy from the Dokploy UI.")
        manual_steps(specs, args.role, env_dir, args.entrypoint_mode,
                     args.entrypoint_host_path, args.preview_limit,
                     args.preview_wildcard)
    else:
        print(f"\n{len(results)} application(s) would be written.")
        manual_steps(specs, args.role, env_dir, args.entrypoint_mode,
                     args.entrypoint_host_path, args.preview_limit,
                     args.preview_wildcard)


if __name__ == "__main__":
    try:
        main()
    except DokployError as error:
        sys.exit("Dokploy API error:" + NEWLINE + str(error))
    except KeyboardInterrupt:
        sys.exit(130)
