# The repositories

All cloned side by side under one folder (the devenv repos root). Service name (manifest) = repo name without
the `mavera-` prefix, except `integration` = `mavera-integration` and the frontend.

Always read `<repo>/CLAUDE.md` first when it exists; it wins over this table. The table exists for repos that have
none and as a quick orientation. Base branch is `develop` everywhere.

| Repo | Owns | CLAUDE.md | Branch name | Commit subject | Tests | Merge |
|---|---|---|---|---|---|---|
| `verisk-nordics-frontend` | Next.js monorepo; the DSS app is `apps/dss` (Chakra v2, RTK Query, next-intl, vitest) | yes, root + `apps/dss/CLAUDE.md` | `feat/DSS-1234-kebab-slug` (type ∈ feat fix chore refactor test ci docs perf; key uppercase, the hook prepends it) | Conventional Commits `type(scope): imperative` | `pnpm --filter dss run gate:seed`, then `gate:tests:run`, `gate:types:run`, `gate:lint:run` (pre-commit hook needs the evidence) | rebase merge, never delete the branch |
| `mavera-libertine` | YARP gateway, DTOs, auth filters (`LibertineWeb`) | no | `DSS-1234_kebab-slug` (check `git log` for the current style) | `DSS-1234: ...` | `dotnet test` at repo root | PR to develop |
| `mavera-evaluation-service` | cases, evaluations, questions, timeline, calculations; DbUp migrations in `EvaluationService/_Migrations` | yes | `DSS-1234_kebab-slug` | `DSS-1234: ...`; CLAUDE.md changes go in the same commit as code | `dotnet test` at repo root | PR to develop |
| `mavera-user-service` | users, organisations, memberships, feature flags, Okta registration; DbUp in `UserService.Api/_Migrations` | yes | `DSS-1234-kebab-slug` | `DSS-1234: ...`; CLAUDE.md gets its own commit | `dotnet test mavera-user-service.sln` | squash |
| `mavera-document-service` | document storage, S3, OCR status; DbUp | no | `DSS-1234_kebab-slug` | `DSS-1234: ...` | `dotnet test` at repo root | PR to develop |
| `mavera-medical-advisor-network` | MA networks, Redis-backed | no | `DSS-1234_kebab-slug` | `DSS-1234: ...` | `dotnet test` at repo root | PR to develop |
| `mavera-caregivers` | caregivers; FluentMigrator | no | `DSS-1234_kebab-slug` | `DSS-1234: ...` | `dotnet test` in `Mavera.Caregivers` | PR to develop |
| `mavera-notification-service` | mail and SMS | no | `DSS-1234_kebab-slug` | `DSS-1234: ...` | `dotnet test` at repo root | PR to develop |
| `mavera-integration` | public Integration API (.NET 10, Clean Architecture, Result pattern) | yes | per its CLAUDE.md | `DSS-1234 - description` | `dotnet test Mavera-Integration.slnx` | squash |
| `mavera-compose` | fleet compose, devenv, this plugin | yes | `dss-1234_kebab-slug` | `DSS-1234: ...` | `dotnet test` in `devenv/` | PR to develop |

## Rules that hold in every repo

- Never commit on `develop` or `master`. Check `git status --porcelain` and `git rev-parse --abbrev-ref HEAD`
  before branching; if the repo is dirty or not on `develop`, stop and ask.
- Never add `Co-Authored-By` or other trailers to commits.
- Never push, open a PR, or merge unless the developer asked in this session.
- Frontend and the two services with CLAUDE.md have `.githooks` (`core.hooksPath`): a commit that fails the
  hook is a real failure, not something to bypass with `--no-verify`.
- Which service serves which path: `/Vera/EvaluationService/**` evaluation-service, `/Vera/UserService/**`
  user-service, `/Vera/DocumentService/**` and `DocSvc` document-service, `/Vera/MedicalAdvisorNetwork/**`
  medical-advisor-network, `/caregivers/**` caregivers, `/libertine/**` libertine's own controllers (auth,
  organization feature flags, DTO aggregation) or its proxy routes to the clusters named in
  `mavera-libertine/LibertineWeb/appsettings.json`.
