---
name: repro
description: Reproduce a DSS bug on the local devenv stack before anyone fixes it - turn the ticket's steps into checks whose expected outcome is the buggy behaviour, run them against the unfixed code (API through libertine, browser through Chrome), compare with dev02 where cheap, and record the evidence as the "before" half of a before/after proof. Use on Bug tickets before /dss:ticket or a manual fix; not for new features.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:repro $ticket

Goal: the bug is seen, not assumed, on the code as it is today, and the very same checks can be run again after
the fix. A repro check *reproduces* when the buggy outcome shows. "Does not reproduce" is a finding too, not a
failure of the run. You change no code, create no branch and post nothing to Jira.

Read `${CLAUDE_PLUGIN_ROOT}/reference/context.md`, `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md` and
`${CLAUDE_PLUGIN_ROOT}/reference/verification.md` (section "Before and after") first.

## 1. What the bug is

- Gather the context per `reference/context.md`. From the description, the spec (`customfield_10064`) and the
  comments take: steps to reproduce, expected and actual behaviour, the role, organisation, case or data it
  needs, the environment where it was seen (production, stage, dev02) and the version or branch if named.
- Turn each into one **repro check**: a request with the buggy status or field, or a browser flow with the buggy
  screen state. Next to it write the correct outcome; that column is what `/dss:verify` will expect after the fix.
- A ticket with neither steps nor an actual/expected pair: ask the developer for the scenario in one sentence.
  Do not invent a scenario.
- Show the list and let the developer trim or add before you run anything.

## 2. The code under test

The repro must run against code that still carries the bug.

- `status --json` → `localServices[].branch`, and `git -C <repo> log -1 --format='%h %s'` for each local
  service and the frontend. The affected service must be on `develop`, or on the branch the ticket names as
  affected (a release branch, say). Record repo, branch and commit; they go into the evidence header.
- A branch for this ticket already exists and carries a fix: say so and ask, once: reproduce on `develop`
  first (they check it out), or skip straight to `/dss:verify`. Never switch a dirty tree yourself.
- Nothing runs: follow `/dss:dev-env` steps 3 to 5 (`${CLAUDE_PLUGIN_ROOT}/skills/dev-env/SKILL.md`) with the
  context you have: propose the `--local` list, wait for the yes, `up -d --local <list>`. The wrong set runs:
  propose the `down` + `up`, wait for the yes.

## 3. Run

- API tier and browser tier per `verification.md`. Writes only inside the test organisation from
  `status --json` → `testing`; an authorisation bug that needs "another organisation" uses ids read from
  responses and read-only requests, nothing else.
- For API checks also send the same request to the remote environment when it is cheap: the token from
  `devenv token` is issued by dev02's identity server and is accepted by `<publicBaseUrl>` from
  `environments/<env>.json` directly. Record both results. A difference between local `develop` and dev02 is a
  finding in itself (a hotfix on a release branch, a deployment ahead or behind).
- Collect at the buggy state: the screenshot, console errors, failed requests to `localhost:5151`, and the
  service log excerpt with the exception or the line that shows the wrong decision.

## 4. Evidence

`<DEVENV>/.state/evidence/$ticket/before/`, laid out as `verification.md` "Before and after" says: `summary.md`,
`checks.md` (the contract verify reads later), `api.md`, `browser-<n>.png`, `console.txt`, `network.txt`,
`log-<service>.txt`.

## 5. Report

- Per check: reproduced, not reproduced or partially, with the evidence file.
- What it was reproduced against: repo, branch, commit per local service; the dev02 result beside it.
- Where it goes wrong, as far as the evidence shows without touching code: the request, the log line, the
  component. A pointer for the fix, not the fix.
- Not reproduced: the likely reasons and the question for the developer. Look before you ask:
  `git -C <repo> log --all -i --grep='<ticket key>' --oneline` and the ticket's linked issues may show the fix
  already landed; the scenario may need a role, organisation or data the test users do not have; the
  environment may differ (see the dev02 comparison); the ticket may describe it wrong.
- End with the folder path and the next step: `/dss:ticket $ticket` continues from this evidence.
