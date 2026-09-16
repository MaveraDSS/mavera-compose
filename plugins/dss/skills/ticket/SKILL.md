---
name: ticket
description: Work a DSS Jira ticket end to end on the local devenv stack - understand, start the right services, branch per repo convention, plan, implement after confirmation, unit tests, restart, verify with evidence, commit. No push or PR unless asked.
disable-model-invocation: true
arguments: [ticket]
---

# /dss:ticket $ticket

Goal: the developer hands over a ticket and reviews a finished change with evidence. You stop for a yes at two
points: the service proposal and the change plan. You never push, open a PR or comment on Jira unless asked.

Read `${CLAUDE_PLUGIN_ROOT}/reference/devenv.md`, `${CLAUDE_PLUGIN_ROOT}/reference/repos.md` and
`${CLAUDE_PLUGIN_ROOT}/reference/verification.md` first.

## 1. Understand

Fetch the ticket (Atlassian MCP: `summary`, `description`, `customfield_10064`, `issuelinks`, `parent`, `status`).
Restate in three lines: what is wrong or wanted, the definition of done, how it will be tested. Anything
ambiguous that changes the work: ask now, once, with concrete options.

## 2. Environment

Follow `/dss:dev-env` steps 3 to 5 (`${CLAUDE_PLUGIN_ROOT}/skills/dev-env/SKILL.md`): propose the services with
reasons, confirm, start or keep the stack. The repos you will change are the local services plus the frontend
when the ticket has frontend signals. Open the code of every candidate repo while the stack starts and confirm
the guess by reading the relevant controller, component or handler; drop repos the ticket does not touch.

## 3. Branch, per repo

For each repo to change: read its `CLAUDE.md` in full when it exists (root and app-level for the frontend),
otherwise `reference/repos.md`. The session is usually opened in the repos root, so Claude Code has not loaded
those files for you; their rules apply regardless. Then `git -C <repo> status --porcelain` and
`git -C <repo> rev-parse --abbrev-ref HEAD`. If the repo is dirty
or not on `develop`, stop and ask; never stash and never switch a dirty tree. Otherwise `git pull --ff-only` and
create the branch in that repo's format (see the table). Report each branch name.

## 4. Plan

Present the change plan: per repo the files, the approach in a few sentences, the unit tests you will add or
change, the verification checks (from the ticket's "how to test"), and open questions. Wait for a yes. This is
the second and last confirmation.

## 5. Implement

Follow the repo's CLAUDE.md conventions exactly (i18n keys, Chakra version, Result pattern, migration numbering,
and so on). Then run that repo's unit tests as its CLAUDE.md or `repos.md` says. Frontend: `gate:seed`,
`gate:tests:run`, `gate:types:run`, `gate:lint:run` (the pre-commit hook needs the evidence files). Services:
`dotnet test`. Fix what fails; do not skip or delete tests to get green.

## 6. Run the change

A changed backend service must be restarted: tell the developer, then `down` and `up -d --local <list>`. Read
`logs <service> --tail 200` after start for exceptions. The frontend hot-reloads; check `logs frontend` for a
compile error.

## 7. Verify

Run `/dss:verify $ticket` (`${CLAUDE_PLUGIN_ROOT}/skills/verify/SKILL.md`) with the checks from the plan. A
failed check means back to step 5, not a softer report.

## 8. Commit and report

Per repo: stage only the files of this change, commit with the repo's subject format (ticket key first, no
trailers, no `Co-Authored-By`), respecting its hooks. The frontend and both services with CLAUDE.md run hooks
that must pass; never `--no-verify`.

Final report, written for someone who was not watching:

- per repo: branch, commit hash, files changed, tests run and their result,
- evidence: the `summary.md` path and the pass/fail list,
- open points: anything the ticket asked for that is not done, decisions the developer must make, follow-ups,
- what has not happened: no push, no PR, no Jira update, and the commands to do each when they want it.
