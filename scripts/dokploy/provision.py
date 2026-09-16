#!/usr/bin/env python3
"""Create and update the Mavera Dokploy Applications from the generated manifest.

Run generate-manifest.py first. Then:

    export DOKPLOY_URL=https://dokploy.example.com
    export DOKPLOY_API_KEY=...            # Settings -> Profile -> API/CLI

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
# /etc/dokploy -- that is Dokploy's own directory, its location has moved
# between versions, and Dokploy prunes paths inside it.
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

    def post(self, path: str, payload: dict) -> Any:
        """Write call. Printed and skipped unless --apply was passed."""
        if self.dry_run:
            redacted = redact(payload)
            print(f"    POST {path}")
            for key, value in redacted.items():
                print(f"      {key}: {value}")
            return {"dryRun": True}
        return self._request("POST", path, payload)


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
    """Fail early and loudly if this Dokploy does not expose what we need."""
    try:
        spec = client.get("/settings.getOpenApiDocument")
    except DokployError as error:
        sys.exit(f"could not read the OpenAPI document:\n{error}")

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
    for field in ("networkSwarm", "command"):
        if field not in update:
            sys.exit(
                f"application.update on this Dokploy has no '{field}' field. "
                "The alias/entrypoint approach in the README will not work as written."
            )
    print("api check: all required endpoints and fields present")


def find_environment(client: Dokploy, project: str, environment: str) -> tuple[str, dict]:
    """Return (environmentId, {application name: application}) for the target."""
    projects = client.get("/project.all")
    for candidate in projects:
        if candidate.get("name") != project:
            continue
        for env in candidate.get("environments") or []:
            if env.get("name") != environment:
                continue
            existing = {
                app["name"]: app for app in (env.get("applications") or [])
            }
            return env["environmentId"], existing
        sys.exit(
            f"project {project!r} has no environment {environment!r} "
            f"(found: {[e.get('name') for e in candidate.get('environments') or []]})"
        )
    sys.exit(
        f"no project named {project!r} "
        f"(found: {[c.get('name') for c in projects]})"
    )


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
) -> dict:
    is_preview = role == "preview"
    name = f"{spec['name']}-pr" if is_preview else spec["name"]
    print(f"\n{name}  ({role})")

    app = existing.get(name)
    if app:
        application_id = app["applicationId"]
        print(f"    exists: appName={app.get('appName')} - updating in place")
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
            "dockerContextPath": "",
            "dockerBuildStage": "",
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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default="build/dokploy/manifest.json")
    parser.add_argument("--project", default="mavera")
    parser.add_argument("--environment", default="production")
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
            client, args.project, args.environment
        )
        print(f"{args.project}/{args.environment}: environmentId={environment_id}, "
              f"{len(existing)} existing application(s)")

    if args.list:
        for name in sorted(existing):
            app = existing[name]
            print(f"  {name:45} appName={app.get('appName')} "
                  f"previews={app.get('isPreviewDeploymentsActive')}")
        return

    if args.apply:
        check_api(client)
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

    results = []
    for spec in specs:
        env_block = (env_dir / spec["envFile"]).read_text(encoding="utf-8").strip()
        results.append(
            provision(
                client, spec, args.role, environment_id, existing, gh_id,
                env_block, args.preview_limit, args.preview_wildcard, domains,
                args.entrypoint_mode, args.entrypoint_host_path,
            )
        )

    if args.apply:
        state_path = env_dir / "state.json"
        state = {}
        if state_path.exists():
            state = json.loads(state_path.read_text(encoding="utf-8"))
        # Re-read so the recorded appNames are the ones Dokploy actually assigned.
        _, refreshed = find_environment(client, args.project, args.environment)
        for result in results:
            app = refreshed.get(result["name"], {})
            state[result["name"]] = {
                "role": result["role"],
                "applicationId": result["applicationId"],
                "appName": app.get("appName"),
            }
        state_path.write_text(
            json.dumps(state, indent=2, sort_keys=True) + "\n",
            encoding="utf-8", newline="\n",
        )
        print(f"\n{len(results)} application(s) written. appNames recorded in {state_path}")
        print("Nothing has been deployed yet - deploy from the Dokploy UI.")
    else:
        print(f"\n{len(results)} application(s) would be written.")


if __name__ == "__main__":
    try:
        main()
    except DokployError as error:
        sys.exit("Dokploy API error:" + NEWLINE + str(error))
    except KeyboardInterrupt:
        sys.exit(130)
