# mavera-compose

One Docker Compose app that runs the 29 Mavera .NET backend services plus SQL Server, Redis, RabbitMQ,
MongoDB, MinIO, Seq, an OTLP collector and Gotenberg — deployable as a single Dokploy Compose app.

```
docker compose up -d                        # infrastructure only
docker compose --profile platform up -d     # + the 13 core platform services
docker compose --profile all up -d          # + all 29
```

---

## How configuration works (read this first)

Every `appsettings.json` in these repos is an **envsubst template**, not a config file:

```json
"MessageBroker": {
  "Host": "rabbitmq.rabbitmq.svc.cluster.local",
  "Username": "$MessageBroker_Username",
  "VirtualHost": "$MessageBroker_VirtualHost"
}
```

The Kubernetes deployments render it in an initContainer
(`envsubst < /app/appsettings.json > /data/appsettings.json`) from one flat secret. That is why all 29
runtime images install `gettext`.

This stack does exactly the same thing:

1. `x-placeholders` in `docker-compose.yml` supplies that same flat variable set — all **163** tokens the
   29 services reference between them.
2. `config/entrypoint.sh` runs `envsubst` over `/app/appsettings.json` in place, then `exec dotnet $APP_DLL`.

So there are **no `Section__Key` overrides anywhere**. To change how a service is configured, change the
placeholder, exactly as you would change the cluster secret.

Two consequences worth knowing:

- **Service discovery costs nothing.** The templates already read
  `http://$ServiceSettings_Services_UserService.svc.cluster.local/Vera/UserService`. Each service
  declares a network alias `<name>.svc.cluster.local`, so those in-cluster URLs resolve verbatim in
  Docker. `rabbitmq.rabbitmq.svc.cluster.local` is hard-coded (not a placeholder) in 24 repos, so the
  broker carries that alias too.
- **`mavera-libertine` wires itself up.** It is a YARP gateway whose
  `ReverseProxy__Clusters__*__Destinations__Route__Address` values are the same placeholders, so its
  whole routing table is populated by `x-placeholders`. It is the fleet's public entry point.

`ASPNETCORE_ENVIRONMENT=Production`, matching the k8s overlays. No repo has an
`appsettings.Production.json`, so nothing can shadow the rendered file. Swagger is therefore off.

---

## Dokploy on an AWS VM

### Instance requirements

| | Minimum | Comfortable |
|---|---|---|
| Architecture | **x86_64 / amd64 — required** | amd64 |
| vCPU | 4 | 8 |
| RAM | 16 GB | 32 GB |
| Disk | 100 GB gp3 | 200 GB gp3 |
| Example | `t3.xlarge` | `m6i.2xlarge` / `m7i.2xlarge` |

**amd64 is not negotiable.** `mavera-ocr` publishes with `-r linux-x64 --self-contained true` and ships
`libPDFNetC.so`, which is an ELF64 **x86-64** binary. On Graviton (`m7g`, `t4g`) that service cannot
build or run. Every other service is architecture-neutral, so if you exclude the `documents` profile
Graviton would work — but the simple answer is: pick an Intel/AMD instance.

Prefer a **non-burstable** family. On `t3`/`t3a` the CPU credits drain during the first build of 29 .NET
services and everything crawls.

**Sizing rationale:** infrastructure is roughly 4 GB (SQL Server 2 GB capped, Mongo ~0.5, Seq ~0.5,
RabbitMQ ~0.3, MinIO ~0.2, Gotenberg ~0.2, Redis + collector ~0.2), and 29 .NET services at the
~200–400 MB the k8s manifests budget each is another 8–10 GB. That is ~12–14 GB steady state, hence
16 GB floor. AWS AMIs usually have **no swap**, so there is no cushion — either size the RAM properly
or add a swapfile.

Disk is dominated by the build: five .NET SDK majors (6.0, 7.0, 8.0, 9.0, 10.0) in alpine *and* Debian
variants, plus the BuildKit cache. Keep Docker's data-root on the large EBS volume.

