# Feature areas → services

Plain table used by `/dss:dev-env` to propose which services a ticket touches. Add rows as you learn; keep the
words as they appear in tickets (Swedish and English both occur). Matching is on summary, spec and description.
A row's match is a *hint*; endpoint paths in the spec and the `[FE]`/`[BE]` prefixes weigh more.

| Words in the ticket | Service to run locally | Why |
|---|---|---|
| evaluation, evaluations, case, cases, ärende, question, questions, question group, formulation, timeline, injury type, typeOfInjury, insurance condition, calculation, necessity, reallocate, close monitor, case view configuration, field sections | `evaluation-service` | owns cases, evaluations, questions and their configuration |
| user, users, organisation, organization, company, company admin, membership, member type, member rights, feature flag, site admin, medical advisor manager, MAM, roles, authInfo, Okta registration, invite, activate | `user-service` | owns users, organisations, memberships, rights and feature flags |
| document, documents, upload, download, PDF, PDFTron, WebViewer, OCR, storage, S3, bucket, document classifier, identity check, DocSvc | `document-service` | owns document storage and document metadata |
| medical advisor network, MA network, network admin, canBeManaged, GetNetwork, GetOrganizationNetworks | `medical-advisor-network` | owns MA networks |
| caregiver, caregivers, vårdgivare, clinic, hospital | `caregivers` | owns the caregiver register |
| notification, e-mail, email, mail, SMS, SendGrid, reminder | `notification-service` | sends mail and SMS (needs `--allow-mail`; ask) |
| integration API, external API, API key, Integration, IntegrationApi toggle | `integration` | the public Integration API |
| webhook, WebhookService, client webhook, delivery, retry policy, dead letter, subscription | none runnable | the webhook service (`mavera-client-webhook`) is not in the manifest at all; it stays on dev02 until someone adds it (README, "Adding a service") |
| libertine, gateway, DTO, aggregate, route, YARP, proxy, getMemberRights, featureFlags endpoint, telemetry middleware, CORS | `libertine` | the gateway; runs locally in every devenv stack anyway, listed so the developer knows it is the code to change |
| login, sign in, Okta, IDX, NextAuth, session, token refresh, middleware.ts, redirect, locale, i18n, translation, component, page, modal, button, Chakra, RTK Query, hook, dashboard widget, statistics view, advanced search UI | frontend only | frontend + libertine, no `--local` service |
| dashboard data, statistics endpoint, Vera/Dashboard | none runnable | Dashboard is not in the runnable list; stays on dev02, say so |
| identity server, IdentityServer4, connect/token, ROPC | none runnable | identity stays on dev02; say so |
| news, NewsManager, Mongo bridge | none runnable | stays on dev02 |
| AI, enhanced patient view, EPV, idp2, document processor, timeline extraction, summarize, complexity, chat | none runnable | the AI services are not in the manifest; stays on dev02 |

## Signals stronger than words

- A path in the spec: `/Vera/<Service>/...` names the service directly; `/libertine/...` is libertine's own
  code or a proxy route, look the route up in `mavera-libertine/LibertineWeb/appsettings.json`
  (`ReverseProxy.Routes`, `ReverseProxy.Clusters`) to see which cluster it forwards to.
- Summary prefixes: `[FE]` frontend, `[BE]` backend, `[INFRA]` tooling. `[BE][FE]` means both.
- Linked pull requests or branches on the ticket: the repo they belong to settles it.
- A C# class, controller or table name: `rg` for it across the cloned repos.
- A frontend ticket that is a follow-up to a backend ticket (it names the other key, or "depends on"): check
  whether dev02 already runs that backend change (the ticket often names the release branch). If not, propose
  the backend service locally on its ticket branch, otherwise the frontend cannot be tested against dev02.

## Evaluated against real tickets (2026-09-16)

DSS-5495 (user-service + audit of evaluation/document), DSS-5501 (evaluation-service), DSS-5566 (frontend only),
DSS-5571 (frontend only, backend dependency noted), DSS-5568 (webhook service, not runnable): five for five
against the repos the commits landed in. Keep this list growing when a proposal turns out wrong.
