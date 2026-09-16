# Deploying the Mavera stack to Dokploy on AWS

Step-by-step, from an empty AWS account to 29 services running behind one gateway domain, with
pull-request preview deployments on the repos that want them.

Infrastructure is deployed as one Dokploy Compose service; each application service is its own Dokploy
Application. README *Topology on Dokploy* explains why, and the reasoning matters for phases 4 and 8.

Two credentials are involved and they are **not** the same thing — mixing them up is the most common
setup mistake:

| Credential | Used by | For |
|---|---|---|
| **Dokploy ↔ GitHub connection** | Dokploy | cloning *this* repo for the infra compose file, **and** cloning each of the 29 service repos to build their Applications. Must be the GitHub App, installed on `MaveraDSS` — preview deployments do not work with any other provider. |
| **`GH_PAT`** env var | BuildKit, local builds only | cloning the *29 service* repos as Compose build contexts. Dokploy does not use it: the GitHub App authenticates the clone (Phase 8b). |

---

## Phase 0 — Prerequisites

- An AWS account, and an SSH key pair in the target region.
- A DNS zone you can add records to (for the gateway domain).
- A GitHub account with read access to the 29 `MaveraDSS/*` service repos.
- SQL Server `.bak` files for `vera-dev02`, `vera-caregivers-dev02`, `vera-identity-dev02`
  (Phase 7 — restored automatically from `Databases.zip` in this repo).

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

## Phase 4 — Create the infrastructure compose service

Only infrastructure is deployed as Compose. The 29 application services become separate Dokploy
Applications in Phase 8, which is what gives them preview deployments — see README *Topology on
Dokploy*.

In the Dokploy UI:

1. **Create → Compose**. Name it `mavera-infra`.
2. **General → Provider**: choose **GitHub** and complete the GitHub connection when prompted (it
   installs a GitHub App). Select `MaveraDSS/mavera-compose`, branch `main`.
   *Alternative:* provider **Git** with the SSH URL and a deploy key, if you would rather not install
   the app. Note that Phase 8 **does** require the GitHub App, on the `MaveraDSS` org.
3. **Compose Type**: **`Docker Compose`** — **not `Stack`**. `docker stack deploy` renames services to
   `<appName>_sqlserver`, and the Applications resolve these by their plain names.
4. **Compose Path**: `./docker-compose.infra.yml`
5. **Advanced → Isolated Deployments: off.** It would move the stack onto a private network and off
   `dokploy-network`, which is the one thing this whole topology depends on.
6. Save. **Do not deploy yet** — environment variables come first.

> The old single-app setup used `./docker-compose.yml` with `COMPOSE_PROFILES`. That file is still in
> the repo and still works for local development, but it is not what Dokploy deploys any more.

---

## Phase 5 — Environment variables

Paste the contents of `.env.example` into the **Environment** tab and fill in the values below — then
**turn on env-file generation** in that tab, or Compose will not see any of them. See
*Turn on env-file generation* below.

### Must be set

| Variable | Value | Notes |
|---|---|---|
| `GH_PAT` | *(leave blank on Dokploy)* | only needed for local `docker compose` builds. On Dokploy the GitHub App clones the service repos (Phase 8b), so nothing reads this. |
| `GATEWAY_HOST` | e.g. `mavera.example.com` | public gateway domain — see *Choosing the hostnames* |
| `IDENTITY_HOST` | e.g. `identity.example.com` | identity server domain — **must differ from `GATEWAY_HOST`** |
| `PUBLIC_SCHEME` | `http` or `https` | `https` once the domains have certificates. Feeds `IdentityServer_IssuerUri`. |
| `DB_PASS` | strong password | **must not contain `$`** — config is rendered with `envsubst` |
| `MONGO_ROOT_PASS`, `MONGO_PASS` | strong passwords | same `$` rule |
| `RABBITMQ_PASS`, `MINIO_PASS`, `SEQ_ADMIN_PASS` | strong passwords | same `$` rule |
| `OKTA_DOMAIN`, `OKTA_AUTH_SERVER_ID` | your Okta org + auth server | see *Okta is now required* below |
| `OKTA_INTERNAL_CLIENT_ID`, `OKTA_INTERNAL_SECRET` | Okta app credentials | the client-credentials pair for the `internalapi` scope |
| `INTERNAL_SERVICES_SECRET` | shared secret | still read by the three repos on `BRANCH_FALLBACK` |

#### Okta is now required

`release/v.be-2026-04-01` moves service-to-service authentication from a shared secret to Okta
client credentials. About 24 of the services build their discovery document as
`https://$OKTA_DOMAIN/oauth2/$OKTA_AUTH_SERVER_ID` and exchange
`OKTA_INTERNAL_CLIENT_ID` / `OKTA_INTERNAL_SECRET` for an `internalapi` token on every call.

Leaving them blank still lets Phase 6 (infrastructure only) come up, but every cross-service call
in the `platform` profile fails token acquisition — the symptom is a 401 from the *downstream*
service and a token-endpoint error in Seq, not a startup crash.

`INTERNAL_SERVICES_SECRET` cannot be dropped: `mavera-identity-server`, `mavera-news-manager` and
`mavera-audit` build from `develop` (see `BRANCH_FALLBACK`) and still read the pre-Okta secret.

### Leave at the defaults

