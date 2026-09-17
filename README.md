# mavera-compose

The 29 Mavera .NET backend services plus SQL Server, Redis, RabbitMQ, MongoDB, MinIO, Seq, an OTLP
collector and Gotenberg.

There are two ways to run this, and they are not the same shape:

| | file | what it is for |
|---|---|---|
| **Dokploy** | `docker-compose.infra.yml` + `docker-compose.apps.yml` | the deployed environment: two Compose services, infrastructure and the 29 apps. |
| **Previews** | `scripts/dokploy/` | one Dokploy **Application** per repo that wants PR preview deployments — the only service type Dokploy gives previews to. |
| **Local** | `docker-compose.yml` | one all-in-one stack on your own machine. Unchanged, still the quickest way to bring the whole fleet up. |

```
# local, all-in-one
docker compose up -d                        # infrastructure only
docker compose --profile platform up -d     # + the 13 core platform services
docker compose --profile all up -d          # + all 29
```

For the deployed environment, read *Topology on Dokploy* next, then `DEPLOY.md`.

---

## Topology on Dokploy

### Why it is split this way

Dokploy's **preview deployments** — a throwaway environment per pull request — only exist for the
**Application** service type. Its Docker-Compose service type has no preview feature at all. That is
the whole reason any of this is not one compose file.

The obvious move is to make all 29 services Applications. That was tried and abandoned, for a concrete
reason: an Application needs two things set that a compose service gets for free —

- `command`, the `envsubst` entrypoint override, and
- `networkSwarm`, the DNS aliases every service resolves its siblings by.

Both are written through `application.update`, which on this Dokploy version **accepts the call and
persists nothing**. Confirmed by reading the Application back: `command: None`, `args: None`,
`networkSwarm: None`, while everything written by `saveGithubProvider`, `saveBuildType` and
`saveEnvironment` persisted correctly. That leaves setting three fields by hand, per service, forever.

In compose both are four lines of YAML that already work. And previews do not need production to be
Applications — a preview Application works perfectly well alongside a compose-based production. So:

```
Dokploy project "mavera" / environment "production"
│
├── compose service  "mavera-infra"     ./docker-compose.infra.yml
│     sqlserver + mssql-backups-init + mssql-init
│     redis · rabbitmq · mongo + mongo-init · minio + minio-init
│     seq · otel-collector · gotenberg
│
├── compose service  "mavera-apps"      ./docker-compose.apps.yml
│     the 29 .NET services, profiles platform / evaluation / documents / ai
│     aliases: <svc> and <svc>.svc.cluster.local
│     mavera-libertine       → GATEWAY_HOST
│     mavera-identity-server → IDENTITY_HOST
│
└── Application  "<svc>-pr"             opt-in, one per repo that wants previews
      GitHub → Dockerfile, previews on, NO aliases
      Run Command + entrypoint mount set once in the UI
      → preview-<app>-<hash>.<wildcard>
```

Both compose services sit on the external `dokploy-network`, which is what lets the apps reach
infrastructure, and lets a preview container reach the whole production fleet.

What this gives up: per-service deploy and rollback buttons in the Dokploy UI for production. A deploy
is per compose service, so `docker-compose.apps.yml` redeploys as a unit. Profiles still let you stage
a first rollout.

### How the services find each other

Compose registers each service's own name as a network alias on **every** network its container joins,
including an external one. So `sqlserver`, `mongo`, `redis`, `minio`, `otel-collector`, `gotenberg` and
`seq` resolve from the app stack with **no change to any `$Placeholder` value**.

App-to-app needs both DNS forms, because the wiring is name-based and partly unreachable from outside
the service repos:

| what | value |
|---|---|
| `ServiceSettings_Services_*` (26 of them) | bare container names; the templates append `.svc.cluster.local` |
| `IdentityClient_BaseUrl` | `http://mavera-identity-client.svc.cluster.local` |
| `InternalServices_Authority` | `http://mavera-identity-server` (no suffix) |
| RabbitMQ host | `rabbitmq.rabbitmq.svc.cluster.local`, hard-coded in 24 of the 29 repos |

So every service declares both:

```yaml
    networks:
      dokploy-network:
        aliases:
          - mavera-user-service
          - mavera-user-service.svc.cluster.local
```

