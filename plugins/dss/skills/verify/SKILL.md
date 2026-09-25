---
name: verify
description: Verify a change against the DSS local stack (devenv), starting it after confirmation when it is down - API calls through local libertine and a browser pass with Chrome DevTools - and write evidence for a ticket. Use after a change, before committing or reviewing.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:verify $ticket

Goal: proof, not a feeling. Every claim in the final report points at a file under
`<DEVENV>/.state/evidence/$ticket/`.

Read `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md` and `${CLAUDE_PLUGIN_ROOT}/reference/verification.md` first.

## 1. What to verify

- With a ticket key: fetch it (Atlassian MCP, fields `summary`, `customfield_10064`, `comment`, `issuelinks`) and
  take the checks from "How can it be tested?" and the DoD; the comments and the linked predecessor ticket often
  hold the concrete test steps and data (`reference/context.md`, sections 1 and 2). Turn each into one concrete check: request + expected status/fields, or a
  browser flow + expected screen state.
- `<DEVENV>/.state/evidence/$ticket/before/checks.md` exists (written by `/dss:repro`): every row of it is a
  check, expecting the "correct outcome" column. Add the ticket's other test steps; drop none of the repro rows.
  Tell the developer the run will produce the before/after table.
- Without a key, or when the ticket has no test description: ask the developer for the flow to check in one
  sentence and derive the checks from that.
- Show the check list and let the developer trim it before you run anything.

## 2. Confirm or start the stack

`status --json`. Every process must be `alive` and `healthy` (a non-empty `degraded` with `healthy: true` is
fine; say what it means, from `detail`); the local services listed must include the ones the ticket changed,
on the expected branch.

- Nothing runs: follow `/dss:dev-env` steps 3 to 5 (`${CLAUDE_PLUGIN_ROOT}/skills/dev-env/SKILL.md`) with the
  context you already have: propose the `--local` list with reasons, wait for the yes, `up -d --local <list>`.
  Do not skip the yes; the developer may have a stack in mind.
- The wrong set runs (a changed service is missing, or on the wrong branch): say so, propose the corrected
  `down` + `up -d --local <list>` (or a `git -C <repo> checkout`), wait for the yes, then do it.
- Something is `alive` but not `healthy`: read `logs <name> --tail 100`, report the cause, and stop; a
  half-running stack gives false failures.

## 3. API tier

Per `verification.md`: token via `devenv token` or a developer-provided token in a shell variable, requests
through `http://localhost:5151`, one record per request, service log excerpt for each. Writes only inside the
test organisation from `status --json` → `testing`; when it is blank, read-only.

## 4. Browser tier

Per `verification.md`: reuse or open a tab on `http://localhost:3002`, let the developer log in, drive the flow,
collect console errors, failed requests to `localhost:5151`, screenshots at the states the ticket describes,
and the local service log for the same moment.

## 5. Report

Write `summary.md`, `api.md`, `console.txt`, `network.txt`, `log-<service>.txt` and the screenshots as laid out in
`verification.md`. With a `before/` folder, write them into `after/` and then the top-level `summary.md` with the
before/after table: one row per check, before outcome, after outcome, verdict (`fixed`, `still broken`,
`unchanged: not a proof`). Then report in chat: pass/fail per check with the evidence file (for bugs: the
before/after table itself), anything unexpected (errors in the console, 4xx/5xx, exceptions in the log) even when
the check passed, and the folder path. Do not post to Jira unless asked; when asked, the comment is `summary.md`
without the token or any secret.