### Security group

Only **80** and **443** need to be open inbound. Every infrastructure container publishes **no host
ports** — SQL Server, Mongo, RabbitMQ, Redis, MinIO and Seq are reachable only on the internal compose
network. The 29 services publish nothing either; traffic enters through the gateway.

To reach a management UI, tunnel rather than opening a port:

```bash
ssh -L 15672:localhost:15672 ec2-user@<vm>   # then docker compose port, or add a Dokploy domain
```

### Setting it up

1. **Create → Compose**, pointed at this repo. **Compose Type must be `Docker Compose`, not `Stack`** —
   Swarm mode does not support `build`.
2. Paste `.env.example` into the app's **Environment** tab and fill in `GH_PAT`, `GATEWAY_HOST`,
   `IDENTITY_HOST` and the passwords.
3. Set `COMPOSE_PROFILES` to choose what starts (empty = infra only; `all` = everything).
4. Leave `COMPOSE_PARALLEL_LIMIT=2`. **29 concurrent `dotnet build` processes will OOM a 16 GB VM** —
   this is the single most likely way a first deploy fails.
5. Add a domain for `mavera-libertine` (the gateway) and one for `mavera-identity-server` in the Dokploy
   UI. Both already carry HTTP Traefik labels and join `dokploy-network`; letting the UI own the domain
   means Dokploy also handles the certificate. (Commented `websecure` labels are in the compose file if
   you would rather declare TLS yourself — match your own `certResolver` name.)
6. Deploy **one profile at a time** on first run: infra, then `platform`, then the rest. It keeps peak
   memory down and makes a failure attributable to one group.

### Memory guard

SQL Server on Linux grows to roughly 80% of host RAM by default, which on a shared VM starves the 29
service containers. `MSSQL_MEMORY_LIMIT_MB` (default `2048`) caps it.

That variable is only honoured by the image during **first-run setup**, so on a volume that already
exists it does nothing. `mssql-init` therefore also applies it with `sp_configure 'max server memory
(MB)'` on every run, which works regardless of when the volume was created. Raise it on a 32 GB box.

### Host prerequisites

Remote git build contexts need a Compose version that resolves them. Verified working on Linux
Compose **v5.5.0**; verified broken on Windows Compose v5.0.1 (see below). Check with:

```bash
docker compose version
docker buildx version        # BuildKit is required for the COPY ../ idiom
```

Do **not** add `docker builder prune` to a cron or post-deploy hook — see *First deploy is heavy*.

### Running it locally instead

The compose file joins Traefik's `dokploy-network`, which is `external`. Create it once:

```bash
docker network create dokploy-network
docker compose up -d            # infrastructure only — works everywhere
```

### ⚠️ On Windows, Compose cannot build the remote git contexts

Verified on Docker Desktop 29.1.3 / Compose **v5.0.1** (Windows): any `build.context` that is a
git URL fails before the build starts, because Compose resolves it as a local path:

```
failed to evaluate path "https://github.com/...git#develop":
  CreateFile C:\...\mavera-compose\https:: The filename, directory name, or volume label syntax is incorrect.
```

This is **not** about the embedded credentials — a plain public URL fails identically. It affects
`docker compose build` *and* `docker compose up` (because `pull_policy: build` makes `up` build too).

It is a Compose-on-Windows problem only. Verified working:

| Path | Result |
|---|---|
| Linux Compose **v5.5.0**, same file | ✅ builds |
| `docker buildx bake -f docker-compose.yml`, same file | ✅ builds |
| `docker buildx build <git-url>` | ✅ builds |
| Windows Compose v5.0.1 | ❌ `failed to evaluate path` |

**Dokploy runs Linux, so deployment is unaffected.** For local work on Windows, build with `bake`
(which reads this very compose file) and then start without building:

```bash
export GH_PAT=$(gh auth token)                        # or your PAT
docker buildx bake -f docker-compose.yml mavera-audit  # build via bake
docker compose --profile platform up -d --no-build mavera-audit
```