Naming any alias replaces the implicit one Compose would have added, which is why the bare name is
spelled out too.

Two Dokploy settings on both compose services are load-bearing:

- **Compose Type `Docker Compose`, not `Stack`.** `docker stack deploy` renames services to
  `<appName>_sqlserver`, which breaks plain-name DNS (and Swarm has no `build`).
- **Isolated Deployments off.** It moves a stack onto a private network and *off* `dokploy-network`.

### Previews

A preview Application is added per repo, and it is **only** a preview host — it is never deployed
itself. It carries **no network aliases**, which is the point: previews inherit their parent's
`networkSwarm`, so a preview of an aliased Application would answer to a production name and Docker's
DNS would round-robin roughly half of production's internal calls into an unreviewed PR build.

| | production (compose) | preview host (Application) |
|---|---|---|
| where | `docker-compose.apps.yml` | Dokploy Application `<svc>-pr` |
| aliases | both DNS forms | **none** |
| previews | n/a | on |
| deployed | yes | never — it only spawns previews |

A preview reaches its 28 siblings and all infrastructure through the compose stack's aliases, and is
reached itself only on its own generated URL.

`command`/`args` are inherited by previews and there is no `previewCommand`, so the Run Command and the
entrypoint mount are set **once** per preview host in the UI and cover every future PR on that repo.

### `docker-compose.yml` is still the source of truth

`docker-compose.infra.yml` and `docker-compose.apps.yml` are generated from it and committed. It also
remains the all-in-one local stack. When `x-placeholders` changes, regenerate rather than editing three
files:

```bash
python scripts/dokploy/generate-manifest.py   # env blocks for the preview Applications
```

`scripts/dokploy/provision.py` still creates the preview-host Applications (GitHub provider, build type,
environment, domains — the parts that demonstrably persist) and its `--inspect` / `--verify-only` modes
are how you check what Dokploy actually stored.

### How `entrypoint.sh` gets into the containers

Every image's ENTRYPOINT is `dotnet <App>.dll`, and it has to be replaced with "render the appsettings
templates, then exec dotnet" (see *How configuration works*). In the compose stacks that is a bind
mount plus an `entrypoint:` override, which is what `x-service-base` already does:

```yaml
  entrypoint: ["/bin/sh", "/entrypoint.sh"]
  volumes:
    - ./config/entrypoint.sh:/entrypoint.sh:ro
```

For a preview **Application** there is no compose file to mount from, so it needs Dokploy's Run Command
(`/bin/sh /entrypoint.sh`, which maps to `ContainerSpec.Command` — a real ENTRYPOINT override despite
the docs describing it as a debugging `exec`) plus a bind mount of the script from the host. Set once
per preview host; previews inherit both.

The script does three things worth knowing about:

- **It renders every `appsettings*.json`, not just the base file.** `ASPNETCORE_ENVIRONMENT` is set, so
  .NET loads `appsettings.Production.json` *on top of* `appsettings.json` wherever a repo has one — and
  an unrendered override silently wins over a rendered base.
- **It reconciles placeholder capitalisation.** The repos are not consistent with each other: some
  templates spell a placeholder `$log_Level`, others `$Log_Level`, and `x-placeholders` can only define
  one. `envsubst` matches names exactly and Linux environment variables are case-sensitive, so the
  other spelling would render as an empty string. For any placeholder a template uses that is unset,
  the first-letter case variant is tried before giving up. This is why the env block is one entry per
  placeholder rather than two, and why it works in compose as well.
- **It reports what happened**, because the two failure modes are indistinguishable from the app's side:

  ```
  [entrypoint] rendered: /app/appsettings.json /app/appsettings.Production.json
  [entrypoint] case-matched: log_Level<-Log_Level
  [entrypoint] UNSET (rendered as empty strings):
      $Okta_Domain
  [entrypoint] appsettings.json rendered; starting Mavera-Audit.dll
  ```

  A literal `$Name` surviving means the file was never rendered — `envsubst` substitutes the empty
  string for an unset name and never leaves a literal. A value arriving **empty** means nothing defined
  it under either spelling. Blank Okta, mail, SMS and SMB entries are expected.

