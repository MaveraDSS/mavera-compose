# devenv

Run the DSS frontend and the backend services you are working on **on your own machine**, with everything
else (the other 25 services, login, the database) on a shared remote environment, dev02 by default. There is
one database, dev02's; a local copy was considered and dropped (DSS-5587), because a mix of local and remote
services must see the same data.
One command starts it, one stops it. Works the same on Windows and macOS.

```
devenv up                              # frontend + local libertine, everything else on dev02
devenv up --local evaluation-service   # ... plus evaluation-service running from your checkout
devenv status
devenv down
```

Jira: DSS-5583 (parent), DSS-5585 (this tool), DSS-5586 (running services locally), DSS-5587 (guards),
DSS-5590 (rollout).

## How it works

```
 browser ──► frontend :3002 ──► libertine :5151 (local, YARP) ──┬─► evaluation-service :5201 (local, optional)
                                                                 ├─► dev02 internal ingress  (every other service)
                                                                 └─► dev02 public host       (identity, SignalR, ...)
```

- **Libertine is the only backend origin** the frontend talks to. Its committed config only knows
  `/libertine/**` routes; devenv adds *edge routes* so that `/Vera/**`, `/connect/**`, `/.well-known/**`,
  `/ReflectionService/**` and `/identity-client/**` are forwarded to dev02 as well. A service you run
  locally gets its `/Vera/<Service>/**` route and its YARP cluster pointed at `localhost` instead.
- **Every service's `appsettings.json` is a template** with `$Placeholder` tokens, rendered in the cluster
  by envsubst. devenv renders the same templates for "local against dev02": defaults from the fleet
  compose file (`../docker-compose.yml`, `x-placeholders`), overrides per environment
  (`environments/dev02.json`), local infra values (`manifest.json` → `placeholders.local`), and secrets
  from your `secrets.json`. Cluster hostnames (`*.svc.cluster.local`) are re-pointed to dev02's internal
  ingress, to the identity authority, or to `localhost:<port>` for a service that runs locally.
- **Generated files are gitignored in their repos** (`appsettings.Development.json`, `apps/dss/.env`) and
  carry a `_generated` marker. A hand-written file found in that place is copied to `.state/backups/` before
  being overwritten.
- **Docker is optional.** RabbitMQ, Redis and Jaeger start in docker only because local backend services
  need a message broker; frontend plus libertine work with `--no-infra` and no Docker at all.
- **Guards** (DSS-5587), each with a test in `tests/`:
  - *Connectivity*: dev02's discovery document and, with a `--local` service, its SQL Server port must
    answer, otherwise `up` stops with "connect Zscaler".
  - *Broker and cache*: a local service must only ever use the *local* RabbitMQ and Redis (cluster consumers
    would steal real messages); a rendered config pointing anywhere else is refused. A host-installed
    RabbitMQ or Redis sitting on the container ports fails the preflight.
  - *Migrations*: services that migrate at startup are checked against the target database's journal
    (DbUp `SchemaVersions`-style tables, EF Core `__EFMigrationsHistory`, FluentMigrator `VersionInfo`);
    scripts the database has not seen block the start unless you pass `--allow-migrations`.
  - *Mail*: notification-service only starts with `--allow-mail`; its rendered config must have a
    non-production `MailEnvironment` and a `TestingEmailAddress` (your `MAIL_TEST_ADDRESS`), so every
    mail it sends goes to you.
  - *Clone on demand*: repos a run needs but that are missing next to mavera-compose are cloned on the
    manifest branch (or `--branch`).

## Prerequisites

| | Windows | macOS |
|---|---|---|
| .NET SDK 8 or newer | `winget install Microsoft.DotNet.SDK.8` | `brew install dotnet@8` |
| Node.js 24 | `winget install --id OpenJS.NodeJS.LTS --scope user` | `brew install node@24` |
| Git | `winget install Git.Git` | `brew install git` |
| Docker, only for `--local` services | Docker Desktop | Docker Desktop or `brew install colima docker docker-compose` |
| Zscaler connected | required for anything on dev02 | same |