---

## Services

Container port is **80** for all 29. None publish host ports; reach them through the gateway or with
`docker compose exec`.

### Profile `platform` (13)

| Service | Health endpoint |
|---|---|
| mavera-identity-server | `Vera/IdentityServer/api/health` |
| mavera-libertine *(gateway, public)* | — |
| mavera-identity-client | `health` |
| mavera-user-service | `Vera/UserService/health` |
| mavera-caregivers | `caregivers/health` |
| mavera-notification-service | `internal-notification/health` |
| mavera-medical-advisor-network | `Vera/MedicalAdvisorNetwork/health` |
| mavera-kuralink | `KuralinkClient/health` |
| mavera-dashboard | — |
| mavera-news-manager | — |
| mavera-audit | — |
| mavera-background-task-schduler | — |
| mavera-reflection | — |

### Profile `evaluation` (3)

| Service | Health endpoint |
|---|---|
| mavera-evaluation-service | `Vera/EvaluationService/health` (+ `/live`, `/ready`) |
| mavera-enhanced-patient-view | `enhanced-patient-view/health` |
| mavera-fkassan-service | `fkassan/health` |

### Profile `documents` (5)

| Service | Health endpoint |
|---|---|
| mavera-document-service | `Vera/DocumentService/health` |
| mavera-storage-service | `api/Storage/health` |
| mavera-file-conversion-service | `FileConversionService/health` (no compose healthcheck — see below) |
| mavera-pdf-generator-service | — |
| mavera-ocr | — |

### Profile `ai` (8)

| Service | Health endpoint |
|---|---|
| mavera-ai-document-classifier | `api/documentclassifier/health` |
| mavera-ai-document-processor | `api/documentprocessor/health` |
| mavera-ai-document-summaries | `documentSummaries/health` |
| mavera-ai-identitycheck-processor | `IdentityCheck/health` |
| mavera-ai-journalevents-classifier | `api/JournalEventsClassifier/health` |
| mavera-ai-timeline | `api/timeline/health` |
| mavera-ai-client-relevance-score | — |
| mavera-ai-document-anonymize | — |

There is no uniform `/health` — each service mounts its probe under its own route prefix. Services with
no health endpoint get **no `healthcheck:`**, rather than a probe that always fails. Two Debian-based
images (`mavera-ocr`, `mavera-file-conversion-service`) have no `wget`/`curl`, so they get no healthcheck
either even where the app exposes one.

### Infrastructure (always starts)

| Service | Purpose |
|---|---|
| `sqlserver` | SQL Server 2022; `mssql-init` creates the 7 catalogs then exits |
| `mongo` | MongoDB 7; `mongo-init` creates the app user + 11 databases |
| `rabbitmq` | 3.13-management, aliased `rabbitmq.rabbitmq.svc.cluster.local` |
| `redis` | 7-alpine, AOF persistence |
| `minio` | S3-compatible storage; `minio-init` creates the buckets |
| `seq` | Structured log/trace UI |
| `otel-collector` | Receives OTLP/gRPC on 4317, forwards to Seq |
| `gotenberg` | PDF engine behind `mavera-file-conversion-service` |

Management UIs publish no ports. Add Dokploy domains if you want them: RabbitMQ `15672`, Seq `80`,
MinIO console `9001`.

---

## Things you need to know

### 1. SQL databases

`config/mssql-init/00-init-databases.sql` runs on every `docker compose up` and is idempotent. It
**creates four empty catalogs** and **reports on three it never creates**:

| Database | Handled by | Notes |
|---|---|---|
| `MaveraInboxOutbox` | created empty | MassTransit saga/outbox; schema comes from the services that migrate at startup |
| `MaveraScheduler` | created empty | Hangfire creates its own schema |
| `MaveraStorageOperations` | created empty | `storage-service` |
| `MaveraOcrOperations` | created empty | `mavera-ocr` |
| `vera-dev02` | **restore manually** | main catalog, `$ConnectionStrings_DB_Name` |
| `vera-caregivers-dev02` | **restore manually** | `$ConnectionStrings_CaregiverContext_DB_Name` |
| `vera-identity-dev02` | **restore manually** | `$ConnectionString_DB_IdentityServer` |