`scripts/dokploy/test_entrypoint.sh` exercises all of this against a throwaway `/app`. Run it on Linux
for full coverage — one case covers case-sensitivity, which cannot be demonstrated on Windows, where
environment lookups are case-insensitive.

### What changed from the original single-compose setup

- **`depends_on` no longer spans the two stacks.** The apps used to wait for `mssql-init`, `mongo-init`
  and `minio-init` to complete and for `rabbitmq` and `redis` to be healthy. Compose cannot wait on a
  service in another project, so those are gone: deploy `mavera-infra` first and let it settle, or the
  app containers crash-loop until it answers. Within a stack, `depends_on` still works.
- **`GH_PAT` is still required.** `docker-compose.apps.yml` keeps the git-URL build contexts, so
  BuildKit clones the 29 service repos with it — and it still ends up in build history (see *`GH_PAT` is
  embedded in the build-context URL*). Only the preview Applications use the Dokploy GitHub App instead.
- **Volume names changed.** Compose prefixes volumes with the project name, and Dokploy sets that to the
  compose service's `appName`. `sqlserver-data` is therefore not the old `mavera_sqlserver-data`. The
  SQL databases restore from `Databases.zip` on first run so this is usually fine, but anything you care
  about in the old volumes must be copied across deliberately.
- **Capacity.** A 4 vCPU / 16 GB VM at ~12–14 GB steady state has little headroom. Each preview adds a
  container plus a .NET SDK build, so keep `--preview-limit` low and enable preview hosts only where
  work is happening.

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

`DEPLOY.md` is the step-by-step. In outline:

1. **Create → Compose**, Compose Path **`./docker-compose.infra.yml`**. **Compose Type must be
   `Docker Compose`, not `Stack`**, and leave **Isolated Deployments off** — see *Topology on Dokploy*
   for why both matter. Paste `.env.example` into its **Environment** tab, fill in the passwords, and
   **turn on env-file generation**.
2. Deploy it, and confirm the SQL restore in the `mssql-init` logs before going further.
3. **Create → Compose** again, Compose Path **`./docker-compose.apps.yml`**, same Compose Type and
   Isolated Deployments settings, same environment block (it needs `GH_PAT`, the hostnames and the
   Okta credentials as well as the infrastructure passwords).
4. Deploy it one profile at a time via `COMPOSE_PROFILES` — `platform`, then `evaluation`,
   `documents`, `ai` — and keep `COMPOSE_PARALLEL_LIMIT=2`, because 29 concurrent `dotnet build`
   processes will OOM a 16 GB VM.
5. Add the two domains: `mavera-libertine` → `GATEWAY_HOST`, `mavera-identity-server` →
   `IDENTITY_HOST`.
6. **Only for previews:** connect the Dokploy **GitHub App** to the `MaveraDSS` org, then run
   `scripts/dokploy/generate-manifest.py` and `provision.py --role preview --only <repo> --apply` for
   each repo that wants them. Finish each one in the UI with Run Command and the entrypoint mount.

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
failed to evaluate path "https://github.com/...git#release/v.be-2026-04-01":
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

## Reaching the services

Only two containers carry Traefik labels, so only two hostnames exist:

| Hostname | Container | What it is |
|---|---|---|
| `GATEWAY_HOST` | `mavera-libertine` | YARP gateway — the API surface for every routed service |
| `IDENTITY_HOST` | `mavera-identity-server` | OIDC issuer; where tokens come from |

The other 27 services publish no host ports. They are reachable only through the gateway, or
service-to-service on the compose network.

### The route table

Everything the gateway serves lives under a `/libertine/` prefix:

```
$PUBLIC_SCHEME://$GATEWAY_HOST/libertine/...
```

Every route rewrites the path with a YARP `PathPattern` transform, so the path the target service
receives is not the one you requested. Both are listed here — the *Rewritten to* column is what shows
up in that service's logs, and is the path its own OpenAPI document describes.

This table is generated from `ReverseProxy` in `LibertineWeb/appsettings.json`. If a route changes
upstream, this goes stale — regenerate it from that file rather than trusting it blindly.

