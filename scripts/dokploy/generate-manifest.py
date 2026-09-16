#!/usr/bin/env python3
"""Derive the Dokploy provisioning manifest from docker-compose.yml.

docker-compose.yml stays the single source of truth. This script shells out to
`docker compose config`, which does the ${VAR} interpolation and the
x-placeholders anchor merge for us, and then writes out, per application
service:

    build/dokploy/manifest.json      what to create in Dokploy
    build/dokploy/env/<service>.env  its fully-resolved environment block

Nothing is hand-maintained, so the Dokploy environment cannot drift from the
compose file the way a second copy of the placeholder list would.

Usage:
    python scripts/dokploy/generate-manifest.py [--env-file .env] [--out build/dokploy]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# The two services Traefik routes from outside. Everything else is internal.
PUBLIC_DOMAINS = {
    "mavera-libertine": "GATEWAY_HOST",
    "mavera-identity-server": "IDENTITY_HOST",
}

# github.com/<owner>/<repo>.git#<branch>, out of the compose build context URL.
BUILD_CONTEXT_RE = re.compile(
    r"github\.com/(?P<owner>[^/]+)/(?P<repo>[^/.]+)\.git#(?P<branch>.+)$"
)

# wget -q --spider http://localhost/<path> || exit 1
HEALTHCHECK_RE = re.compile(r"http://localhost/(?P<path>[^\s|]*)")


def compose_config(env_file: str) -> dict:
    """Fully resolved compose config, all profiles included."""
    cmd = [
        "docker", "compose",
        "--profile", "all",
        "--env-file", env_file,
        "config", "--format", "json",
    ]
    proc = subprocess.run(
        cmd, cwd=REPO_ROOT, capture_output=True, text=True, encoding="utf-8"
    )
    if proc.returncode != 0:
        sys.exit(
            "docker compose config failed. Every ${VAR:?} must be satisfied by "
            f"{env_file} before this will run.\n\n{proc.stderr}"
        )
    return json.loads(proc.stdout)


def health_path(service: dict) -> str | None:
    """The app's health endpoint, or None for the services that have none."""
    test = (service.get("healthcheck") or {}).get("test") or []
    if isinstance(test, str):
        test = [test]
    match = HEALTHCHECK_RE.search(" ".join(test))
    return "/" + match.group("path") if match else None


def describe(name: str, service: dict, env: dict) -> dict:
    context = (service.get("build") or {}).get("context", "")
    match = BUILD_CONTEXT_RE.search(context)
    if not match:
        sys.exit(
            f"{name}: could not read owner/repo/branch out of its build context. "
            "Only git-URL contexts are supported here."
        )

    return {
        "name": name,
        "owner": match.group("owner"),
        "repository": match.group("repo"),
        "branch": match.group("branch"),
        "dockerfile": (service.get("build") or {}).get(
            "dockerfile", "dockerfile/Dockerfile"
        ),
        "appDll": env["APP_DLL"],
        "port": int(env.get("ASPNETCORE_HTTP_PORTS", 80)),
        # Both forms have to resolve: appsettings.json templates append
        # .svc.cluster.local to $ServiceSettings_Services_*, while
        # InternalServices_Authority uses the bare name. Dokploy's own appName
        # carries a random suffix, so these aliases are the only stable names.
        "aliases": [name, f"{name}.svc.cluster.local"],
        "healthPath": health_path(service),
        "domainEnv": PUBLIC_DOMAINS.get(name),
        "envFile": f"env/{name}.env",
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--env-file", default=".env")
    parser.add_argument("--out", default="build/dokploy")
    args = parser.parse_args()

    config = compose_config(args.env_file)
    services = config["services"]

    out_dir = REPO_ROOT / args.out
    env_dir = out_dir / "env"
    env_dir.mkdir(parents=True, exist_ok=True)

    apps = []
    for name in sorted(services):
        service = services[name]
        env = service.get("environment") or {}
        # APP_DLL is what distinguishes an application service from
        # infrastructure: x-service-base gives every app exactly one own key.
        if "APP_DLL" not in env:
            continue

        apps.append(describe(name, service, env))

        lines = [f"{key}={env[key]}" for key in sorted(env)]
        (env_dir / f"{name}.env").write_text(
            "\n".join(lines) + "\n", encoding="utf-8", newline="\n"
        )

    manifest = {
        "generatedFrom": "docker-compose.yml",
        "envFile": args.env_file,
        "gatewayHost": os.environ.get("GATEWAY_HOST")
        or services["mavera-libertine"]["environment"]["ServiceSettings_EnvURL"],
        "applications": apps,
    }
    (out_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )

    infra = sorted(set(services) - {a["name"] for a in apps})
    key_count = len(services[apps[0]["name"]]["environment"])
    print(f"{len(apps)} application services -> {out_dir / 'manifest.json'}")
    print(f"{len(apps)} env blocks ({key_count} keys each) -> {env_dir}")
    print(f"{len(infra)} infrastructure services left to docker-compose.infra.yml: {', '.join(infra)}")
    missing = [a["name"] for a in apps if not a["healthPath"]]
    if missing:
        print(f"\nno health endpoint (deliberate, matches compose): {', '.join(missing)}")


if __name__ == "__main__":
    main()