The three data-bearing databases are deliberately **not created**. If the script created an empty
`vera-dev02`, your manual `RESTORE` would be forced to use `WITH REPLACE`. Instead it just prints
whether each one is present:

```
[create]  MaveraInboxOutbox (empty)
[present] vera-dev02 (restored)
[MISSING] vera-caregivers-dev02 - restore it manually, or repoint $ConnectionStrings_CaregiverContext_DB_Name
NOTE: 1 data-bearing database(s) missing. Services reading them will start but fail on their first query.
```

A missing database is a **warning, not a failure** — the deploy still succeeds, so you can stand the
stack up before the restore is done. Existing databases are never touched: no `DROP`, no `REPLACE`, no
`ALTER`.

#### Restoring the data-bearing databases (one-off)

Backups are **not** shipped with this repo. Copy them onto the VM and restore once. `RESTORE` runs
inside the SQL Server process, so the file must be inside the `sqlserver` container:

```bash
# 1. get the file into the container (no bind mount needed)
docker compose cp mybackup.bak sqlserver:/var/opt/mssql/data/

# 2. find the LOGICAL file names — they do NOT match the database name
#    (vera-dev02's data file is logically "vera2"; vera-identity-dev02's is "vera-identity-test")
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASS"   -Q "RESTORE FILELISTONLY FROM DISK='/var/opt/mssql/data/mybackup.bak'"

# 3. restore, relocating each file with MOVE (required: the backups carry
#    Windows paths like C:\Program Files\... that do not exist on Linux)
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASS" -b   -Q "RESTORE DATABASE [vera-dev02] FROM DISK='/var/opt/mssql/data/mybackup.bak' WITH RECOVERY, STATS=25,
       MOVE 'vera2'     TO '/var/opt/mssql/data/vera-dev02_1.mdf',
       MOVE 'vera2_log' TO '/var/opt/mssql/data/vera-dev02_2.ldf'"

# 4. tidy up and confirm
docker compose exec sqlserver rm /var/opt/mssql/data/mybackup.bak
docker compose up -d --force-recreate mssql-init && docker compose logs mssql-init
```

Restoring under the **original** source names means any 3-part reference inside the data (views, procs,
synonyms) keeps resolving. If you restore under different names, repoint the three placeholders in
`x-placeholders` to match.

Backups from SQL Server 2019 (major 15) restore cleanly onto this 2022 image; compatibility levels 110
and 150 both remain supported.

A `.sql` dump works too: any file dropped into `config/mssql-init/` is applied in filename order, so
name it `10-something.sql` to run after the bootstrap.

### 2. `GH_PAT` is embedded in the build-context URL

Build contexts are `https://x-access-token:${GH_PAT}@github.com/MaveraDSS/<repo>.git#develop`, and
BuildKit records that URL in build history. Use a **fine-grained, read-only, short-lived** PAT scoped to
these repos (or a GitHub App installation token) and rotate it. Removing this exposure is the main reason
to switch to pull-only — see below.

### 3. First deploy is heavy

29 .NET builds spanning five SDK majors — 6.0 (identity-server), 7.0 (doc-anonymize, kuralink), 8.0
(19 repos), 9.0 (user-service, pdf-generator, ocr, storage-service), 10.0 (identity-client) — in both
alpine and Debian variants. Budget tens of GB of disk and a long first run. Bring profiles up one at a
time. BuildKit caches git contexts by resolved commit, so unchanged repos are not rebuilt on redeploy.

### 4. Out-of-scope dependencies

`dashboard`, `evaluation-service`, `notification-service` and `user-service` all resolve
`$ServiceSettings_Services_Translation`, which points at a container this stack does not run.
`mavera-translation-service` is ready to uncomment at the bottom of `docker-compose.yml` (along with
`mavera-client-webhook` and `mavera-client-external-service`).