| Variable | Default | Why |
|---|---|---|
| `COMPOSE_PARALLEL_LIMIT` | `2` | local only. The infra stack builds nothing, and Dokploy builds the Applications one at a time. |
| `COMPOSE_PROFILES` | *(empty)* | local only. The infra stack has no profiles; every service in it always starts. |
| `MSSQL_MEMORY_LIMIT_MB` | `2048` | caps SQL Server so it cannot starve the 29 services. Raise to ~6144 on a 32 GB box once things are stable. |
| `SQL_RESTORE_ENABLED` | `true` | restores the three data-bearing databases from `Databases.zip` on first deploy (Phase 7) |
| `SQL_RESTORE_FORCE` | `false` | **destructive.** `true` re-restores over the live databases on every deploy |
| `BRANCH` | `release/v.be-2026-04-01` | the branch built for the 26 service repos that have it |
| `BRANCH_FALLBACK` | `develop` | used by the three repos that have not cut that branch: `mavera-identity-server`, `mavera-news-manager`, `mavera-audit` |
| `TAG` | `release-v.be-2026-04-01` | names the locally built images; Docker tags cannot contain `/` |

### Turn on env-file generation — this is the step that trips people up

Paste the contents of `.env.example` into the **Environment** tab, fill in the values, and **enable the
switch that generates the environment file** (in the same tab). Without it Dokploy holds the variables
but never writes the `.env` next to the compose file, so Compose has nothing to interpolate from and
every required variable fails:

```
error while interpolating services.mavera-ai-client-relevance-score.environment.BucketProviderSecret:
  required variable MINIO_PASS is missing a value: MINIO_PASS is not set
```

With the switch on, the file appears in the application's directory alongside `docker-compose.yml`,
Compose picks it up automatically, and no extra flags are needed.

Confirm it on the VM after saving:

```bash
cd /etc/dokploy/compose/<app-name>/code
ls -la .env && grep -E '^(GATEWAY_HOST|IDENTITY_HOST|MINIO_PASS)=' .env
docker compose config >/dev/null && echo "interpolation OK"
```

Only the ~15 infrastructure knobs are actually read by `docker-compose.infra.yml` (`DB_PASS`, the
`MONGO_*`, `RABBITMQ_*` and `MINIO_*` pairs, `BUCKET_ASSETS`, `BUCKET_DOCUMENTS`, `SEQ_ADMIN_PASS`,
`MSSQL_MEMORY_LIMIT_MB`, `SQL_RESTORE_ENABLED`, `SQL_RESTORE_FORCE`, `SQLSERVER_IMAGE`). Paste the whole
file anyway: `generate-manifest.py` reads the same `.env` in Phase 8c to resolve the 169 placeholders,
and keeping one copy is what stops the two halves drifting.

#### Fallback: an env file you control

