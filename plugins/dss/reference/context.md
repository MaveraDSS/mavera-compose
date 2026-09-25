# Understanding a ticket: always gather the context first

A ticket's own text is rarely the whole story. Before proposing services, a plan or a change, collect the
following and summarise it in a few lines. Do this every time; skip a step only when the ticket has none of it.

## 1. The ticket itself

Atlassian MCP `getJiraIssue` with fields `summary`, `description`, `customfield_10064` (the DSS spec: task
description, DoD, how to test, unit-test criteria), `status`, `parent`, `subtasks`, `issuelinks`, `comment`,
`labels`, `fixVersions`. Read the comments: implementation notes, decisions, "tested on dev02" remarks and review
findings live there, not in the description.

## 2. Related tickets

- **Parent** and its other **sub-tasks**: the ticket is one slice of a larger change; sibling sub-tasks tell you
  what is already done and what is still coming (a frontend sub-task often depends on a backend one).
- **Issue links** (`relates to`, `blocks`, `is blocked by`, `tests`, `is tested by`, `duplicates`): fetch each linked
  issue's summary, status and, when it is the direct predecessor, its spec and comments.
- **Keys mentioned in the text**: every `DSS-1234` in summary, description, spec or comments; fetch summary and
  status for each ("follow-up to DSS-5224" means DSS-5224's spec is required reading).
- Stop at two hops: the ticket, what it links to, and what those link to when they are parents or predecessors.

## 3. Previous work in the repositories

The same key, or the predecessor's key, usually already has code. In every candidate repo (and the frontend):

```
git -C <repo> fetch --quiet --all
git -C <repo> branch -r --list '*DSS-1234*' '*dss-1234*'
git -C <repo> log --all -i --grep='DSS-1234' --oneline
```

- Branches and commits for the ticket itself: work in progress or an earlier attempt; read the diff
  (`git -C <repo> log --all -i --grep='DSS-1234' -p --stat`) before starting over.
- Branches and commits for the predecessor tickets: the shape of the previous change, its tests and its migration
  numbers tell you the conventions to follow and the files you will touch.
- Pull requests: when `gh` is available, `gh pr list --repo MaveraDSS/<repo> --search 'DSS-1234' --state all` and
  `gh pr view <n> --repo MaveraDSS/<repo> --comments` for review discussions. Without `gh`, the merge commits from
  `git log` name the pull request number (`(#123)`), and the ticket's comments often link it.

## 4. What to write down

Before the first proposal, a short block in the chat:

- What the ticket asks for, in one sentence, and its definition of done.
- Parent and siblings: what is done, what is pending, what this ticket depends on.
- Predecessor tickets and where their code landed (repo, branch, pull request).
- Existing branches or commits for this ticket, and whether to continue them.
- Anything in the comments that changes the work (decisions, test findings, scope cuts).

Then the proposal. A plan that ignores a linked predecessor or an existing branch is wrong before it starts.