The AI **model** endpoints (`AiModels_*`) are separate Python services and default to a non-resolving
host, so classification/summarisation/relevance calls will fail until you point them somewhere real.

### 5. Blank secrets

Okta, Apryse (`APRYSE_LICENSE_KEY`), mail, SMS, SMB, Kuralink and the crypto keys are empty by default.
Flows depending on them will not work. OCR and PDF generation in particular need a valid Apryse licence.

### 6. Passwords must not contain `$`

Config is rendered with `envsubst`, so a `$` in a password would be treated as a variable reference.

---

## Build vs pull — what actually happens

**Nothing is downloaded from a registry to get your service images.** Every service declares both
`image:` and `build:`, plus `pull_policy: build` in `x-service-base`. That means:

- Dokploy builds all 29 images **on the Dokploy host**, from the GitHub repos, at deploy time.
- The `image:` value (`mavera.azurecr.io/mavera/<env>/<svc>:<tag>`) is only the **name and tag applied
  to the locally built image**. Compose does not push it and does not pull it.
- The built images live in the Dokploy host's local Docker image store and are used straight from there.
- **No container registry or registry credentials are needed at all** in this mode.

`pull_policy: build` is load-bearing. Without it, Compose's documented behaviour when a service has both
`image:` and `build:` is to *try the registry first and build only if the pull fails* — which would mean
a doomed, unauthenticated ACR round-trip on every single deploy. `build` skips that and rebuilds from
source (BuildKit caches by resolved git commit, so unchanged repos are near-instant).

What *is* downloaded during a build: the .NET base images from `mcr.microsoft.com`, NuGet packages
(nuget.org plus the private MyGet `cloudjunction` feed), and the 29 git contexts from GitHub.

### Switching to pull-only later (recommended once CI supports it)

Every image already exists in ACR at `mavera.azurecr.io/mavera/<env>/<service>`, but only under 10-char
git-SHA tags — there is no `:latest` or branch tag. Once `MaveraDSS/ci-template` also pushes a moving tag
(e.g. `develop`), this stack can stop building entirely. Two changes:

1. In `x-service-base`, change `pull_policy: build` to `pull_policy: always`.
2. Delete (or comment out) the `build:` block on each service.

Then add Dokploy registry credentials for `mavera.azurecr.io` and:

```bash
docker compose --profile all pull
docker compose --profile all up -d
```

The `image:` references are already written as `${ACR}/mavera/${ACR_ENV}/<service>:${TAG}`, so nothing
else changes. That removes both the long build and the `GH_PAT` exposure.

---

## What has actually been verified

Run against Docker 29.1.3 on 2026-09-03, infrastructure plus `mavera-audit`:

| Check | Result |
|---|---|
| Infra up, health gates, init jobs | 8 containers up, 5 healthy, all 3 init jobs `exit 0` |
| 4 empty catalogs created, 3 reported | ✅ idempotent — re-runs print `[skip]` / `[present]`, `create_date` unchanged |
| Manual restore path proven | ✅ all 3 restored via `MOVE` from real backups (`vera-dev02` 123 tables, `vera-caregivers-dev02` 12, `vera-identity-dev02` 22) before the machinery was removed |
| Missing database is non-fatal | ✅ warns and exits 0, so the stack deploys before the restore is done |
| 11 Mongo databases + admin-scoped user | ✅ |
| 2 MinIO buckets | ✅ |
| `rabbitmq.rabbitmq.svc.cluster.local` resolves | ✅ same IP as `rabbitmq` — the alias design works |
| Real build from the **private** git URL (Linux Compose) | ✅ including `COPY ../` clamping and `dockerfile:` on a remote context |
| Image tagged locally, nothing pushed or pulled | ✅ `mavera.azurecr.io/mavera/dev/mavera-audit:develop` built on the host |
| envsubst render inside the running container | ✅ Mongo URI, broker host, OTLP endpoint all resolved; no `$` literals |
| MassTransit connected | ✅ `Bus started: rabbitmq://rabbitmq.rabbitmq.svc.cluster.local/`, queue `audit-consumers` declared |
| Mongo read path | ✅ `GET /audit-logs/emails` and `/audit-logs/sms` → `204 No Content` |
| OTLP telemetry end to end | ✅ collector `accepted_spans=3` over gRPC, `sent_spans=3` to Seq, zero failures |

