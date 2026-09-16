---
name: verify
description: Verify a change against the running DSS local stack (devenv) - API calls through local libertine and a browser pass with Chrome DevTools - and write evidence for a ticket. Use after a change, before committing or reviewing.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:verify $ticket

Goal: proof, not a feeling. Every claim in the final report points at a file under
`<DEVENV>/.state/evidence/$ticket/`.

Read `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md` and `${CLAUDE_PLUGIN_ROOT}/reference/verification.md` first.

## 1. What to verify

- With a ticket key: fetch it (Atlassian MCP, fields `summary`, `customfield_10064`) and take the checks from
  "How can it be tested?" and the DoD. Turn each into one concrete check: request + expected status/fields, or a
  browser flow + expected screen state.
- Without a key, or when the ticket has no test description: ask the developer for the flow to check in one
  sentence and derive the checks from that.
- Show the check list and let the developer trim it before you run anything.

## 2. Confirm the stack

`status --json`. Every process must be `alive` and `healthy`; the local services listed must include the ones the
ticket changed, on the expected branch. If not, stop and say what to start (`/dss:dev-env`); do not start it
yourself from this skill.

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
`verification.md`. Then report in chat: pass/fail per check with the evidence file, anything unexpected (errors in
the console, 4xx/5xx, exceptions in the log) even when the check passed, and the folder path. Do not post to Jira
unless asked; when asked, the comment is `summary.md` without the token or any secret.
