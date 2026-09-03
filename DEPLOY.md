# Deploying the Mavera stack to Dokploy on AWS

Step-by-step, from an empty AWS account to 29 services running behind one gateway domain.

Two credentials are involved and they are **not** the same thing — mixing them up is the most common
setup mistake:

| Credential | Used by | For |
|---|---|---|
| **Dokploy ↔ GitHub connection** | Dokploy | cloning *this* repo (the compose file) |
| **`GH_PAT`** env var | BuildKit, during the build | cloning the *29 service* repos, which are the build contexts |

---

## Phase 0 — Prerequisites

- An AWS account, and an SSH key pair in the target region.
- A DNS zone you can add records to (for the gateway domain).
- A GitHub account with read access to the 29 `MaveraDSS/*` service repos.
- SQL Server `.bak` files for `vera-dev02`, `vera-caregivers-dev02`, `vera-identity-dev02`
  (Phase 7 — the stack deploys without them, services just fail on first query).

---

## Phase 1 — Push this repo to GitHub

Dokploy deploys *from a git repo*, so this folder has to become one.

```bash
cd /path/to/mavera-compose

git init -b main
git add .
git commit -m "Mavera fleet compose stack for Dokploy"

# private is recommended, though the repo holds no secrets
gh repo create MaveraDSS/mavera-compose --private --source=. --push
```

Confirm `.env` is **not** committed — `.gitignore` already excludes it. All real values are supplied
through the Dokploy UI in Phase 5.

```bash
git ls-files | grep -x '.env' && echo "STOP: .env is tracked" || echo "OK: .env is not tracked"
```

---

## Phase 2 — Provision the AWS VM

### Instance

| Setting | Value | Why |
|---|---|---|
| AMI | Ubuntu 22.04 or 24.04 LTS | Dokploy-supported |
| Architecture | **x86_64 / amd64** | mandatory — see below |
| Type | `m6i.2xlarge` (8 vCPU / 32 GB) | `t3.xlarge` (4/16) is the floor |
| Storage | 200 GB gp3 | 100 GB minimum |

**Do not use Graviton (`t4g`, `m7g`).** `mavera-ocr` publishes with `-r linux-x64 --self-contained true`
and ships `libPDFNetC.so`, an ELF64 x86-64 binary. It cannot build or run on arm64.

Avoid burstable `t3`/`t3a` if you can: CPU credits drain during the first build of 29 .NET services.

### Security group

| Port | Source | Purpose |
|---|---|---|
| 22 | your IP only | SSH |
| 3000 | your IP only | Dokploy UI |
| 80 | 0.0.0.0/0 | Traefik HTTP + ACME challenge |
| 443 | 0.0.0.0/0 | Traefik HTTPS |

Nothing else. Every infrastructure container in this stack publishes **no host ports** — SQL Server,
Mongo, RabbitMQ, Redis, MinIO and Seq are reachable only on the internal compose network.

### Confirm the architecture before going further

```bash
ssh ubuntu@<vm-ip>
uname -m        # must print x86_64
free -g         # confirm your RAM
df -h /         # confirm your disk
```

### Add swap (recommended)

AWS AMIs ship without swap, so a memory spike during the build has no cushion.

