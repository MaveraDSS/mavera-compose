# CLAUDE.md — mavera-compose

Two things live here:

- **The fleet compose file** (`docker-compose.yml`, `config/`): all backend services in Docker, with the
  `x-placeholders` block that names every `$Placeholder` the services' `appsettings.json` templates use.
  See `README.md` and `DEPLOY.md`.
- **devenv** (`devenv/`): run the frontend, libertine and chosen services on a developer machine against
  dev02. A .NET 8 console app; `devenv/README.md` is the source of truth for how it works.
- **The `dss` Claude Code plugin** (`plugins/dss`, marketplace in `.claude-plugin/`): the `/dss:dev-env`,
  `/dss:verify` and `/dss:ticket` skills. They orchestrate work across the other repos and read each repo's
  own `CLAUDE.md` for its conventions; do not copy those conventions here.

## Working in this repo

- Run devenv from `devenv/`: `dotnet run --project src/Devenv -- <command>`. Tests: `dotnet test` in `devenv/`
  (xunit; the fixtures link the real `manifest.json`, `environments/dev02.json`, `secrets.example.json` and the
  root compose file, so manifest mistakes fail tests).
- `TreatWarningsAsErrors` is on in `devenv/src/Devenv`.
- Secrets: `devenv/secrets.json`, `devenv/devenv.local.json` and `devenv/.state/` are gitignored. Never commit a
  secret value; `secrets.example.json` holds 1Password locations only.
- Branches: `dss-NNNN_<kebab-slug>` off `develop`; commit subjects start with the ticket key
  (`DSS-5606: ...`). No `Co-Authored-By` trailers. Push only when asked.
- When a service gains a placeholder or a new service appears, follow the "Adding a service" checklist in
  `devenv/README.md` and keep `x-placeholders` in the root compose file complete.
