# Running devenv from a skill

devenv is the .NET console app in `mavera-compose/devenv`. It is not installed as a tool; run it with
`dotnet run --project`, from any working directory, always passing `--root` so it finds its files:

```
dotnet run --project <DEVENV>/src/Devenv -- <command> [options] --root <DEVENV>
```

## Finding `<DEVENV>`

In this order, stop at the first that contains `manifest.json`:

1. `$DEVENV_ROOT` when set.
2. `<cwd>/mavera-compose/devenv`: the session was opened in the **repos root**, the recommended way to work
   (every repo is visible, no directory switching).
3. `<git toplevel of the current directory>/devenv`: the session was opened inside mavera-compose itself
   (its `CLAUDE.md` is then loaded automatically; the other repos are one level up, `<toplevel>/..`).
4. `<git toplevel of the current directory>/../mavera-compose/devenv`: the session was opened inside another repo.
5. `~/source/repos/mavera-compose/devenv`, then `C:\source\repos\mavera-compose\devenv` on Windows.
6. Ask the developer where mavera-compose is cloned. Never clone it yourself.

Any other folder works too when `DEVENV_ROOT` is set (for example in the shell profile).

The repos root (parent of mavera-compose) is where devenv expects every other repo, unless
`<DEVENV>/devenv.local.json` sets `reposRoot`. `devenv setup` writes a short `CLAUDE.md` into the repos root
(when none exists) that names the repos and points at these skills.

## Commands the skills use

| Purpose | Command | Notes |
|---|---|---|
| What runs | `status --json` | One object: `processes[]` with `alive`/`healthy`/`degraded[]`/`detail`, `infra`, `localServices[]` with `branch`, `testing` (test organisation), `ok`. Exit 1 when nothing runs or something is unhealthy. A process with `healthy: true` and a non-empty `degraded` (today: document-service `s3` while the S3 secrets are blank) is usable; `detail` says what does not work. Mention it, do not treat it as a failure. |
| Start | `up -d --local a,b` | Clones missing repos, preflight, render, guards, docker infra, processes, health waits. Returns when everything answers, or exits 1 with the failing line. `--no-infra` when no `--local`. |
| Stop | `down` | Also stops the docker infra. |
| Restart one changed service | `down` then `up -d --local ...` | Services are `dotnet run`, no hot reload. The frontend hot-reloads on its own. |
| Log tail | `logs <name> --tail 200` | Names as in status: `frontend`, `libertine`, `<service-name>`, `devenv` (supervisor). |
| Token | `token` | Prints only the bearer token on stdout; stderr says user, origin and expiry. Needs `TEST_USER`/`TEST_PASSWORD` in `secrets.json`. |
| Preflight only | `check` | Zscaler, ports, tools, secrets. |
| Rendered config | `render --local <name> --dry-run` | Shows the appsettings a service would get. |

## Reading the output of `up`

Order: `preflight` (lines `ok`/`FAIL`), `render` (files written, warnings), `migrations:` lines for services that
migrate, `infra`, `start`, health lines per process, then `ready: ...`. Failures are `error: ...` on stderr with
the fix in the message. Typical ones and what to tell the developer:

- `remote env FAIL ... Is Zscaler connected?`: connect Zscaler, then retry.
- `port 5151 already in use`: `status`, then `down`; or a libertine started from an IDE.
- `mail guard: ...`: notification-service needs `--allow-mail` and `MAIL_TEST_ADDRESS`; ask before adding it.
- `refusing to start ...: it would change the shared dev02 database schema`: the checkout has migrations dev02
  has not applied. Do not add `--allow-migrations` on your own; explain and ask.
- `secrets missing or blank`: name the key and where it lives (`secrets.example.json`); never ask for the value in chat.

## Ports and URLs

Frontend `http://localhost:3002`, libertine `http://localhost:5151` (the only backend origin the frontend
uses; `/Vera/**`, `/libertine/**`, `/connect/**` go through it), local services `http://localhost:52xx` per
`manifest.json`, RabbitMQ UI `http://localhost:15672`, Jaeger `http://localhost:16686`.

## Rules

- Never start, stop or restart the stack without telling the developer first; `up` and `down` take minutes and
  interrupt whatever they were doing.
- Never pass `--allow-migrations`, `--allow-mail` or `--skip-preflight` unless the developer asked for it.
- Never print secret values, tokens included, into chat. `devenv token` output goes into a shell variable.
