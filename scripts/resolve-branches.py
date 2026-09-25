#!/usr/bin/env python3
"""Pick BRANCH or BRANCH_FALLBACK for every service repo in a compose file.

Each build context in docker-compose.apps.yml reads

    MaveraDSS/<repo>.git#${BRANCH_<NAME>:-${BRANCH:-develop}}

so it builds BRANCH unless BRANCH_<NAME> overrides it. Compose cannot tell
whether a ref exists, and BuildKit just fails when one does not. This script
asks GitHub instead: for every repo that lacks BRANCH it emits
BRANCH_<NAME>=<BRANCH_FALLBACK>, and nothing for the repos that have it.

    python scripts/resolve-branches.py                  # print the block
    python scripts/resolve-branches.py --write .env     # update .env in place
    BRANCH=my-feature python scripts/resolve-branches.py

BRANCH, BRANCH_FALLBACK and GH_PAT are read from the environment first, then
from --env-file (default .env, if it exists). With GH_PAT unset, git uses
whatever credentials it already has for github.com.

On Dokploy nothing runs before the build, so run this wherever you have repo
access, then paste the printed block into the compose service's Environment
tab (replacing the previous block) and redeploy.

Exit status: 0 if every repo resolved, 1 if a repo has neither branch (those
are listed; the deploy would fail on them), 2 on usage or access errors.
"""

from __future__ import annotations

import argparse
import base64
import os
import re
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]

BEGIN = "# >>> resolve-branches.py -- generated, do not edit by hand"
END = "# <<< resolve-branches.py"

# Matches live and commented-out contexts alike, so the extras are covered too.
CONTEXT = re.compile(r"MaveraDSS/([A-Za-z0-9._-]+)\.git#\$\{(BRANCH_[A-Z0-9_]+):-")

SHA = re.compile(r"^[0-9a-f]{7,40}$")


def read_env_file(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.is_file():
        return values
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip("'\"")
    return values


def repos_in(compose: Path) -> dict[str, str]:
    """{repo: override variable}, in file order."""
    found: dict[str, str] = {}
    for repo, var in CONTEXT.findall(compose.read_text(encoding="utf-8")):
        found.setdefault(repo, var)
    return found


def git_env(token: str) -> dict[str, str]:
    # The token goes in through GIT_CONFIG_* rather than the URL or argv, so it
    # never shows up in the process list or in an error message.
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    if token:
        basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
        env.update(
            GIT_CONFIG_COUNT="1",
            GIT_CONFIG_KEY_0="http.https://github.com/.extraHeader",
            GIT_CONFIG_VALUE_0=f"Authorization: Basic {basic}",
        )
    return env


def has_ref(repo: str, ref: str, env: dict[str, str]) -> bool:
    """True if ref is a branch or tag of MaveraDSS/<repo>. Raises on access errors."""
    if SHA.match(ref):
        return True  # a commit; BuildKit fetches it, ls-remote cannot confirm it
    result = subprocess.run(
        ["git", "ls-remote", f"https://github.com/MaveraDSS/{repo}.git",
         f"refs/heads/{ref}", f"refs/tags/{ref}"],
        capture_output=True, text=True, env=env, timeout=60,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip().splitlines()[-1] if result.stderr.strip()
                           else f"git ls-remote exited {result.returncode}")
    return bool(result.stdout.strip())


def render(overrides: dict[str, str], branch: str, fallback: str) -> str:
    lines = [BEGIN,
             f"# BRANCH={branch}  BRANCH_FALLBACK={fallback}",
             "# Repos without BRANCH build BRANCH_FALLBACK instead. Every other repo",
             "# builds BRANCH. Re-run after changing either one."]
    lines += [f"{var}={ref}" for var, ref in overrides.items()]
    if not overrides:
        lines.append("# (every repo has BRANCH -- no overrides)")
    lines.append(END)
    return "\n".join(lines) + "\n"


def write_block(path: Path, block: str) -> None:
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    pattern = re.compile(re.escape(BEGIN) + r".*?" + re.escape(END) + r"\n?", re.S)
    if pattern.search(text):
        text = pattern.sub(lambda _: block, text)
    else:
        text = text + ("" if text.endswith("\n") or not text else "\n") + "\n" + block
    path.write_text(text, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("-f", "--file", default=str(REPO_ROOT / "docker-compose.apps.yml"),
                        help="compose file to read build contexts from")
    parser.add_argument("--env-file", default=".env",
                        help="read BRANCH / BRANCH_FALLBACK / GH_PAT from here (default .env)")
    parser.add_argument("--write", metavar="PATH",
                        help="replace the generated block in PATH (e.g. .env) instead of printing")
    args = parser.parse_args()

    file_env = read_env_file(Path(args.env_file))
    get = lambda key, default="": os.environ.get(key) or file_env.get(key) or default
    branch, fallback, token = get("BRANCH", "develop"), get("BRANCH_FALLBACK", "develop"), get("GH_PAT")

    compose = Path(args.file)
    repos = repos_in(compose)
    if not repos:
        print(f"no MaveraDSS build contexts with a BRANCH_<NAME> override in {compose}",
              file=sys.stderr)
        return 2

    env = git_env(token)

    def probe(repo: str) -> tuple[str, bool | Exception, bool | Exception | None]:
        try:
            on_branch = has_ref(repo, branch, env)
        except Exception as error:  # noqa: BLE001 -- reported per repo below
            return repo, error, None
        if on_branch or fallback == branch:
            return repo, on_branch, None
        try:
            return repo, False, has_ref(repo, fallback, env)
        except Exception as error:  # noqa: BLE001
            return repo, False, error

    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(probe, repos))

    overrides: dict[str, str] = {}
    missing, errors = [], []
    for repo, on_branch, on_fallback in results:
        if isinstance(on_branch, Exception) or isinstance(on_fallback, Exception):
            errors.append(f"  {repo}: {on_branch if isinstance(on_branch, Exception) else on_fallback}")
        elif on_branch:
            print(f"  {repo:40} {branch}", file=sys.stderr)
        elif on_fallback:
            overrides[repos[repo]] = fallback
            print(f"  {repo:40} {fallback}  (no {branch})", file=sys.stderr)
        else:
            missing.append(repo)
            print(f"  {repo:40} MISSING  (neither {branch} nor {fallback})", file=sys.stderr)

    if errors:
        print("could not query these repos (check GH_PAT / git credentials):\n"
              + "\n".join(errors), file=sys.stderr)
        return 2

    block = render(overrides, branch, fallback)
    if args.write:
        write_block(Path(args.write), block)
        print(f"wrote {len(overrides)} override(s) to {args.write}", file=sys.stderr)
    else:
        print(block, end="")

    if missing:
        print(f"{len(missing)} repo(s) have neither branch; the build will fail on them: "
              + ", ".join(missing), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
