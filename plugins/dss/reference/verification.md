# Verifying against the running stack

Two tiers. Use the one the ticket's "How can it be tested?" calls for; a backend ticket usually gets the API tier
plus one browser pass over the affected screen, a frontend ticket the browser tier.

## Where writes are allowed

`devenv status --json` → `testing.organizationId` / `testing.organizationName`. Create or change data only inside
that organisation. When it is blank, do read-only checks and say so; do not pick another organisation.

## API tier

Token:

```
T=$(dotnet run --project <DEVENV>/src/Devenv -- token --root <DEVENV>)
```

`TEST_USER` is a claims handler of the test organisation. For medical-advisor flows the developer can put the
MA user's credentials into `TEST_USER`/`TEST_PASSWORD` temporarily, or you ask them to; `secrets.json` also carries
`TEST_MA_USER`/`TEST_MA_PASSWORD` for that purpose. Never print either.

If that fails with a missing `TEST_USER`, ask the developer for a token instead: in the logged-in frontend, open
`http://localhost:3002/api/auth/session` and copy `accessToken`; they paste it into a shell variable themselves.
Never ask them to paste it into the chat.

Requests go through libertine, exactly as the frontend sends them:

```
curl -sS -o /tmp/body.json -w '%{http_code}\n' -H "Authorization: Bearer $T" http://localhost:5151/Vera/EvaluationService/<path>
curl -sS -o /tmp/body.json -w '%{http_code}\n' -H "Authorization: Bearer $T" http://localhost:5151/libertine/<path>
```

For each check record: method, path, status, and the one or two fields of the body that prove the behaviour.
Then read the local service's log for the same moment (`devenv logs <service> --tail 200`) and quote the lines
that show the request reached your local code, plus any exception.

## Browser tier (Chrome DevTools MCP)

The `chrome-devtools` MCP server drives the developer's Chrome. Pattern:

1. `list_pages`; reuse a tab on `localhost:3002` if there is one, else `new_page` with
   `http://localhost:3002/`.
2. If the login page shows, tell the developer to log in (Okta e-mail code) and wait with `wait_for` on a
   dashboard element. Never type credentials.
3. Drive the flow with `take_snapshot` (to find elements by role and name), `click`, `fill`, `navigate_page`.
   Wait for the network to settle before reading results.
4. After each step: `list_console_messages` and keep only errors; `list_network_requests` and keep requests to
   `localhost:5151` with status 400 or above (two 403s from `/Vera/MedicalAdvisorNetwork/...` on the case page
   are a known dev02 issue, note them and move on). `take_screenshot` at the state the ticket describes.
5. Correlate with `devenv logs <service> --tail 200` for a local service.

Known behaviour, learned on the first trial (2026-09-16): opening a document from the case page opens a
**new tab** (`/document-page/multi-page?...`), so run `list_pages` and address the new page id afterwards. The
PDFTron viewer logs one `Uncaught (in promise)` from `TabManager.js` (`_writeToDB`, its IndexedDB tab
persistence) on every document open; third-party noise, note it and move on. A 404 for
`/expertise/<area>.svg` is a missing icon for a test expertise area, not an API failure. Requests to
`/Vera/<Service>/**` for a `--local` service show up in that service's log as `Request finished ... localhost:5151/...`.

## Evidence

Folder: `<DEVENV>/.state/evidence/<TICKET>/` (gitignored). Contents:

- `summary.md`: ticket, date, what was checked, result per check (pass/fail), links to the files below,
  open points. Written for a reviewer who was not there.
- `api.md`: one block per request (command without the token, status, relevant body fields).
- `browser-<n>.png`: screenshots, numbered in flow order, each referenced from `summary.md`.
- `console.txt`, `network.txt`: the filtered console errors and failed requests.
- `log-<service>.txt`: the log excerpt.

Quote `summary.md` in the final report. Do not claim a check passed unless its evidence file exists.