No admin rights are needed for the user-scope installs. After installing, open a **new** terminal and run
`corepack enable` once.

## First-time setup

Three commands, in a folder that will hold the repos side by side (for example `C:\source\repos` or
`~/source/repos`):

```
git clone https://github.com/MaveraDSS/mavera-compose.git
cd mavera-compose/devenv
dotnet run --project src/Devenv -- setup
dotnet run --project src/Devenv -- up -d
```

Open http://localhost:3002 and log in with a dev02 user (Okta email code).

`setup` (DSS-5606) does what used to be four manual steps, and skips whatever is already done:

1. **Repos**: clones verisk-nordics-frontend and mavera-libertine next to mavera-compose on the branch the
   manifest names (libertine: `dss-5583_local-dev-cors` until that change is merged), plus any `--local`
   service. An existing clone is never switched; setup tells you when it is on another branch.
2. **Frontend packages**: `corepack pnpm install --frozen-lockfile`, only when `node_modules` is missing or
   `pnpm-lock.yaml` changed since the last install (a few minutes the first time).
3. **Secrets**: writes `secrets.json` from `secrets.example.json`, taking values from two sources in
   order, and never overwriting a value already in `secrets.json`:
   - `--secrets <file>`: another `secrets.json`, typically the filled one kept as a **Document in the
     k8s-secrets-dev vault** (download it, point setup at it, done). A colleague's copy works the same way.
     Dropping such a file into `devenv/` as `secrets.json` before setup works too.
   - the keyboard: setup prompts for the **required** values only (five for the frontend, libertine's Okta
     secret, the database password), showing where each lives in 1Password; input is hidden.
   Every key in the example file carries its 1Password location in the `op://vault/item/field` notation;
   that is documentation only, devenv never runs the 1Password CLI. Optional values stay as such references
   until someone fills them, and devenv treats a reference as blank.

`dotnet run --project src/Devenv --` is how devenv is run: straight from the checkout, compiled on first use,
about a second afterwards, and a `git pull` is the whole update. `devenv` below stands for that prefix; the
Claude Code skills type the long form for you.

If your repos are not next to mavera-compose, or you are not on dev02, copy `devenv.local.example.json` to
`devenv.local.json` and set `reposRoot` / `environment` there before `setup`.

## Commands

| Command | What it does |
|---|---|
| `setup` | Clones missing repos, installs the frontend packages, fills `secrets.json` (`--secrets <file>` or prompts). Idempotent. `--no-prompt` for scripts. |
| `check` | Tools, repos, packages, secrets file, dev02 reachable (Zscaler), ports free. No changes. |
| `render` | Writes libertine's `appsettings.Development.json`, the frontend `.env`, and the config of every `--local` service. `--dry-run` prints instead. |
| `up` | `check` + `render`, starts docker infra, then the processes; waits until each answers; Ctrl+C stops the processes. `-d` leaves them running in the background and returns. |
| `status` | Each process with pid, port and health (`degraded: ...` when a tolerated check fails, see *Running a backend service locally*); infra containers. `--json` prints one object for tools. |
| `logs <name>` | The end of a process log (`--tail 100`), same on Mac and Windows. Names as in `status`, or `devenv` for the supervisor. |
| `token` | A bearer token for API checks: password grant with `TEST_USER`/`TEST_PASSWORD` from `secrets.json`, through local libertine when it runs. Prints only the token, so `T=$(devenv token)` works. |
| `down` | Stops everything `up` started, including the docker infra. |

Options: `--local a,b` services to run here · `--branch <name>` for repos devenv has to clone ·
`--env dev02` · `--repos <path>` · `--no-infra` · `--allow-migrations` · `--allow-mail` · `--secrets <file>` · `--no-prompt` ·
`--skip-preflight` · `--root <devenv folder>`.

Logs: `.state/logs/<name>.log` per process, `.state/logs/devenv.log` for the background supervisor.

## Running a backend service locally

```
devenv up -d --local evaluation-service
```

What happens in addition to the plain `up`:

1. `mavera-evaluation-service/EvaluationService/appsettings.Development.json` is rendered from that repo's
   committed `appsettings.json` (see *How it works*). Warnings list placeholders that ended up blank.
2. The migration preflight reads dev02's `SchemaVersions` table and compares it with the `_Migrations`
   folder in your checkout. Any script not yet applied stops `up`; rebase onto what dev02 runs, or pass
   `--allow-migrations` if applying it to the shared database is really what you want.
3. RabbitMQ, Redis and Jaeger start in docker; the rendered config points the service at them
   (`localhost`), never at the cluster broker.
4. The service starts with `dotnet run --project <csproj> --no-launch-profile` on its manifest port
   (`ASPNETCORE_URLS`), environment `Development`.
5. Libertine's rendered config routes `/Vera/EvaluationService/**` and the `mavera-evaluation-service`
   cluster to `http://localhost:5201/`; other local services see it there too.

**Apple Silicon note.** evaluation-service, user-service and document-service carry x64-only Service Fabric assemblies (a
leftover from the old hosting), which the arm64 .NET runtime cannot load. devenv starts those services
with the x64 .NET host installed side by side (`/usr/local/share/dotnet/x64/dotnet`; get it from the
"macOS x64" installer on dotnet.microsoft.com, any version 8 or newer, it rolls forward). `check` tells
you when it is missing. Windows x64 machines need nothing extra.

Services that can run locally today (have a `project` in the manifest): evaluation-service, user-service,
document-service, medical-advisor-network, caregivers, integration. notification-service is listed but
blocked (sends real mail) until DSS-5587. Anything else needs its `project` path filled in first.

**Degraded, not broken.** A service's `/health` aggregates checks on its dependencies. When a dependency was
never configured on your machine, the check fails although the service itself works; the manifest lists such
checks under `optionalHealthChecks` with the secrets that switch them on. Today: document-service's `s3` while
the `S3_*` keys in `secrets.json` are blank (DSS-5604). `up` then prints a `warning (document-service): runs
degraded ...` line, `status` shows `HTTP 503, degraded: s3 (...)` with `healthy: true` and the entry names in
`degraded`, and the run counts as ok. Documents cannot be opened or uploaded through that local service until
the keys are filled; everything else works. A failing check that is not optional, or whose secrets are set, still
fails `up` and `status`.

Run the frontend flows that hit the service; the service log is in `.state/logs/<name>.log`.

## Claude Code

The `dss` plugin in this repository (`plugins/dss`, DSS-5588 and DSS-5647) gives Claude Code three commands
that work in every repo once installed:

```
claude plugin marketplace add MaveraDSS/mavera-compose
claude plugin install dss@mavera
```

| Command | What it does |
|---|---|
| `/dss:dev-env DSS-1234` | Reads the ticket, proposes the services to run locally with a reason each, and after your yes runs `devenv up -d --local ...`. |
| `/dss:verify DSS-1234` | Checks the stack against the ticket's "how to test" (starting it first, after your yes, when it is down): API calls through libertine with `devenv token`, and Claude driving Chrome (you log in once). Evidence lands in `.state/evidence/<ticket>/`. |
| `/dss:ticket DSS-1234` | The whole loop: understand, start the stack, branch per repo convention, plan, implement after your yes, unit tests, restart the service, verify, commit. No push or PR unless asked. |

Open Claude Code in the **repos root** (the folder holding mavera-compose and the other repos side by side),
not inside one repo: every repo is in reach and the skills find devenv at `./mavera-compose/devenv`. `setup`
writes a short `CLAUDE.md` into that folder for this purpose (never overwriting yours). The skills use
`devenv status --json`, `devenv logs` and `devenv token`, and read each repo's own `CLAUDE.md` before
touching it, since Claude Code does not load those from the parent folder. Automated checks write only inside the test organisation named
in `environments/<env>.json` (`testing`). The Jira connection (Atlassian MCP) is a one-time login per developer.

## Adding a service to the manifest (checklist)