| Request path | Method | Target | Rewritten to |
|---|---|---|---|
| `/libertine/evaluation/list` | **GET** | `mavera-evaluation-service` | `Vera/EvaluationService/Evaluation/pagedList` |
| `/libertine/evaluation/send` | **POST** | `mavera-evaluation-service` | `Vera/EvaluationService/Assign/send` |
| `/libertine/evaluation/share` | **PUT** | `mavera-evaluation-service` | `Vera/EvaluationService/ShareEvaluation` |
| `/libertine/evaluation/sign` | **POST** | `mavera-evaluation-service` | `Vera/EvaluationService/Evaluation/signEvaluation` |
| `/libertine/evaluation/{evaluationId}/message` | **POST** | `mavera-evaluation-service` | `Vera/EvaluationService/message` |
| `/libertine/evaluation/{evaluationId}/messages` | **GET** | `mavera-evaluation-service` | `Vera/EvaluationService/message/{evaluationId}` |
| `/libertine/evaluation/{evaluationId}/note` | **POST** | `mavera-evaluation-service` | `Vera/EvaluationService/Note` |
| `/libertine/evaluation/{evaluationId}/notes` | **GET** | `mavera-evaluation-service` | `Vera/EvaluationService/Note/{evaluationId}` |
| `/libertine/evaluation/{evaluationId}/questions` | **GET** | `mavera-evaluation-service` | `Vera/EvaluationService/EvaluationQuestion/{evaluationId}/questions` |
| `/libertine/EvaluationService/{**catch-all}` | any | `mavera-evaluation-service` | `Vera/EvaluationService/{**catch-all}` |
| `/libertine/case/{**catch-all}` | any | `mavera-evaluation-service` | `Vera/EvaluationService/maveracase/{**catch-all}` |
| `/libertine/evaluation/{**catch-all}` | any | `mavera-evaluation-service` | `Vera/EvaluationService/Evaluation/{**catch-all}` |
| `/libertine/EvaluationDocument/documentType` | **GET** | `mavera-document-service` | `Vera/DocumentService/EvaluationDocument/documentType` |
| `/libertine/DocumentService/{**catch-all}` | any | `mavera-document-service` | `Vera/DocumentService/{**catch-all}` |
| `/libertine/companyadmin/{**catch-all}` | any | `mavera-user-service` | `Vera/UserService/companyadmin/{**catch-all}` |
| `/libertine/user/{**catch-all}` | **PATCH, PUT** | `mavera-user-service` | `Vera/UserService/user/{**catch-all}` |
| `/libertine/caregivers/{**catch-all}` | any | `mavera-caregivers` | `caregivers/{**catch-all}` |
| `/libertine/evaluationcaregiver/{**catch-all}` | any | `mavera-caregivers` | `caregivers/evaluationcaregiver/{**catch-all}` |
| `/libertine/ai/documentclassifier` | **GET** | `mavera-ai-document-classifier` | `api/documentclassifier/query` |
| `/libertine/ai/documentclassifier/{evaluationId}` | **GET** | `mavera-ai-document-classifier` | `api/documentclassifier/query/{evaluationId}` |
| `/libertine/ai/{**catch-all}` | any | `mavera-ai-document-processor` | `api/documentprocessor/{**catch-all}` |
| `/libertine/evaluation/{evaluationId}/timeline` | **GET** | `mavera-ai-timeline` | `api/timeline/evaluation/{evaluationId}` |
| `/libertine/timeline/evaluation/documentEvents/{documentId}` | **GET** | `mavera-ai-timeline` | `api/timeline/evaluation/documentEvents/{documentId}` |
| `/libertine/timeline/{evaluationId}/status` | **GET** | `mavera-ai-timeline` | `api/timeline/{evaluationId}/status` |
| `/libertine/document-summaries/{**catch-all}` | any | `mavera-ai-document-summaries` | `documentSummaries/{**catch-all}` |
| `/libertine/IdentityCheck/{**catch-all}` | any | `mavera-ai-identitycheck-processor` | `IdentityCheck/{**catch-all}` |
| `/libertine/OCRService/{**catch-all}` | any | `mavera-ocr` | `OCRService/{**catch-all}` |
| `/libertine/pdf-generator/{**catch-all}` | any | `mavera-pdf-generator-service` | `pdf/{**catch-all}` |
| `/libertine/enhanced-patient-view/{**catch-all}` | any | `mavera-enhanced-patient-view` | `enhanced-patient-view/{**catch-all}` |
| `/libertine/fkassan/{**catch-all}` | any | `mavera-fkassan-service` | `fkassan/{**catch-all}` |
| `/libertine/audit-logs/{**catch-all}` | any | `mavera-audit` | `audit-logs/{**catch-all}` |
| `/libertine/identity-client/{**catch-all}` | any | `mavera-identity-client` | `identity-client/{**catch-all}` |

