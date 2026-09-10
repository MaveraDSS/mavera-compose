# devenv

Run the DSS frontend and the services you are working on **on your own machine**, with everything else
(other services, login, database, SignalR) on a remote environment such as dev02. One command, same steps
on macOS and Windows. Ticket: DSS-5583, this folder is DSS-5585.

How it works: local **libertine** is the only origin the frontend talks to. Its committed
`appsettings.json` is an envsubst template with 25 YARP clusters; `devenv` renders a gitignored
`appsettings.Development.json` from it that points every cluster at the remote environment (or at
`localhost:<port>` for a service you run here) and adds the "edge" routes for the paths the frontend calls
outside `/libertine` (`Vera/**`, `connect/**`, `.well-known/**`, `ReflectionService/**`,
`identity-client/**`). No Caddy, no nginx, no extra proxy.

```
browser ── http://localhost:3002 (Next.js dev server)
             │  NEXT_PUBLIC_VERA_BASE_PATH=http://localhost:5151
             ▼
           libertine (dotnet run, :5151, Development config rendered by devenv)
             ├── /libertine/**  ──► remote internal ingress (or localhost for a --local service)
             ├── /Vera/**, /connect/**, /.well-known/**, /ReflectionService/**, /identity-client/** ──► remote public host
             └── /Vera/<Service>/** ──► localhost:<port> when that service is --local
           docker: rabbitmq :5672, redis :6379, jaeger (OTLP :4317, UI :16686)  ← for local services only
```

## Requirements

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8 or newer | `dotnet --version`. Windows: `winget install Microsoft.DotNet.SDK.8` |
| Node.js | 24 (LTS) | brings `corepack`. Windows: `winget install --id OpenJS.NodeJS.LTS --scope user` |
| Git | any | |
| Docker | any recent | Docker Desktop / Colima. Only needed for `--local` services; `--no-infra` skips it |
| Zscaler | connected | the remote environment is not reachable without it |

Clone the repos **side by side**, e.g. `C:\source\repos\` or `~/source/repos/`:

```
repos/
  mavera-compose/          this repo
  verisk-nordics-frontend/ run `corepack pnpm install --frozen-lockfile` once
  mavera-libertine/        branch dss-5583_local-dev-cors until it is merged to develop
  mavera-<service>/        only the ones you work on
```

## First time

1. `cd mavera-compose/devenv`
2. Copy `secrets.example.json` to `secrets.json` and fill it in. Every key says where its value lives in
   1Password (vault `k8s-secrets-dev`). `secrets.json` is gitignored.
3. Optional: copy `devenv.local.example.json` to `devenv.local.json` if your repos are not next to
   mavera-compose or you want a different environment name.
4. `dotnet run --project src/Devenv -- check`

```
devenv check  (environment dev02, repos in /Users/you/source/repos)
  ok    dotnet               8.0.420
  ok    node                 v24.16.0
  ok    corepack             0.35.0
  ok    docker               29.5.2
  ok    frontend repo        /Users/you/source/repos/verisk-nordics-frontend
  ok    libertine repo       /Users/you/source/repos/mavera-libertine
  ok    frontend packages    node_modules present
  ok    secrets              /Users/you/source/repos/mavera-compose/devenv/secrets.json
  ok    remote env           dev02 reachable
  ok    frontend port        3002 free
  ok    libertine port       5151 free
all checks passed
```

Fix any `FAIL` line; the message says what to do.

## Every day

```
dotnet run --project src/Devenv -- up          # foreground: prefixed logs, Ctrl+C stops the processes
dotnet run --project src/Devenv -- up -d       # background: returns when everything answers
dotnet run --project src/Devenv -- status
dotnet run --project src/Devenv -- down        # stops processes and the docker infra
```

`up` runs the checks, renders the two config files, starts the docker infra, then libertine and the
frontend, and waits until both answer (libertine's first proxied request is retried once; YARP warms up).
Then open http://localhost:3002 and log in with a dev02 user (Okta email code). Logs of background
processes are in `.state/logs/`.

Tip: `dotnet run --project src/Devenv --` is long. Once the tool is packed and installed
(`dotnet pack src/Devenv` then `dotnet tool install -g MaveraDSS.Devenv --add-source src/Devenv/nupkg`)
it is just `devenv up`. Until then a shell alias works.

## Running a service locally

```
dotnet run --project src/Devenv -- render --local evaluation-service
```

renders libertine so that `/Vera/EvaluationService/**` and the `mavera-evaluation-service` cluster go to
`http://localhost:5201/` instead of dev02. You start the service yourself for now; **starting it from
devenv, with its own config rendered against the remote database and the local broker, is DSS-5586**.
`up --local ...` refuses until then, on purpose: a service pointed at the cluster RabbitMQ starts real
consumers, and one with migrations at startup writes to the shared database.

Ports and names come from `manifest.json`; `project` is empty for every service until someone runs it
locally the first time and fills it in.

## Files

| File | Committed | What |
|---|---|---|
| `manifest.json` | yes | frontend, gateway and every service: repo, port, path prefix, libertine cluster ids, quirks |
| `environments/dev02.json` | yes | hostnames, Okta authorization server and client ids, SQL host. No secrets |
| `secrets.example.json` | yes | every secret key with its 1Password source |
| `secrets.json` | **no** | your filled-in copy |
| `devenv.local.json` | **no** | repos root, environment, developer name, local services, infra on/off |
| `infra/docker-compose.yml` | yes | rabbitmq, redis, jaeger; compose project `mavera-devenv` |
| `.state/` | **no** | pids of what `up` started, logs |
| `src/Devenv` | yes | the CLI (.NET 8 console app, no dependencies) |
| `tests/Devenv.Tests` | yes | manifest validation and config rendering, `dotnet test` |

Generated into other repos (both gitignored there):

- `mavera-libertine/LibertineWeb/appsettings.Development.json`
- `verisk-nordics-frontend/apps/dss/.env`

Never hand-edit them; change the manifest, environment or secrets and run `render`.

## Adding an environment

Copy `environments/dev02.json`, change the hostnames and Okta values (each environment has its own
authorization server `vnordics-<env>-dss` and its own internal-services client), then
`up --env <name>`. The Okta client secrets differ per environment too; keep one `secrets.json` per
environment if you switch often.

## Troubleshooting

- **`remote env` fails**: connect Zscaler. Everything else will fail too.
- **libertine answers but `/Vera/UserService/health` does not**: the remote side is unreachable through
  libertine; see `.state/logs/libertine.log`. The first request after start may time out once.
- **Okta login says "We could not start verification"**: `OKTA_CLIENT_SECRET` is wrong. The frontend log
  shows `The client secret supplied for a confidential client is invalid`.
- **`/libertine/organization/featureFlags` is 500**: libertine is on an old `develop`; it needs the
  TokenProvider fix (commit d09ddf4 or newer) and the `dss-5583_local-dev-cors` branch.
- **CORS errors in the browser**: libertine is not on `dss-5583_local-dev-cors`, or the rendered config
  is missing its `LocalDev` section. Run `render` again.
- **`something is already running`**: `down`, or delete `.state/processes.json` if those processes are gone.
- **Windows: `corepack` not found** although Node is installed: open a new terminal after installing Node.