Three real bugs were found and fixed by doing this: `mcr.microsoft.com/mssql-tools18:latest` does not
exist (the server image ships sqlcmd 18, so it is reused); Seq refuses to start without
`SEQ_FIRSTRUN_ADMINPASSWORD`; and the Mongo app user has to be created in `admin`, not in each
application database, or every service fails with `saslStart failed: Authentication failed`.

Not yet verified: the other 28 services (only `mavera-audit` was built and run), and anything that
needs a populated SQL schema or real Okta/Apryse credentials.

## Verifying a deployment

```bash
# 1. config resolves (no daemon needed). Requires a .env with the required
#    variables set: GATEWAY_HOST, IDENTITY_HOST, DB_PASS, MONGO_ROOT_PASS,
#    MONGO_PASS, RABBITMQ_PASS, MINIO_PASS, SEQ_ADMIN_PASS. It fails naming
#    any that are unset or blank -- deliberately, so a missing .env can never
#    deploy silently with dev defaults.
docker compose config >/dev/null && echo OK
COMPOSE_PROFILES=all docker compose config --services | grep -c '^mavera-'   # 29

# 2. the build contract, on one service first
docker compose build mavera-audit

# 3. config actually renders — no $ literals, absolute OTLP URI
docker compose run --rm --entrypoint sh mavera-audit -c 'envsubst < /app/appsettings.json'

# 4. infrastructure
docker compose up -d
docker compose ps                      # infra healthy, *-init exited (0)
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd \
    -C -S localhost -U sa -P "$DB_PASS" -Q "SELECT name FROM sys.databases"

# 5. the in-cluster DNS trick, which everything depends on
docker compose exec mavera-audit getent hosts mavera-user-service.svc.cluster.local
docker compose exec mavera-audit getent hosts rabbitmq.rabbitmq.svc.cluster.local

# 6. one service end to end
docker compose --profile platform up -d mavera-audit
docker compose logs mavera-audit       # no '$' placeholders, no UriFormatException
#   -> 'audit-consumers' queue appears in RabbitMQ  (MassTransit connected)
#   -> a trace for mavera-audit appears in Seq      (collector path works)

# 7. widen one profile at a time, watching for restart loops
COMPOSE_PROFILES=platform docker compose up -d && docker compose ps
```

### Troubleshooting

| Symptom | Cause |
|---|---|
| Config values arrive as literal `$Foo` | entrypoint was overridden, or `gettext` missing |
| `UriFormatException` at startup | `Tracing_Connection_String` is empty or not absolute |
| `Name or service not known` for `*.svc.cluster.local` | the target service isn't running, or is missing its network alias |
| MassTransit connection refused | check the `rabbitmq.rabbitmq.svc.cluster.local` alias on the broker |
| `Invalid object name 'dbo.X'` | empty database — see *SQL schema* above |
| `network dokploy-network not found` | running locally: `docker network create dokploy-network` |
| Deploy stalls on `Pulling ... unauthorized` | `pull_policy: build` was removed from `x-service-base` |

---

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | The stack: 11 infra/init containers + 29 services |
| `.env.example` | Every knob, documented |
| `config/entrypoint.sh` | envsubst wrapper, mounted into all 29 |
| `config/otel-collector.yaml` | OTLP gRPC → Seq |
| `config/mssql-init/00-create-databases.sql` | The 7 SQL catalogs |
| `config/mongo-init.js` | App user + 11 Mongo databases |
| `config/minio-init.sh` | Buckets |