1. `manifest.json` → `services[]`: `name` (kebab-case), `repo` (GitHub repo name; it is also the cluster
   hostname the other templates use), `branch` if not `develop` (used when devenv clones it), `project`
   (web `.csproj`, relative to the repo), a free `port` (52xx), `healthPath` (from `MapHealthChecks` in
   Program.cs; omit for a port check).
2. Libertine wiring: `clusterIds` (the YARP cluster ids in libertine's `appsettings.json` that point at
   it), `serviceSettingsNames` (its entries in libertine's `ServiceSettings.Services`), `publicPrefix`
   if the frontend calls it directly (`Vera/<Service>`), `remotePath` if another template names it by
   bare hostname.
3. Migrations: `migrations: "dbup"` with `migrationsFolder`, `migrationsJournal` (the table name in
   `JournalToSqlTable`, default `SchemaVersions`) and `connectionStringName`; or `"efcore"` (folder
   `Migrations`, journal `__EFMigrationsHistory`); or `"fluentmigrator"` (folder `Migrations`, journal
   `VersionInfo`, versions read from the `[Migration(NNN)]` attributes).
4. Flags: `usesMessageBroker` (registers RabbitMQ consumers), `sendsMail`, `requiresX64` (x64-only
   assemblies in the build output; check with the PE headers if a service crashes with
   `ReflectionTypeLoadException` on a Mac).
5. New `$Placeholders` in its template: add environment values to `environments/<env>.json`, local infra
   values to `placeholders.local`, secrets to `placeholders.secrets` and `secrets.example.json`. Anything
   not in the fleet compose defaults either. `devenv render --local <name> --dry-run` refuses until every
   token has a value.
6. New cluster hostnames that are not a service repo (like the translation service) go to
   `placeholders.hosts`.
7. Run `dotnet test` in `devenv/`: the manifest tests check ports, cluster claims and that every libertine
   cluster is accounted for.

## Layout

```
devenv/
  manifest.json              frontend, gateway, services, placeholder rules   (committed)
  environments/dev02.json    hosts, database, Okta server, per-env placeholders (committed, no secrets)
  secrets.example.json       every secret key with its op:// 1Password reference  (committed)
  secrets.json               your values, written by `setup`                      (gitignored)
  devenv.local.json          reposRoot, environment, developerName, localServices (gitignored, optional)
  infra/docker-compose.yml   rabbitmq, redis, jaeger
  src/Devenv/                the CLI (.NET 8 console app, packable as a dotnet tool)
  tests/Devenv.Tests/        xunit: manifest validation, placeholder table, both renderers
  .state/                    pids, logs, backups                                  (gitignored)
```

## Troubleshooting

- `remote env FAIL ... Is Zscaler connected?` — connect Zscaler; nothing on dev02 answers without it.
- `port 5151 already in use` — something is still running: `devenv status`, then `devenv down`, or a
  libertine started by hand from an IDE.
- `docker FAIL` — Docker Desktop (or Colima) is not running. Start it, or `--no-infra` when you have no
  `--local` service.
- A value in `secrets.json` still reads `op://...` — nobody has filled it yet; the text is where it lives in
  1Password (vault/item/field). Paste the value in its place, or run `setup --secrets <file>` with a filled
  copy; devenv treats such a value as blank.
- Login accepts the email code and then fails — `OKTA_CLIENT_SECRET` in `secrets.json` is wrong or blank;
  the real error is in `.state/logs/frontend.log`. Restart after fixing (`down`, `up`).
- Libertine's first proxied request after start can time out once; devenv retries, browsers just reload.
- `document-service ... HTTP 503, degraded: s3 (...)` — expected while the `S3_*` keys in `secrets.json` are
  blank; the service serves everything except document content. Fill the keys (DSS-5604) and restart to clear
  it. Each health probe takes up to 30 s in this state because the AWS SDK looks for credentials first, so
  `status` is slow for that one process.
- `refusing to start ...: it would change the shared dev02 database schema` — your branch carries
  migration scripts dev02 has not applied. Rebase, or decide consciously with `--allow-migrations`.
- Two `403` responses from `/Vera/MedicalAdvisorNetwork/...` on the case page are a dev02 build issue,
  also visible on the Vercel deployment; not caused by the local setup.