Where an explicit route and a catch-all both match, YARP picks the more specific one, so
`/libertine/evaluation/list` reaches `Evaluation/pagedList` rather than the `evaluation/**`
catch-all.

### Expect 401 before you expect 200

No route carries an `AuthorizationPolicy`, so the gateway forwards anything it can match. The
*target* service is what validates the bearer token — on `release/v.be-2026-04-01` its
`SecuritySettings` accepts audiences `api` and `internalapi`, with the identity server as authority
alongside `OktaAuthority`.

```bash
# 401 from evaluation-service -- which means the gateway and the route both worked
curl -i "http://$GATEWAY_HOST/libertine/evaluation/list"

# with a token
curl -i -H "Authorization: Bearer $TOKEN" "http://$GATEWAY_HOST/libertine/evaluation/list"
```

Read the two failure modes differently:

| Response | Meaning |
|---|---|
| `401` | Route matched, request reached the service, token missing or rejected |
| `404` | **No route matched.** Check the prefix and the method — `/libertine/user/**` accepts only PATCH and PUT |
| `502` / `503` | Route matched but the target container is down or has lost its network alias |

### Two routes that cannot work here

| Route | Why |
|---|---|
| `/libertine/pdf-search/**` | Its cluster address is hardcoded to an ngrok URL (`mavera.ngrok.pdfsearch.app.ngrok.pizza`), not a placeholder — there is nothing in this stack to point it at. |
| `/libertine/idp2/**` | Resolves `$ServiceSettings_Services_Idp2`, which defaults to `not-configured`. Set `AI_IDP2_HOST` to a reachable host to use it. |

### Services with no gateway route

`mavera-ai-client-relevance-score`, `mavera-ai-document-anonymize`, `mavera-ai-journalevents-classifier`, `mavera-background-task-schduler`, `mavera-dashboard`, `mavera-file-conversion-service`, `mavera-kuralink`, `mavera-medical-advisor-network`, `mavera-news-manager`, `mavera-notification-service`, `mavera-reflection`, `mavera-storage-service`.

These are called service-to-service only. To reach one directly — for a health check or while
debugging — use the compose network rather than the gateway:

```bash
docker compose exec mavera-libertine \
  curl -s http://mavera-storage-service.svc.cluster.local/api/Storage/health
```

Health endpoints are internal paths, and only some happen to sit under a gateway route:
`/libertine/caregivers/health` and `/libertine/EvaluationService/health` resolve, but user-service's
`Vera/UserService/health` has no matching route. Use the `exec` form above for health checks; the
per-service paths are in the profile tables above.

## Things you need to know

### 1. SQL databases

SQL bootstrap is two steps, both idempotent and both run on every `docker compose up`:
`config/mssql-restore.sh` **restores the three data-bearing databases from backup**, then
`config/mssql-init/00-init-databases.sql` **makes sure all seven databases exist**, creating as an
empty database anything the restore did not produce.

So a deploy with no backups at all still ends with seven databases on the instance. That matters
because a service whose database is *missing* cannot start its EF migration at all, whereas one
whose database is merely *empty* migrates itself and builds its own schema.

| Database | Handled by | Notes |
|---|---|---|
| `MaveraInboxOutbox` | created empty | MassTransit saga/outbox; schema comes from the services that migrate at startup |
| `MaveraScheduler` | created empty | Hangfire creates its own schema |
| `MaveraStorageOperations` | created empty | `storage-service` |
| `MaveraOcrOperations` | created empty | `mavera-ocr` |
| `vera-dev02` | **restored from backup**, else created empty | main catalog, `$ConnectionStrings_DB_Name` |
| `vera-caregivers-dev02` | **restored from backup**, else created empty | `$ConnectionStrings_CaregiverContext_DB_Name` |
| `vera-identity-dev02` | **restored from backup**, else created empty | `$ConnectionString_DB_IdentityServer` |