```bash
sudo fallocate -l 8G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

---

## Phase 3 — Install Dokploy

```bash
curl -sSL https://dokploy.com/install.sh | sh
```

It installs Docker if absent, and sets up Traefik plus the `dokploy-network` that this compose file
joins. Then open:

```
http://<vm-ip>:3000
```

Create the administrator account on the first-run page.

### Verify the host can build remote git contexts

This stack builds from git URLs, which needs a Compose version that resolves them (verified working on
Linux Compose v5.5.0):

```bash
docker compose version     # v5.x
docker buildx version      # BuildKit — required for the COPY ../ idiom
docker network ls | grep dokploy-network
```

---

## Phase 4 — Create the Compose application

In the Dokploy UI:

1. **Create → Compose**.
2. **General → Provider**: choose **GitHub** and complete the GitHub connection when prompted (it
   installs a GitHub App). Select `MaveraDSS/mavera-compose`, branch `main`.
   *Alternative:* provider **Git** with the SSH URL and a deploy key, if you would rather not install
   the app.
3. **Compose Type**: **`Docker Compose`** — **not `Stack`**. Swarm mode does not support `build`, and
   all 29 services build from source.
4. **Compose Path**: `./docker-compose.yml`
5. Save. **Do not deploy yet** — environment variables come first.

---

## Phase 5 — Environment variables

Open the **Environment** tab and paste the contents of `.env.example`, then fill in the values below.

Dokploy writes this to a `.env` file next to the compose file. It does **not** inject those variables
into containers directly — this compose file reads them with `${VAR}` interpolation, which is the
supported pattern, so nothing extra is needed.

### Must be set

| Variable | Value | Notes |
|---|---|---|
| `GH_PAT` | a GitHub token | **read-only**, scoped to the 29 `MaveraDSS/*` repos. This is the build-context credential, not the Dokploy connection. |
| `GATEWAY_HOST` | e.g. `mavera.example.com` | public gateway domain |
| `IDENTITY_HOST` | e.g. `identity.example.com` | identity server domain |
| `DB_PASS` | strong password | **must not contain `$`** — config is rendered with `envsubst` |
| `MONGO_ROOT_PASS`, `MONGO_PASS` | strong passwords | same `$` rule |
| `RABBITMQ_PASS`, `MINIO_PASS`, `SEQ_ADMIN_PASS` | strong passwords | same `$` rule |

### Leave at the defaults

| Variable | Default | Why |
|---|---|---|
| `COMPOSE_PARALLEL_LIMIT` | `2` | **29 concurrent `dotnet build` runs will OOM the VM.** The likeliest cause of a failed first deploy. |
| `COMPOSE_PROFILES` | *(empty)* | empty = infrastructure only. Phase 6 relies on this. |
| `MSSQL_MEMORY_LIMIT_MB` | `2048` | caps SQL Server so it cannot starve the 29 services. Raise to ~6144 on a 32 GB box once things are stable. |
| `BRANCH` | `develop` | the branch built for all 29 service repos |

### Generating the `GH_PAT`

A fine-grained token, **Contents: Read-only**, scoped to the 29 repos, with a short expiry.
It is embedded in the build-context URL and therefore recorded in BuildKit history — treat it as
disposable and rotate it. (Removing this exposure is the reason to move to pull-from-ACR later; see
the README.)

### Secrets you can leave blank for now

`OKTA_*`, `APRYSE_LICENSE_KEY*`, `MAIL_*`, `SMS_*`, `SMB_*`, `KURALINK_*`, `CRYPTO_*`. Flows depending
on them will not work; OCR and PDF generation in particular need a valid Apryse licence.

---

## Phase 6 — First deploy: infrastructure only

With `COMPOSE_PROFILES` empty, press **Deploy**. This starts 8 containers and 3 init jobs and builds
nothing, so it should finish in a couple of minutes.

Expected result — via SSH on the VM:

```bash
cd /etc/dokploy/compose/<app-name>/code     # Dokploy shows the exact path in the UI
docker compose ps
```

| Service | Expected |
|---|---|
| `sqlserver`, `mongo`, `rabbitmq`, `redis`, `minio` | `Up (healthy)` |
| `seq`, `otel-collector`, `gotenberg` | `Up` |
| `mssql-init`, `mongo-init`, `minio-init` | `Exited (0)` |

Check the bootstrap output:

```bash
docker compose logs mssql-init
#   capping max server memory at 2048 MB
#   [create]  MaveraInboxOutbox (empty)          ... x4
#   [MISSING] vera-dev02 - restore it manually   ... x3   <- expected at this point
```

`[MISSING]` is a warning, not a failure. Fix it in the next phase.

---

## Phase 7 — Restore the SQL databases (one-off)

Three databases hold real data and are restored by hand, once. The init script deliberately never
creates them, so your `RESTORE` does not need `WITH REPLACE`.

`RESTORE` runs inside the SQL Server process, so the file must be inside that container.

```bash
# 1. copy the backup onto the VM, then into the container
scp mybackup.bak ubuntu@<vm-ip>:/tmp/
docker compose cp /tmp/mybackup.bak sqlserver:/var/opt/mssql/data/

# 2. read the LOGICAL file names — they do NOT match the database name
#    (vera-dev02's data file is logically "vera2"; vera-identity-dev02's is "vera-identity-test")
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASS" \
  -Q "RESTORE FILELISTONLY FROM DISK='/var/opt/mssql/data/mybackup.bak'"

# 3. restore, relocating every file with MOVE (mandatory: the backups carry
#    Windows paths like C:\Program Files\... that do not exist on Linux)
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASS" -b \
  -Q "RESTORE DATABASE [vera-dev02] FROM DISK='/var/opt/mssql/data/mybackup.bak' WITH RECOVERY, STATS=25,
       MOVE 'vera2'     TO '/var/opt/mssql/data/vera-dev02_1.mdf',
       MOVE 'vera2_log' TO '/var/opt/mssql/data/vera-dev02_2.ldf'"

# 4. clean up, repeat for the other two, then confirm
docker compose exec sqlserver rm /var/opt/mssql/data/mybackup.bak
docker compose up -d --force-recreate mssql-init && docker compose logs mssql-init
#   [present] vera-dev02 (restored)   x3
```

Restore under the **original** names so any 3-part reference inside the data (views, procs, synonyms)
keeps resolving. Backups from SQL Server 2019 restore cleanly onto this 2022 image.

If you would rather point at an existing dev SQL instance, set `DB_SERVER` to its hostname instead and
skip this phase entirely.

---

## Phase 8 — Deploy the services, profile by profile

Do **not** jump straight to `all`. One group at a time keeps peak memory down and makes any failure
attributable.

For each step: set `COMPOSE_PROFILES` in the Environment tab, press **Deploy**, wait, then check
`docker compose ps` for restart loops before moving on.

| Step | `COMPOSE_PROFILES` | Services | Notes |
|---|---|---|---|
| 1 | `platform` | 13 | identity server, user service, gateway, audit, … |
| 2 | `platform,evaluation` | +3 | evaluation service, EPV, fkassan |
| 3 | `platform,evaluation,documents` | +5 | document/storage/OCR/PDF/file-conversion |
| 4 | `all` | 29 | adds the 8 AI orchestrators |

**The first build is long.** Five .NET SDK majors (6.0, 7.0, 8.0, 9.0, 10.0) in alpine *and* Debian
variants get pulled, and 29 projects compile. Budget an hour or more for step 1, then much less —
BuildKit caches git contexts by resolved commit, so unchanged repos are not rebuilt.

> **Never** run `docker builder prune`, and do not put it in a cron job. It wipes the cache and forces
> a full 29-service rebuild on the next deploy.

---

## Phase 9 — Domains and TLS

1. Point DNS at the VM's public IP:

   ```
   mavera.example.com     A   <vm-ip>
   identity.example.com   A   <vm-ip>
   ```

2. In Dokploy → **Domains**, add a domain for service **`mavera-libertine`**, container port **80**,
   and enable HTTPS / Let's Encrypt. Repeat for **`mavera-identity-server`**.

   Letting the UI own the domain means Dokploy manages the certificate. The compose file already
   carries working HTTP Traefik labels and both services join `dokploy-network`; commented `websecure`
   labels are in the file if you would rather declare TLS yourself (match your own `certResolver`).

`mavera-libertine` is a YARP gateway whose routing table configures itself from the same placeholder
set, so it fronts the FE-facing surface with no extra wiring. The other 27 services stay internal.

---

## Phase 10 — Verify

```bash
# 1. everything is up, nothing restarting
docker compose ps

# 2. the in-cluster DNS trick everything depends on
docker compose exec mavera-audit getent hosts rabbitmq.rabbitmq.svc.cluster.local
docker compose exec mavera-audit getent hosts mavera-user-service.svc.cluster.local

# 3. config really rendered — no "$" literals should appear
docker compose exec mavera-audit sh -c 'grep -E "Host|Endpoint" /app/appsettings.json'

# 4. message bus connected: queues should exist
docker compose exec rabbitmq rabbitmqctl list_queues name messages

# 5. telemetry flowing (accepted == sent, no failures)
docker run --rm --network <project>_mavera curlimages/curl:latest -s \
  http://otel-collector:8888/metrics | grep -E 'otelcol_(receiver_accepted|exporter_sent)_spans'

# 6. the gateway answers
curl -I https://mavera.example.com
```

Logs for all services land in **Seq** — add a Dokploy domain for the `seq` service (port 80) and log in
as `admin` with `SEQ_ADMIN_PASS`.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Build fails `failed to evaluate path "https://…"` | Compose is resolving the git URL as a local path. Known on **Windows** Compose; the Linux host is fine. Check `docker compose version`. |
| Build fails cloning a service repo | `GH_PAT` missing, expired, or not scoped to that repo. |
| VM OOMs mid-build | `COMPOSE_PARALLEL_LIMIT` was raised or dropped. Set it back to `2` and deploy one profile at a time. |
| `Stack` selected instead of `Docker Compose` | Swarm does not support `build`; recreate the app with the right Compose Type. |
| Config arrives as literal `$Placeholder` | The entrypoint was overridden, or a variable is missing from `x-placeholders`. `docker compose logs <svc>` prints any unrendered names. |
| `UriFormatException` at startup | `Tracing_Connection_String` empty or not absolute. It must be `http://otel-collector:4317`. |
| `Name or service not known` for `*.svc.cluster.local` | The target service is not running, or lost its network alias. |
| `Invalid object name 'dbo.X'` | The database is empty — Phase 7 not done, or `DB_SERVER` points somewhere unpopulated. |
| Deploy stalls on `Pulling … unauthorized` | `pull_policy: build` was removed from `x-service-base`; Compose is trying ACR instead of building. |
| SQL Server eating all RAM | `MSSQL_MEMORY_LIMIT_MB` only applies at first-run setup; `mssql-init` re-applies it via `sp_configure` on every run — re-run it. |
| `storage-service` / `mavera-ocr` fail on queries | `MaveraStorageOperations` / `MaveraOcrOperations` are empty; no backup was supplied for them. |

---

## Ongoing

- **Redeploys** rebuild only service repos whose branch head moved. Keep the build cache.
- **Webhooks**: Dokploy can auto-deploy on push to this repo. Note that it re-clones the compose repo,
  not the service repos — those are re-resolved by BuildKit at build time.
- **Backups**: named volumes (`sqlserver-data`, `mongo-data`, `minio-data`, `rabbitmq-data`,
  `redis-data`, `seq-data`) support Dokploy's scheduled S3 volume backups.
- **The upgrade worth making**: have `MaveraDSS/ci-template` push a moving tag (e.g. `develop`)
  alongside its git-SHA tags. Then set `pull_policy: always`, delete the `build:` blocks, add ACR
  registry credentials, and deploys become a `docker compose pull` — minutes instead of an hour, and
  no `GH_PAT` in build history.
