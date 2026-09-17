---
name: dev-env
description: Start the DSS local stack for a Jira ticket. Reads the ticket, proposes which backend services to run locally (the rest stays on dev02), and after confirmation runs devenv with that --local list. Also accepts an explicit --local list instead of a ticket.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:dev-env $ticket

Goal: the developer types a ticket and ends with the right stack running, without having to know which services
the ticket touches. You propose, they confirm, then you start. Nothing starts without a yes.

Read `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md` first (how to find and run devenv, how to read its output).

## 1. Resolve the argument

- `DSS-1234` or a Jira URL containing a key: the ticket.
- `--local a,b`: skip inference; go to step 4 with that list. `--no-infra` alone means frontend + libertine only.
- Nothing: ask for a ticket key.

## 2. Fetch the ticket and its context

Follow `${CLAUDE_PLUGIN_ROOT}/reference/context.md`: the ticket (summary, description, spec
`customfield_10064`, status, parent, sub-tasks, issue links, comments), the related tickets (parent, siblings,
linked issues, keys mentioned in the text) and the previous work in the repos (branches and commits carrying the
key or a predecessor's key, pull requests). Cloud `mavera-dss.atlassian.net`. If the Atlassian MCP is not
available, say so with the fix (connect the Atlassian connector in Claude Code, then retry) and stop. If the key
does not exist, stop with the message. Summarise the context in a few lines before proposing.

## 3. Infer the services

Apply, in this order of weight, and keep a one-line reason per hit:

1. Paths and names in the spec: `/Vera/<Service>/...`, controller or class names (`rg` across the repos root),
   libertine routes (`mavera-libertine/LibertineWeb/appsettings.json`). See `feature-areas.md`, section
   "Signals stronger than words".
2. Summary prefixes `[FE]`, `[BE]`, `[INFRA]`, existing branches or commits for the key, linked pull requests,
   and the repos where the predecessor tickets landed.
3. The word table in `${CLAUDE_SKILL_DIR}/feature-areas.md`.

Only services with a `project` in `<DEVENV>/manifest.json` can run locally (today: evaluation-service,
user-service, document-service, medical-advisor-network, caregivers, notification-service, integration). A hit
on anything else is reported as "stays on dev02" with the reason. Frontend-only tickets propose no local service:
frontend + libertine is the whole stack. notification-service needs `--allow-mail`; propose it, never add the
flag without asking.

## 4. Propose

Show a short table: service, reason, confidence (high / medium / low), then the exact command you would run.
Ask with AskUserQuestion: accept as proposed; frontend + libertine only; edit the list (they type it). Also
mention the current state from `status --json` when something already runs: if it runs with the same set, offer
to keep it instead of restarting.

## 5. Start

On yes: `up -d --local <list>` (or `up -d --no-infra` with an empty list). Relay preflight failures and guard
messages verbatim with the fix from `reference/devenv.md`; do not retry with `--allow-*` or `--skip-preflight`.
On success end with:

- the `ready:` line, the frontend URL and the log folder,
- which repo and branch each local service runs from (`status --json` → `localServices[]`), so a wrong branch
  is visible now rather than after an hour of debugging,
- one sentence on what stays on dev02.

Do not edit code, do not create branches; that is `/dss:ticket`.