**Order is what makes this safe.** The restore runs first and has its chance at every data-bearing
database; only then does the create-if-missing step run, so an empty `CREATE DATABASE` can never
mask a backup. And because `mssql-restore.sh` treats *exists but has no user tables* as restorable,
adding a backup later still works — the empty shell does not block it forever.

```
restoring data-bearing databases from /var/opt/mssql/backups
  [restore] vera-dev02  <-  vera-dev02-31JAN2024-cleaned.bak
  [ok]      vera-dev02 ONLINE (2 file(s) relocated to /var/opt/mssql/data)
  [skip]    vera-caregivers-dev02 - no backup matching vera-caregivers-dev02*.bak
  [skip]    vera-identity-dev02 already exists with data (312 tables) - not restored
[create]  MaveraInboxOutbox (empty)
[restored] vera-dev02 (287 tables)
[create]  vera-caregivers-dev02 (empty - no backup restored; $ConnectionStrings_CaregiverContext_DB_Name)
NOTE: 1 data-bearing database(s) have no data. They exist, so services start and EF migrations run,
      but queries for seeded data will fail. Add the backups to Databases.zip and redeploy.
```

An unpopulated database is a **warning, not a failure** — the deploy still succeeds. A restore that
is attempted and *fails*, however, is fatal: `mssql-init` exits non-zero so nothing starts on
half-restored data. A database that already holds data is never touched unless you ask: no `DROP`,
no `REPLACE`, no `ALTER`.

#### Restoring the data-bearing databases (automatic)

The backups ship with this repo as `Databases.zip` and are restored on first deploy. Two containers
do it:

```
Databases.zip  ──mssql-backups-init──▶  mssql-backups volume
                  (alpine, unzip)              │
                        :ro /var/opt/mssql/backups ─┬─▶ sqlserver   (opens the .bak)
                                                   └─▶ mssql-init  (finds the .bak)
```

The `mssql-backups` volume is mounted at the **same path in both containers**, and both mounts are
needed: `mssql-init` globs the directory to find the backups, and the `sqlserver` process is what
actually opens them, so the paths that go into the `RESTORE` statement must resolve on both sides.

For each of the three databases, `mssql-restore.sh`:

1. finds the newest `<db>*.bak` in `/var/opt/mssql/backups`, and **skips** if there is none;
2. decides what to do from the database's state — so redeploys cost one query each:

   | State | Action |
   |---|---|
   | absent | restore |
   | exists, no user tables (or not `ONLINE`) | restore `WITH REPLACE` — it is an empty shell |
   | exists, has tables | **skip**, unless `SQL_RESTORE_FORCE=true` |

3. reads the logical file names with `RESTORE FILELISTONLY` and builds one `MOVE` clause per file.

Step 3 is the part worth knowing about. `MOVE` is mandatory because the backups carry Windows paths
(`C:\Program Files\...`) that do not exist on Linux, and the logical names cannot be hardcoded
because they do not match the database names:

| Backup | Database | Logical data file | Logical log file |
|---|---|---|---|
| `vera-dev02-*.bak` | `vera-dev02` | `vera2` | `vera2_log` |
| `vera-caregivers-dev02-*.bak` | `vera-caregivers-dev02` | `vera-caregivers-dev02` | `vera-caregivers-dev02_log` |
| `vera-identity-dev02_*.bak` | `vera-identity-dev02` | `vera-identity-test` | `vera-identity-test_log` |

Files land as `/var/opt/mssql/data/<database>_<logical>.<mdf|ndf|ldf>`.

`FILELISTONLY` is parsed as text rather than via `INSERT ... EXEC` into a temp table: that result set
gains columns between SQL Server versions, so a fixed-shape temp table would break on an image bump.

**Refreshing the data.** Replace `Databases.zip`, then run one deploy with `SQL_RESTORE_FORCE=true`
and set it back to `false`. This is destructive — it restores `WITH REPLACE` over the live databases.
`SQL_RESTORE_ENABLED=false` turns the whole step off, which is what you want when `DB_SERVER` points
at an existing SQL instance.

