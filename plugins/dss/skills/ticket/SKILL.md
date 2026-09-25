---
name: ticket
description: Work a DSS Jira ticket end to end on the local devenv stack - understand, start the right services, reproduce first when it is a bug, branch per repo convention, plan, implement after confirmation, unit tests, restart, verify with before/after evidence, commit. No push or PR unless asked.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:ticket $ticket

Goal: the developer hands over a ticket and reviews a finished change with evidence. For a bug that evidence is
a before/after proof: the bug seen on the unfixed code, the same checks passing on the fix. You stop for a yes
at two points: the service proposal and the change plan. You never push, open a PR or comment on Jira unless
asked.

Read `${CLAUDE_PLUGIN_ROOT}/reference/context.md`, `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md`,
`${CLAUDE_PLUGIN_ROOT}/reference/repos.md` and `${CLAUDE_PLUGIN_ROOT}/reference/verification.md` first.

## 1. Understand

Gather the context per `reference/context.md`: the ticket with its spec and comments; the parent, sibling
sub-tasks, linked issues and every ticket key mentioned in the text; existing branches, commits and pull
requests carrying this key or a predecessor's key in every candidate repo. Write the context block (what is
asked, DoD, siblings and dependencies, predecessors and where their code landed, existing work to continue,
decisions from comments). Then restate in three lines: what is wrong or wanted, the definition of done, how it
will be tested. Anything ambiguous that changes the work: ask now, once, with concrete options. An existing
branch for the ticket is continued, not replaced, unless the developer says otherwise.

Decide **bug or feature** and say which: a bug when the issue type is Bug, or the text has steps to reproduce or
an actual/expected pair, or the summary speaks of a fix, regression or something broken. A feature has nothing
to reproduce; step 3 is skipped with one line saying so.

## 2. Environment

Follow `/dss:dev-env` steps 3 to 5 (`${CLAUDE_PLUGIN_ROOT}/skills/dev-env/SKILL.md`): propose the services with
reasons, confirm, start or keep the stack. The repos you will change are the local services plus the frontend
when the ticket has frontend signals. Open the code of every candidate repo while the stack starts and confirm
the guess by reading the relevant controller, component or handler; drop repos the ticket does not touch.

## 3. Reproduce (bugs only)

Run `/dss:repro $ticket` (`${CLAUDE_PLUGIN_ROOT}/skills/repro/SKILL.md`) with the context you already have, on the
code as it is now, before any branch exists: the affected service on `develop` (or the branch the ticket names).
It writes `<DEVENV>/.state/evidence/$ticket/before/` with `checks.md`.

- Reproduced: the "correct outcome" column of `checks.md` is the verification list of the plan. Continue.
- Not reproduced: stop and ask, with what the repro found (fix already on `develop`? data or role missing?
  environment difference? ticket wrong?) and the options: proceed anyway, adjust the checks and retry, or leave
  the ticket for the developer to clarify. Do not plan a fix for a bug nobody has seen.

## 4. Branch, per repo

For each repo to change: read its `CLAUDE.md` in full when it exists (root and app-level for the frontend),
otherwise `reference/repos.md`. The session is usually opened in the repos root, so Claude Code has not loaded
those files for you; their rules apply regardless. Then `git -C <repo> status --porcelain` and
`git -C <repo> rev-parse --abbrev-ref HEAD`. If the repo is dirty
or not on `develop`, stop and ask; never stash and never switch a dirty tree. Otherwise `git pull --ff-only` and
create the branch in that repo's format (see the table). Report each branch name.

## 5. Plan

Present the change plan: per repo the files, the approach in a few sentences, the unit tests you will add or
change, the verification checks (for a bug: the repro checks with their correct outcomes, plus the ticket's other
test steps; for a feature: from the ticket's "how to test"), and open questions. A bug fix also gets a unit test
that fails on the unfixed code for the same reason the repro showed. Wait for a yes. This is the second and last
confirmation.

## 6. Implement

Follow the repo's CLAUDE.md conventions exactly (i18n keys, Chakra version, Result pattern, migration numbering,
and so on). Then run that repo's unit tests as its CLAUDE.md or `repos.md` says. Frontend: `gate:seed`,
`gate:tests:run`, `gate:types:run`, `gate:lint:run` (the pre-commit hook needs the evidence files). Services:
`dotnet test`. Fix what fails; do not skip or delete tests to get green.

## 7. Run the change

A changed backend service must be restarted: tell the developer, then `down` and `up -d --local <list>`. Read
`logs <service> --tail 200` after start for exceptions. The frontend hot-reloads; check `logs frontend` for a
compile error.

## 8. Verify

Run `/dss:verify $ticket` (`${CLAUDE_PLUGIN_ROOT}/skills/verify/SKILL.md`) with the checks from the plan. For a
bug it finds `before/checks.md`, writes `after/` and the before/after table. A failed check, or a row whose
before and after are the same, means back to step 6, not a softer report.

## 9. Commit and report

Per repo: stage only the files of this change, commit with the repo's subject format (ticket key first, no
trailers, no `Co-Authored-By`), respecting its hooks. The frontend and both services with CLAUDE.md run hooks
that must pass; never `--no-verify`.

Final report, written for someone who was not watching:

- per repo: branch, commit hash, files changed, tests run and their result,
- evidence: for a bug the before/after table from `summary.md` (check, before, after, verdict) and the folder;
  for a feature the pass/fail list and the `summary.md` path,
- open points: anything the ticket asked for that is not done, decisions the developer must make, follow-ups,
- what has not happened: no push, no PR, no Jira update, and the commands to do each when they want it.
