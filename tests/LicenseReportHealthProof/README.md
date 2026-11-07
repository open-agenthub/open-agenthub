# Licence report health proof

Run with the .NET 10 SDK and a disposable real Postgres database:

```sh
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres' \
  dotnet run --project tests/LicenseReportHealthProof
```

The proof creates and drops an isolated schema, starts an actual HTTP licence-service
stand-in, runs the production SeatUsageReporter, LicenseStore and EnterpriseLicense, and
reads the production AdminController over HTTP. A generated local signing key is supplied
only through the existing verifier test constructor; the production compiled key is unchanged.

Checks cover initial never-reported state, signed expiry, rejected HTTP reports, missing and
invalid renewals preserving the previous token, shared database persistence through a new
store instance, successful clearing, a real connection-refused failure, preserving last success,
configurable warning lead time, zero lead time and expired tokens. No output is committed.

## Chromium against the real API

1. Run the command above with `-- --serve` appended. It keeps the real rejected-report state
   running on port 8080 until shutdown.
2. Run `npm ci --prefix browser-runtime`; copy `tests/LicenseReportHealthProof/ui.html` to
   `frontend/license-proof.html`, then run the frontend Vite dev server on port 5173 with
   `--host 0.0.0.0`. Copy rather than symlink: Vite caches inline HTML module resolution.
3. Run `PROOF_CDP_URL="$CDP_URL" PROOF_UI_URL="$UI_URL" node tests/LicenseReportHealthProof/browser.mjs`
   using your browser's CDP URL and the frontend URL reachable from that browser. The script
   checks actual Admin API data, warning semantics, refresh, and 1280/390px overflow. Set
   `PROOF_SCREENSHOT_PATH` to save a screenshot outside tracked source; upload it to Files.
4. Stop the host/dev server and remove the copied `frontend/license-proof.html`.

The service proof also checks actual HTTP timeout recording and that intentional shutdown
cancellation does not overwrite the last error. The browser entry renders the production
Admin component with the production API client, without the rest of the application shell.