**Adding a database.** Put its `.bak` in the zip named `<database>*.bak`, and add the database to
`RESTORE_DATABASES` in `config/mssql-restore.sh` (and to `@expected` in `00-init-databases.sql` if you
want it reported).

Restoring under the **original** source names means any 3-part reference inside the data (views, procs,
synonyms) keeps resolving. If you restore under different names, repoint the three placeholders in
`x-placeholders` to match.

Backups from SQL Server 2019 (major 15) restore cleanly onto this 2022 image; compatibility levels 110
and 150 both remain supported.

The extracted backups stay in the `mssql-backups` volume (~300 MB) so a rebuilt `sqlserver-data`
volume does not need the zip unpacked again. Reclaim it with `docker volume rm mavera_mssql-backups`.

A `.sql` dump works too: any file dropped into `config/mssql-init/` is applied in filename order, so
name it `10-something.sql` to run after the bootstrap.

### 2. `GH_PAT` is embedded in the build-context URL

Build contexts are `https://x-access-token:${GH_PAT}@github.com/MaveraDSS/<repo>.git#${BRANCH}`, and
BuildKit records that URL in build history. Use a **fine-grained, read-only, short-lived** PAT scoped to
these repos (or a GitHub App installation token) and rotate it. Removing this exposure is the main reason
to switch to pull-only — see below.

### 2b. Which branch each repo is built from

`BRANCH` is the branch built for every service repo that has it — currently
`release/v.be-2026-04-01`, which 26 of the 29 repos carry.

Three do not, and their build contexts read `BRANCH_FALLBACK` (`develop`) instead:
`mavera-identity-server`, `mavera-news-manager`, `mavera-audit`. Compose has no conditionals, so
the split is expressed as two knobs rather than resolved at deploy time. Re-check membership
before bumping `BRANCH` — the command is in the `docker-compose.yml` header and in DEPLOY.md's
*Ongoing* section — and move any repo that has caught up back onto `${BRANCH}`.

`TAG` is separate and names the locally built images. It is `release-v.be-2026-04-01`, dashed
because Docker tags cannot contain `/`.

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

Apryse (`APRYSE_LICENSE_KEY`), mail, SMS, SMB, Kuralink and the crypto keys are empty by default.
Flows depending on them will not work. OCR and PDF generation in particular need a valid Apryse licence.

**Okta is the exception on `release/v.be-2026-04-01` — it is no longer optional.** That branch
replaces the shared-secret internal auth (`$InternalServicesSettings_Secret`, `$ServiceSettings_EnvURL`)
with Okta client credentials across ~24 services: they build a discovery document as
`https://$Okta_Domain/oauth2/$Okta_AuthServerId` and exchange `$Okta_InternalServices_ClientId` /
`$Okta_InternalServices_Secret` for an `internalapi` token. So `OKTA_DOMAIN`,
`OKTA_AUTH_SERVER_ID`, `OKTA_INTERNAL_CLIENT_ID` and `OKTA_INTERNAL_SECRET` must be set for the
`platform` profile to function. Infrastructure-only deploys still come up without them.

`INTERNAL_SERVICES_SECRET` must stay populated too — the three repos on `BRANCH_FALLBACK` are
still on the pre-Okta scheme.

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

Run against Docker 29.1.3 on 2026-09-03, infrastructure plus `mavera-audit`. This predates the
switch to `release/v.be-2026-04-01`, so the image tag below is the `develop` one that was actually
built; the contract each row demonstrates is unchanged by the branch switch.

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

### The Dokploy Application topology is not verified at all

Everything under *Topology on Dokploy* is a design, not a report. What has been checked:

| Check | Result |
|---|---|
| `docker-compose.infra.yml` parses and resolves | ✅ `docker compose config` — 12 services, 7 volumes, 1 external network |
| `rabbitmq` keeps both aliases on `dokploy-network` | ✅ renders as `[rabbitmq, rabbitmq.rabbitmq.svc.cluster.local]` |
| `generate-manifest.py` output | ✅ 29 applications, 170 env keys each, 26 on `release/v.be-2026-04-01` and 3 on `develop`, ports all 80 |
| `provision.py` payloads for all 29, both roles | ✅ `--selftest`, no network calls |
| Inline entrypoint args carry the script verbatim | ✅ `Command: ["/bin/sh"]`, `Args: ["-c", <44-line script ending in `exec dotnet "${APP_DLL}"`>]` |
| Dokploy behaviour the design depends on | read out of the Dokploy source (`resolveServiceNetworks`, `mechanizeDockerContainer`, `deployPreviewApplication`, `generateFileMounts`, the GitHub webhook handler), **not** observed on a running instance |