If the generated file still lands in the wrong directory — [Dokploy #2777](https://github.com/Dokploy/dokploy/issues/2777)
reports it being written to `/etc/dokploy/compose/<app>/.env` while Compose runs with cwd
`/etc/dokploy/compose/<app>/code/`, because the path is derived from a `composePath` that can go stale —
keep the file at a fixed absolute path **outside** the cloned directory, where the per-deploy `git clone`
cannot clear it, and point Compose at it explicitly:

```bash
sudo install -d -m 700 /etc/dokploy/env
sudo cp /etc/dokploy/compose/<app-name>/code/.env.example /etc/dokploy/env/mavera.env
sudo chmod 600 /etc/dokploy/env/mavera.env
sudo nano /etc/dokploy/env/mavera.env
```

Then Dokploy → **Advanced → Custom Command** (it *fully replaces* the default, so include every flag):

```
compose -p mavera --env-file /etc/dokploy/env/mavera.env -f docker-compose.yml up -d --build --remove-orphans
```

Verified: an absolute `--env-file` satisfies interpolation with no `.env` in the project directory, that
flag ordering is accepted, `COMPOSE_PROFILES` is honoured from the file, and the values reach both the
infrastructure containers and the placeholder mapping. The cost is that changes are made over SSH rather
than in the UI.

#### Why `env_file:` is not the answer

Dokploy's docs offer `env_file: [.env]` as an alternative to `${VAR}` interpolation. It does not work
here: `env_file` injects the *knob* names (`MINIO_PASS`, `DB_PASS`) into containers, but the services
read the *placeholder* names (`BucketProviderSecret`, `ConnectionStrings_DB_Pass`). The mapping happens
in `x-placeholders` via interpolation, so Compose must resolve the values at parse time.

### Choosing the hostnames

**Do not use the EC2 public DNS name** (`ec2-1-2-3-4.eu-north-1.compute.amazonaws.com`) for these.
Three reasons, the first of which is a hard blocker:

1. **They must be two different hostnames.** Each becomes a Traefik `Host()` rule. Give both the same
   value and the gateway and identity routers match identically, so requests land on whichever wins —
   unpredictably. You cannot create `identity.ec2-…compute.amazonaws.com`, because you do not control
   that DNS zone.
2. **Let's Encrypt refuses to issue for `*.compute.amazonaws.com`** by policy — the ACME server returns
   *"forbidden by policy"*. So Dokploy's HTTPS toggle cannot work and you are stuck on HTTP.
3. **The name changes on stop/start** without an Elastic IP. `IDENTITY_HOST` feeds
   `IdentityServer_IssuerUri`, which is the `iss` claim in every token and the discovery document — if it
   changes, every previously issued token fails validation. This one fails silently, later.

| Situation | Use |
|---|---|
| Proper deployment | a domain you own + an **Elastic IP**: `mavera.dev.example.com` / `identity.dev.example.com`, `PUBLIC_SCHEME=https` |
| No DNS zone, quick test | wildcard DNS: `mavera.<ip-with-dashes>.sslip.io` / `identity.<ip>.sslip.io` — two distinct names off one IP, and Let's Encrypt will issue for them |
| Smoke test only | skip domains entirely, leave the defaults, and tunnel: `ssh -L 8080:localhost:80 ubuntu@<vm>`. Phases 06–08 and 10 all work without a domain. |

Use an Elastic IP either way — it is free while attached and removes reason 3.

#### Setting up the DNS records

Two **A records**, both pointing at the VM's **Elastic IP**. Using `example.com` as your domain:

| Type | Name / Host | Points to | TTL |
|---|---|---|---|
| A | `mavera` | `<elastic-ip>` | 300 |
| A | `identity` | `<elastic-ip>` | 300 |

giving `mavera.example.com` (gateway) and `identity.example.com` (identity server).

Registrar gotchas worth knowing:

- **The Name field takes the label only** — enter `mavera`, not `mavera.example.com`. Most panels
  (Hostinger, Namecheap, GoDaddy) append the domain for you, so the full name produces
  `mavera.example.com.example.com`. Some panels show the resulting FQDN as you type; check it.
- **Records must live wherever the nameservers point.** If the domain still uses the registrar's default
  nameservers, add them in the registrar's DNS panel. If you have pointed it at Cloudflare, Route 53 or
  anywhere else, the registrar's DNS panel is ignored — add them there instead.
- **A record, not CNAME.** A CNAME to the EC2 public DNS would resolve, but it reintroduces the
  stop/start problem from reason 3 above.
- **Keep TTL low (300) while setting up**, so a typo costs five minutes rather than a day. Raise it later.
- If you later put **Cloudflare** in front, leave the proxy off (grey cloud) until Let's Encrypt has
  issued, or use DNS-01 — a proxied record can interfere with the HTTP-01 challenge.

Verify before touching Dokploy — both must return the Elastic IP:

```bash
dig +short mavera.example.com
dig +short identity.example.com
```

Then set the two variables and leave `PUBLIC_SCHEME=http` until the certificates exist:

```
GATEWAY_HOST=mavera.example.com
IDENTITY_HOST=identity.example.com
PUBLIC_SCHEME=http
```

**Naming, if you expect more environments later.** The flat scheme above is fine for one deployment. If
dev/stage/prod will each get a VM, scope the environment in the name from the start — `mavera.dev`,
`identity.dev`, then `mavera.stage`, `identity.stage` — rather than renaming later. Renaming
`IDENTITY_HOST` changes the token issuer, which invalidates every issued token.

**Optional third record.** If you want the Seq log UI on a domain rather than an SSH tunnel, add
`logs` → `<elastic-ip>` and point a Dokploy domain at the `seq` service on port 80. It is behind Seq's
own admin login (`SEQ_ADMIN_PASS`), but it does expose every service's logs, so weigh that.

### `PUBLIC_SCHEME` and TLS

TLS terminates at Traefik, so the containers themselves only ever speak HTTP. `PUBLIC_SCHEME` is what
the apps *advertise*:

| Variable | With `PUBLIC_SCHEME=https` |
|---|---|
| `IdentityServer_IssuerUri` | `https://identity.example.com` |
| `IdentityServer_PublicOrigin` | `https://identity.example.com` |
| `IdentityServer_PostLogoutRedirectUri` | `https://mavera.example.com` |
| `ServiceSettings_ClientUrl` | `https://mavera.example.com` |

Leave it `http` until the certificates are actually issued, then switch it and redeploy. Setting
`https` before Traefik can serve it gives you an issuer nobody can reach.

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

### Authenticate to Docker Hub first

Anonymous Docker Hub pulls have been capped at **10 per hour per IP** since April 2025, and this stack
pulls 7 Hub images in one go (`redis`, `rabbitmq`, `mongo`, `alpine`, `datalust/seq`,
`otel/opentelemetry-collector-contrib`, `gotenberg/gotenberg`). A free account raises that to 100/hour:

```bash
sudo docker login          # any free Docker Hub account
```

Skip it and the deploy may fail part-way through with `pull access denied` or `toomanyrequests` on
whichever images happen to come last — and then with `No such image: …` on the rest, because one failed
pull aborts the whole `up`. The SQL Server image (`mcr.microsoft.com`) and MinIO (`quay.io`) are not
affected; neither registry rate-limits anonymous pulls this way.

### Deploy

Press **Deploy**. `docker-compose.infra.yml` has no profiles: this starts 8 long-running containers
plus 4 init jobs, and builds nothing. Budget **5–10 minutes** on the first deploy: `mssql-backups-init` unpacks ~300 MB of backups
and `mssql-init` restores all three databases before it exits. Later deploys skip both and take a
couple of minutes.

Expected result — via SSH on the VM:

```bash
cd /etc/dokploy/compose/<infra-app-name>/code   # Dokploy shows the exact path in the UI
docker compose -f docker-compose.infra.yml ps
```

| Service | Expected |
|---|---|
| `sqlserver`, `mongo`, `rabbitmq`, `redis`, `minio` | `Up (healthy)` |
| `seq`, `otel-collector`, `gotenberg` | `Up` |
| `mssql-backups-init`, `mssql-init`, `mongo-init`, `minio-init` | `Exited (0)` |

Check the bootstrap output:

```bash
docker compose logs mssql-backups-init
#   extracting Databases.zip (a1b2c3...)
#   -rw-r--r--  vera-dev02-31JAN2024-cleaned.bak      ... x3

docker compose logs mssql-init
#   capping max server memory at 2048 MB
#   restoring data-bearing databases from /var/opt/mssql/backups
#   [restore] vera-dev02  <-  vera-dev02-31JAN2024-cleaned.bak
#   [ok]      vera-dev02 ONLINE (2 file(s) relocated to /var/opt/mssql/data)   ... x3
#   [create]  MaveraInboxOutbox (empty)          ... x4
#   [restored] vera-dev02 (287 tables)           ... x3
```

Three `[ok]` lines and three `[restored]` lines mean Phase 7 is already done.

`[create] vera-... (empty - no backup restored)` or `[empty]` instead means no matching backup was
found: the database is created anyway so services start and EF migrations run, but anything that
reads seeded data will fail. See Phase 7.

---

## Phase 7 — The SQL databases (automatic)

Nothing to do here on a normal deploy. This phase is reference material for when it goes wrong.

Three databases hold real data: `vera-dev02`, `vera-caregivers-dev02` and `vera-identity-dev02`.
Their backups are committed to this repo as `Databases.zip`, and two containers restore them for you:

| Container | What it does |
|---|---|
| `mssql-backups-init` | Unpacks `Databases.zip` into the `mssql-backups` volume, which `sqlserver` mounts read-only at `/var/opt/mssql/backups`. Re-extracts only when the zip's checksum changes. |
| `mssql-init` | Runs `config/mssql-restore.sh` **before** the catalog scripts: for each database, finds `<db>*.bak` and restores it unless the database already holds data. Mounts the same volume at the same path, because it is the one doing the globbing. |

Two details the restore handles that a hand-written `RESTORE` usually gets wrong:

- **Every file is relocated with `MOVE`.** The backups carry Windows paths
  (`C:\Program Files\...`) that do not exist on Linux, so a plain `RESTORE` fails.
- **Logical names do not match the database names**, and are read at restore time with
  `RESTORE FILELISTONLY` rather than hardcoded — `vera-dev02`'s data file is logically `vera2`,
  and `vera-identity-dev02`'s is `vera-identity-test`.

Safe on every deploy: a database that already holds data is never overwritten, and re-running costs
one query per database.

**What happens when a backup is missing.** `00-init-databases.sql` runs straight after the restore
and creates any of the seven databases that still do not exist, empty. So the stack always ends up
with all seven: services start and EF migrations run, and only queries for seeded data fail. If you
add the backup later, the restore picks it up — an empty database (no user tables) counts as
restorable, so the shell created on an earlier deploy does not block it.

### Refreshing the data from a newer backup

Replace `Databases.zip`, push, then set `SQL_RESTORE_FORCE=true` for one deploy and set it back to
`false` afterwards. **This is destructive** — it restores `WITH REPLACE` over the live databases,
discarding anything written since.

Backups from SQL Server 2019 restore cleanly onto this 2022 image. Keep the original database names,
so any 3-part reference inside the data (views, procs, synonyms) keeps resolving.

### If a restore fails

A failed restore is deliberately fatal: `mssql-init` exits non-zero and no service starts on
half-restored data. Read the log, fix the cause, then re-run just that container:

```bash
docker compose logs mssql-init
docker compose up -d --force-recreate mssql-init && docker compose logs -f mssql-init
```

| Symptom | Cause |
|---|---|
| `[skip] <db> - no backup matching <db>*.bak` | The zip does not contain a file whose name starts with that database name. The database is then created empty by the next step. |
| `no backup directory at /var/opt/mssql/backups` | The `mssql-backups` volume is not mounted into `mssql-init`. Both it and `sqlserver` need it, at that same path. |
| `no Databases.zip in the project directory` | The zip was not committed, or Dokploy cloned before it was pushed. |
| `RESTORE ... could not be opened. Operating system error 5` | Permissions on the backup volume. `sqlserver` runs as root and mounts it read-only; check `docker compose -f docker-compose.infra.yml exec sqlserver ls -la /var/opt/mssql/backups`. |
| Restore runs on every deploy | `SQL_RESTORE_FORCE` was left at `true`. |

### Using an existing SQL instance instead

Point `DB_SERVER` at its hostname and set `SQL_RESTORE_ENABLED=false`. The restore is skipped and
nothing is written to that instance.

---

## Phase 8 — Provision and deploy the 29 Applications

Infrastructure is up and the SQL restore is verified before this starts. Applications have no
`depends_on`, so anything deployed before infrastructure answers will crash-loop until it does.

### 8a. The entrypoint script — nothing to do

Every app image's ENTRYPOINT is `dotnet Foo.dll`, and it has to be replaced with "render the
appsettings template with `envsubst`, then exec dotnet" (README *How configuration works*). Compose did
that by bind-mounting `config/entrypoint.sh`; an Application has no compose file to mount from.

`provision.py` handles it **inline** by default: it reads `config/entrypoint.sh` out of your checkout
and sends it as the container's arguments.

```
Command: ["/bin/sh"]        Args: ["-c", "<the whole script>"]
```

Dokploy's `command` field is split on spaces with no quote handling, which is why the command is the
bare `/bin/sh` — but `args` is a real string array, so the script survives verbatim, newlines and all.
Both fields are inherited by preview deployments.

**So there is nothing to place on the host, and no path to get right.** Skip to 8b.

#### Fallback: bind-mount it from the host instead

Use this if you would rather keep the script out of Dokploy's database, or if inline args ever
misbehave:

```bash
# on the Docker host, over SSH
sudo install -d -m 755 /srv/mavera
sudo tee /srv/mavera/entrypoint.sh < config/entrypoint.sh   # or scp it across
sudo chmod 644 /srv/mavera/entrypoint.sh
head -1 /srv/mavera/entrypoint.sh      # expect: #!/bin/sh
```

```bash
python scripts/dokploy/provision.py --entrypoint-mode bind --apply
# non-default location:
python scripts/dokploy/provision.py --entrypoint-mode bind \
  --entrypoint-host-path /opt/mavera/entrypoint.sh --apply
```

Any absolute path on the Docker host works — `hostPath` goes to Docker verbatim, with no validation,
prefixing or normalisation. **Do not put it under `/etc/dokploy`**: that is Dokploy's own data root, and
it prunes paths inside it (`rm -rf /etc/dokploy/applications/<appName>` on app or preview teardown).

Three things that bite in this mode, and are the reason it is not the default:

- **If the source file does not exist when the container starts, Docker silently creates it as a
  directory**, and the app dies with `/entrypoint.sh: not found`. Check `file /srv/mavera/entrypoint.sh`
  says `POSIX shell script`, not `directory`.
- **On a multi-node Swarm the file must exist on every node** that could schedule these tasks, or you
  need a placement constraint pinning them to the one that has it.
- `644` is deliberate. Dokploy never marks mounted files executable, so the Run Command runs the script
  *through* `/bin/sh` rather than executing it.

> **Running from Git Bash on Windows?** MSYS rewrites `/opt/...`-style arguments into Windows paths
> (`C:/Program Files/Git/opt/...`). Use PowerShell. `provision.py` rejects the mangled value rather than
> writing a broken mount.

#### Where is `/etc/dokploy`, anyway?

You do not need it for the default inline mode, but Phase 6 and Phase 10 reference it, so: on a standard
install it is **always `/etc/dokploy`, on the host**. The path is a hardcoded ternary in
`packages/server/src/constants/index.ts` — `/etc/dokploy` whenever `NODE_ENV=production` or the target
is a remote server — and it is byte-identical from v0.20.0 through v0.30.6. There is **no environment
variable that overrides it**, and it is not a named volume: the installer does

```sh
mkdir -p /etc/dokploy && chmod 777 /etc/dokploy
docker service create --name dokploy \
  --mount type=bind,source=/etc/dokploy,target=/etc/dokploy ...
```

so the container path and the host path are the same string. Under it: `compose/<appName>/code`,
`applications/<appName>/code`, `traefik/`, `logs/`.

If you cannot see it, the cause is almost certainly one of these, in order of likelihood:

1. **You are not on the Docker host.** Dokploy's built-in web terminal and `docker exec` both put you
   inside a *container*. You need a real SSH session to the VM.
2. **Dokploy is on a different machine** than the one you are logged into — a separate Dokploy host, or
   a multi-server setup where the apps run on a remote node. Remote nodes get their own `/etc/dokploy`,
   provisioned over SSH by Dokploy's server setup.
3. **A non-standard install.** Anything other than `curl -sSL https://dokploy.com/install.sh | bash`.

Note it is `chmod 777`, so *permissions* are not the explanation — a root-owned-directory theory does
not fit. Ask Docker rather than guessing:

```bash
# authoritative: the swarm service's declared mounts
docker service inspect dokploy \
  --format '{{range .Spec.TaskTemplate.ContainerSpec.Mounts}}{{.Type}}  {{.Source}} -> {{.Target}}{{"\n"}}{{end}}'
# expect a line:  bind  /etc/dokploy -> /etc/dokploy

# same from the running task, which also catches non-swarm installs
docker inspect "$(docker ps -qf 'name=^dokploy\.')" \
  --format '{{range .Mounts}}{{.Type}}  {{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'
```

Or find it from the clone Dokploy made when you deployed the infra stack in Phase 6:

```bash
sudo find / -name docker-compose.infra.yml -not -path '/proc/*' 2>/dev/null
```

The Dokploy UI also shows the exact path on the Compose service's page.

### 8b. Connect the GitHub App to the org

**Settings → Git Providers → GitHub**, installed on `MaveraDSS` with access to the 29 service repos.
Preview deployments only run for `sourceType: github`, so this is required, not optional. `GH_PAT` is no
longer used for builds.

### 8c. Generate the manifest

From a checkout of this repo, with a `.env` whose required variables are filled in:

```bash
python scripts/dokploy/generate-manifest.py
```

Expect `29 application services`, `170 keys each`, and the 12 infrastructure services listed as left to
`docker-compose.infra.yml`. This reads `docker compose config`, so anything it reports is exactly what
the Compose file says — no second copy of the placeholder list to keep in sync.

`build/dokploy/env/*.env` now holds **resolved secrets**. It is gitignored; keep it that way.

### 8d. Dry run, then one service

```bash
export DOKPLOY_URL=https://dokploy.example.com
export DOKPLOY_API_KEY=...            # Settings -> Profile -> API/CLI
export DOKPLOY_PROJECT=mavera         # your project's name in the Dokploy UI

python scripts/dokploy/provision.py --list
python scripts/dokploy/provision.py --only mavera-audit          # prints every API call
python scripts/dokploy/provision.py --only mavera-audit --apply
```

**The Dokploy project has to exist already** — create it in the UI first; `provision.py` never creates
one. It targets project `mavera`, environment `production` by default. Override either way:

| | flag | env var |
|---|---|---|
| project by name | `--project` | `DOKPLOY_PROJECT` |
| project by id | `--project-id` | `DOKPLOY_PROJECT_ID` |
| environment by name | `--environment` | `DOKPLOY_ENVIRONMENT` |
| environment by id | `--environment-id` | `DOKPLOY_ENVIRONMENT_ID` |

Names are matched exactly first, then case-insensitively. Dokploy does not enforce unique project
names, so if two match you get an error listing their ids rather than a guess at which one you meant.
Every run prints the target it resolved before it writes anything:

```
target: project 'mavera' (proj-abc123) / environment 'production' (env-def456) - 0 existing application(s)
```

Get the name wrong and it tells you what is actually there:

```
no project named 'mavera'. Available:
  'mavera-dss'                        proj-abc123  [production, staging]
  'sandbox'                           proj-def456  [production]

Pass --project <name> (or --project-id) to pick one.
```

`mavera-audit` is the right pilot: it is the one service verified end to end on the old setup, and it
needs Mongo and RabbitMQ but not SQL, so a SQL problem cannot confuse the result.

Deploy it from the UI and read the logs. The line that matters:

```
[entrypoint] appsettings.json rendered; starting Mavera-Audit.dll
```

A `[entrypoint] WARNING: unrendered placeholders remain` block names exactly which variables are
missing from the Dokploy environment — it doubles as the completeness check. If instead you see the app
start with no `[entrypoint]` line at all, the Run Command did not take effect and nothing has been
rendered — check Advanced → Run Command on the Application.

Then confirm the two things the whole topology rests on, from inside the container (Dokploy's terminal):

```sh
getent hosts sqlserver mongo redis minio otel-collector gotenberg seq
getent hosts rabbitmq rabbitmq.rabbitmq.svc.cluster.local
getent hosts mavera-audit.svc.cluster.local     # its own alias, from a sibling
```

### 8e. The rest

```bash
python scripts/dokploy/provision.py                # dry run, all 29
python scripts/dokploy/provision.py --apply
```

**If a run fails part-way through**, re-run it — the script matches existing applications by name and
updates them in place rather than creating a second one. Check what it sees first:

```bash
python scripts/dokploy/provision.py --list
python scripts/dokploy/provision.py --only mavera-audit --update-only --apply
```

`--list` prints the same listing the provisioning run uses to decide create-vs-update, so if your
half-provisioned service appears there, the re-run will update it. `--update-only` makes that
guarantee explicit: it refuses to create anything, and fails instead.

**A half-applied Application is worse than a failed one**, because the API can accept every call and
still leave something unset. The one that bites hardest: `buildType` defaults to **`nixpacks`**, and
Nixpacks ignores `dockerfile/Dockerfile` completely — it auto-detects the language and chooses its own
SDK version. So an Application whose `saveBuildType` never landed builds *successfully* against the
wrong .NET major, and the only symptom is a confusing runtime failure much later.

Every `--apply` therefore reads each Application back and checks it against the manifest — build type,
Dockerfile path, repo, branch, source type, entrypoint command and args, network aliases, and a
non-empty environment. It refuses to report success if anything is off:

```
verifying what actually landed:
  mavera-audit
    PROBLEM  buildType: expected 'dockerfile', got 'nixpacks' - nixpacks ignores
             dockerfile/Dockerfile and picks its own SDK version

1 problem(s): the API accepted the calls but the state is not what the manifest
says. Do NOT deploy yet - fix these first, then re-run.
```

Check already-provisioned Applications the same way without writing anything:

```bash
python scripts/dokploy/provision.py --verify-only
python scripts/dokploy/provision.py --verify-only --role preview --only mavera-audit
```

The preview-role check is worth running on its own: it is what catches a preview host that has
inherited production network aliases, without needing a live preview to observe the DNS collision.

Then deploy from the UI in this order, so each group comes up against something that already answers:

| Step | Services |
|---|---|
| 1 | `mavera-identity-server`, `mavera-identity-client`, `mavera-user-service` |
| 2 | the remaining 10 platform services |
| 3 | evaluation (3), then documents (5) |
| 4 | the 8 AI orchestrators |
| 5 | `mavera-libertine` last — the gateway is only useful once its targets resolve |

**The first build of each Application is long.** Five .NET SDK majors (6.0, 7.0, 8.0, 9.0, 10.0) across
alpine and Debian variants get pulled. Dokploy builds Applications one at a time, which is slower than
the old `COMPOSE_PARALLEL_LIMIT=2` but cannot OOM the VM the way 29 concurrent builds could.

> **Never** run `docker builder prune`, and do not put it in a cron job. It wipes the layer cache and
> forces every Application to rebuild from scratch.

`provision.py` records each Application's generated `appName` in `build/dokploy/state.json`. Dokploy
appends a random suffix and never changes it, so that file is the only place those names are written
down — keep it, or re-derive it with `--list`.

---

## Phase 8.5 — Turn on preview deployments

Opt in per repo. Each enabled repo costs up to `--preview-limit` containers plus a .NET SDK build on the
same VM.

```bash
python scripts/dokploy/provision.py --role preview --only mavera-audit \
  --preview-wildcard '*.preview.example.com' --apply
```

This creates a **second** Application, `mavera-audit-pr`, which is never deployed itself — it exists
only to spawn previews, and it carries **no network aliases**. That separation is the point: previews
inherit their parent's aliases, so a preview of the production Application would answer to
`mavera-audit.svc.cluster.local` and take roughly half of production's internal traffic. See README
*Why two Applications per repo*.

Add a wildcard DNS A record for `*.preview.example.com` → the Elastic IP. Omit `--preview-wildcard` and
Dokploy falls back to `sslip.io`, which needs no DNS at all but gives out ugly, public hostnames.

Then verify the behaviour, not just the setup:

1. Open a PR on `MaveraDSS/mavera-audit` **targeting the branch the Application is configured with**
   (`develop` for audit). A PR against any other base branch produces no preview — that is the match
   condition, and it is also why the production Applications keep previews off.
2. A preview URL appears on the `mavera-audit-pr` Application. Hit it.
3. Push a second commit to the same PR. The **same** URL rebuilds in place — appName and domain are
   stable for the life of the PR.
4. The check that matters, from a third container while the preview is running:

   ```sh
   getent hosts mavera-audit.svc.cluster.local     # must return exactly ONE address
   ```

   More than one means the preview is claiming a production name — stop and re-check that the preview
   host's Swarm network setting has no `Aliases`.
5. Close the PR. The preview, its Traefik config and its files are removed.

**Previews share production data.** They point at the same SQL Server, Mongo and RabbitMQ as
production. A PR carrying an EF migration or a destructive seed will hit real data, and a preview that
consumes a named RabbitMQ queue will steal messages from its production twin. That was the deliberate
trade-off in choosing single-service previews; if it bites, the next step is a per-preview SQL catalog /
Mongo database / RabbitMQ vhost driven off `${{DOKPLOY_DEPLOY_URL}}` in the preview environment.

---

## Phase 9 — Domains and TLS

1. Point DNS at the VM's **Elastic IP** (two records, two distinct names — see
   *Choosing the hostnames* in phase 05):

   ```
   mavera.example.com     A   <elastic-ip>
   identity.example.com   A   <elastic-ip>
   ```

2. `provision.py` already created both domains, if `GATEWAY_HOST` and `IDENTITY_HOST` were set in its
   environment when it ran:

   ```bash
   GATEWAY_HOST=mavera.example.com IDENTITY_HOST=identity.example.com \
     python scripts/dokploy/provision.py --only mavera-libertine --only mavera-identity-server --apply
   ```

   Otherwise add them by hand: Dokploy → the Application → **Domains**, container port **80**, HTTPS
   with Let's Encrypt. Either way Dokploy owns the certificate and writes the Traefik config; the
   Applications carry no labels of their own.

   The two hosts **must differ** — they are distinct `Host()` rules.

3. If you are using previews with a custom wildcard, add that record too:

   ```
   *.preview.example.com  A   <elastic-ip>
   ```

4. Once the certificates are issued, set `PUBLIC_SCHEME=https` in `.env`, then regenerate and
   re-provision so the identity server advertises an issuer that matches what browsers actually reach:

   ```bash
   python scripts/dokploy/generate-manifest.py
   python scripts/dokploy/provision.py --apply
   ```

   Then redeploy `mavera-identity-server` and `mavera-libertine`. Skipping this leaves
   `IdentityServer_IssuerUri` on `http://`, and clients reject the mismatch.

`mavera-libertine` is a YARP gateway whose routing table configures itself from the same placeholder
set, so it fronts the FE-facing surface with no extra wiring. The other 27 services stay internal, and
reachable only through the network aliases — which is why Phase 10 checks those first.

---

## Phase 10 — Verify

The application services are Swarm services now, not Compose services, so `docker compose exec` only
reaches the infrastructure stack. For an Application, use Dokploy's built-in terminal, or find its task
container by the `appName` in `build/dokploy/state.json`:

```bash
APP=$(docker ps --filter "name=mavera-audit-" --format '{{.Names}}' | head -1)
```

```bash
# 1. infrastructure is up, nothing restarting
cd /etc/dokploy/compose/<infra-app-name>/code
docker compose -f docker-compose.infra.yml ps

# 2. the Applications are running and converged
docker service ls | grep mavera

# 3. infrastructure DNS, from inside an Application
docker exec "$APP" getent hosts sqlserver mongo redis minio otel-collector gotenberg seq
docker exec "$APP" getent hosts rabbitmq rabbitmq.rabbitmq.svc.cluster.local

# 4. app-to-app aliases — BOTH forms must resolve, or service discovery is broken
docker exec "$APP" getent hosts mavera-user-service
docker exec "$APP" getent hosts mavera-user-service.svc.cluster.local

# 5. and they must answer, not just resolve
docker exec "$APP" wget -qO- http://mavera-user-service/Vera/UserService/health

# 6. config really rendered — no "$" literals should appear
docker exec "$APP" sh -c 'grep -E "Host|Endpoint" /app/appsettings.json'

# 7. message bus connected: queues should exist
docker compose -f docker-compose.infra.yml exec rabbitmq rabbitmqctl list_queues name messages

# 8. telemetry flowing (accepted == sent, no failures)
docker run --rm --network dokploy-network curlimages/curl:latest -s \
  http://otel-collector:8888/metrics | grep -E 'otelcol_(receiver_accepted|exporter_sent)_spans'

# 9. the gateway answers, and the route table works.
#    401 is the PASS here: the route matched and the target rejected the empty token.
#    404 means no route; 502/503 means the target is down or lost its alias.
curl -I https://mavera.example.com
curl -s -o /dev/null -w '%{http_code}\n' https://mavera.example.com/libertine/case/1
```

Logs for all services land in **Seq** — add a Dokploy domain for the `seq` service (port 80) and log in
as `admin` with `SEQ_ADMIN_PASS`.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Build fails `failed to evaluate path "https://…"` | Compose is resolving the git URL as a local path. Known on **Windows** Compose; the Linux host is fine. Check `docker compose version`. |
| Build fails cloning a service repo | The Dokploy GitHub App is not installed on that repo. Re-check Phase 8b; `GH_PAT` is not involved. |
| VM OOMs mid-build | Too much building at once: an Application build plus one or more previews. Lower `--preview-limit`, or move builds to a Dokploy Build Server. |
| `Stack` selected instead of `Docker Compose` | Swarm does not support `build`; recreate the app with the right Compose Type. |
| Config arrives as literal `$Placeholder` | The entrypoint was overridden, or a variable is missing from `x-placeholders`. `docker compose logs <svc>` prints any unrendered names. |
| `UriFormatException` at startup | `Tracing_Connection_String` empty or not absolute. It must be `http://otel-collector:4317`. |
| `Name or service not known` for `*.svc.cluster.local` | The target service is not running, or lost its network alias. |
| Deploy aborts: `required variable X is missing a value` | Env-file generation is off in the Environment tab, so no `.env` is written next to the compose file. Turn it on. If the file lands in the wrong directory instead, use the absolute `--env-file` fallback. |
| Stack comes up on `localhost` with dev passwords | You are on an older revision of this file that still had `${VAR:-default}` fallbacks. Pull the current one. |
| Login redirects fail, or `iss` mismatch | `PUBLIC_SCHEME` does not match how clients reach the server, or `IDENTITY_HOST` changed (use an Elastic IP). |
| Gateway and identity server serve each other's responses | `GATEWAY_HOST` and `IDENTITY_HOST` are the same value; the two Traefik routers collide. |
| `Invalid object name 'dbo.X'` | The database is empty — the restore was skipped or `DB_SERVER` points somewhere unpopulated. Check `docker compose logs mssql-init` for `[ok]`/`[MISSING]`. |
| `mssql-init` exits non-zero on a restore | Deliberate: nothing starts on half-restored data. See Phase 7, *If a restore fails*. |
| Deploy stalls on `Pulling … unauthorized` | Local only: `pull_policy: build` was removed from `x-service-base`, so Compose is trying ACR instead of building. |
| SQL Server eating all RAM | `MSSQL_MEMORY_LIMIT_MB` only applies at first-run setup; `mssql-init` re-applies it via `sp_configure` on every run — re-run it. |
| `pull access denied for minio/mc` / `for minio/minio` | MinIO deleted its Docker Hub repositories. Both images now come from `quay.io` and are pinned in the compose files — make sure you are deploying a revision that has that change. |
| `No such image: alpine:3` (or any other image) right after a failed pull | Collateral: one failed pull aborts the whole `up`, and Compose then cannot create the remaining containers. Fix the *first* pull error in the log and re-deploy; the rest usually clear on their own. |
| `pull access denied` / `toomanyrequests` on several Docker Hub images | Anonymous Docker Hub pulls are capped at 10/hour per IP since April 2025, and the infra stack pulls 7 Hub images. Authenticate on the host: `sudo docker login`. See *Authenticate to Docker Hub* in Phase 6. |
| `Input validation failed` / `expected nonoptional, received undefined` from an `application.*` call | Dokploy added a required field to that endpoint's input schema. `provision.py` fills required fields from your instance's own OpenAPI document and prints each one it auto-filled, so a re-run normally clears it. If it does not, the failing field name is in the `zodError` and needs adding to the payload in `provision.py`. |
| Build installs the wrong .NET major (e.g. 6.0 for a net8.0 project), or ignores `dockerfile/Dockerfile` | The Application is still on Dokploy's default `buildType` of `nixpacks`, which auto-detects the language and picks its own SDK. Almost always means `saveBuildType` failed earlier in the run. Check with `provision.py --list` (it prints `buildType=`) or `--verify-only`, then re-run with `--apply`. |
| A run failed part-way through one service | Re-run it. `provision.py` matches existing applications by name and updates in place. Confirm first with `--list` that the half-made application is listed; then re-run, optionally with `--update-only` so it can only update, never create. |
| App starts but reads literal `$Placeholder` values | The Run Command did not take effect, so `envsubst` never ran. Check Advanced → Run Command: it should be `/bin/sh` with the script as the first argument (inline mode), or `/bin/sh /entrypoint.sh` (bind mode). |
| `/entrypoint.sh: not found`, or it is a directory | `--entrypoint-mode bind` only: the bind-mount source is missing on the node the task landed on, so Docker created it as a directory. See Phase 8a. The default inline mode cannot hit this. |
| A service resolves to two addresses | A preview is claiming a production alias. The preview-host Application must have **no** `Aliases` in its Swarm network setting. See README *Why two Applications per repo*. |
| 502/503 from the gateway for one service | That Application lost its alias, or was provisioned without one. Re-run `provision.py --only <svc> --apply`. |
| A PR opens but no preview appears | The PR's **base** branch must equal the preview-host Application's configured branch, previews must be active on it, and the author needs write access to the repo. |
| `storage-service` / `mavera-ocr` fail on queries | `MaveraStorageOperations` / `MaveraOcrOperations` are empty; no backup was supplied for them. |

---

## Ongoing

- **Redeploys.** Each Application redeploys on its own, on push to its configured branch (auto-deploy
  is on for the production Applications). Keep the build cache.
- **Configuration changes** start in `docker-compose.yml` — it is still the source of truth for the
  `$Placeholder` values. Edit `x-placeholders` or `.env`, then:

  ```bash
  python scripts/dokploy/generate-manifest.py
  python scripts/dokploy/provision.py                    # dry run
  python scripts/dokploy/provision.py --apply            # pushes the new env to all 29
  ```

  Then redeploy the affected Applications. Re-run the `--role preview --only <svc>` calls too, since a
  preview's environment is a separate copy rather than a merge.
- **Bumping to the next release branch**: set `BRANCH` in `.env`, and first re-check which repos still
  need `BRANCH_FALLBACK`. Anything listed has not cut the branch; anything that has dropped off the
  list can move back onto `${BRANCH}` in `docker-compose.yml`:

  ```bash
  REL='release%2Fv.be-2026-04-01'   # url-encode the '/'
  for r in $(grep -oE 'MaveraDSS/[a-z0-9-]+' docker-compose.yml | sort -u); do
    gh api "repos/$r/branches/$REL" >/dev/null 2>&1 || echo "fallback: $r"
  done
  ```

  Then regenerate and re-provision as above — `provision.py` reads the branch out of the manifest, so
  the Applications follow `docker-compose.yml` rather than needing 29 edits in the UI.
- **Adding a service.** Add it to `docker-compose.yml` as usual, regenerate, and
  `provision.py --only <name> --apply`. Nothing else is hand-written.
- **`build/dokploy/state.json`** maps each service to the `appName` Dokploy generated for it. Those
  names are random and immutable, and they are what `docker service ls` and the host paths use. Keep the
  file, or re-derive it with `provision.py --list`.
- **Backups**: the infra stack's named volumes (`sqlserver-data`, `mongo-data`, `minio-data`,
  `rabbitmq-data`, `redis-data`, `seq-data`) support Dokploy's scheduled S3 volume backups. Note they
  are prefixed with the infra service's `appName`, not `mavera_`.
- **The upgrade worth making**: have `MaveraDSS/ci-template` push a moving tag (e.g. `develop`)
  alongside its git-SHA tags. The Applications could then use a Docker-image source instead of building
  on the VM — minutes instead of an hour, and no build load competing with previews. The cost is that
  preview deployments would have to be driven by CI through the Dokploy API rather than by Dokploy's own
  PR webhook, so weigh it against how much previews are being used.