Nothing has been deployed. In particular these three are unproven against a real Dokploy and should be
the first things checked, in this order:

1. **Run Command really overrides the image ENTRYPOINT, and a multi-line `args` element survives
   the round trip through Dokploy.** The log line
   `[entrypoint] appsettings.json rendered; starting <APP_DLL>` is the proof. If it does not,
   `--entrypoint-mode bind` is the fallback and exercises a much more conventional code path.
2. **Both alias forms resolve** from one Application to another — `<svc>` *and*
   `<svc>.svc.cluster.local`. If they do not, service discovery is broken fleet-wide.
3. **A running preview does not claim a production alias.** `getent hosts <svc>.svc.cluster.local`
   must return exactly one address while a preview is up.

`provision.py` also checks its required endpoints and fields against the instance's own OpenAPI
document before it writes anything, so a Dokploy version that does not expose `networkSwarm` or
`command` fails loudly rather than provisioning something that cannot work.

## Verifying a local run

This is the all-in-one `docker-compose.yml` path. For the Dokploy environment, where the apps are
Swarm services and `docker compose exec` does not reach them, use **DEPLOY.md Phase 10** instead.

```bash
# 0. the infra file the Dokploy environment actually uses, checked at the same time
docker compose -f docker-compose.infra.yml config >/dev/null && echo "infra config OK"

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

### Deployed environment (Dokploy)

| File | Purpose |
|---|---|
| `docker-compose.infra.yml` | The 12 infrastructure/init containers, on `dokploy-network` |
| `docker-compose.apps.yml` | The 29 .NET services, on `dokploy-network`, with both DNS alias forms |
| `scripts/dokploy/generate-manifest.py` | Derives `build/dokploy/manifest.json` + one resolved env block per app from `docker-compose.yml` |
| `scripts/dokploy/provision.py` | Creates the preview-host Applications over the API. Dry run by default; `--inspect` and `--verify-only` show what Dokploy actually stored |
| `scripts/dokploy/test_provision.py` | Offline checks for project/environment resolution and the existing-application lookup |
| `scripts/dokploy/test_schema.py` | Offline checks for the OpenAPI-driven required-field handling. `--spec <file>` checks against a spec dumped from your own instance |
| `scripts/dokploy/test_verify.py` | Offline checks for the read-back verifier, including the nixpacks and preview-alias-hijack regressions |
| `scripts/dokploy/test_aliases.py` | Offline checks for the placeholder case-alias generation (`--case-aliases`, not the default path) |
| `scripts/dokploy/test_entrypoint.sh` | Exercises `config/entrypoint.sh`: overlay rendering, case reconciliation, unset reporting, hard failures |
| `build/dokploy/` | Generated, gitignored. The env blocks hold real secrets |

### Local

| File | Purpose |
|---|---|
| `docker-compose.yml` | All-in-one stack: 12 infra/init containers + 29 services. Also the source of truth for configuration — the two files above are generated from it, and `generate-manifest.py` reads it |

### Shared by both

| File | Purpose |
|---|---|
| `.env.example` | Every knob, documented |
| `Databases.zip` | Backups of the three data-bearing SQL databases, restored on first deploy |
| `config/entrypoint.sh` | envsubst wrapper for **every** `appsettings*.json`. Bind-mounted by Compose locally; sent inline as container args by `provision.py` on Dokploy |
| `config/otel-collector.yaml` | OTLP gRPC → Seq |
| `config/mssql-restore.sh` | Restores the three data-bearing databases; leaves a populated one alone |
| `config/mssql-init/00-init-databases.sql` | The 4 empty SQL catalogs, and a report on all 7 |
| `config/mongo-init.js` | App user + 11 Mongo databases |
| `config/minio-init.sh` | Buckets |
