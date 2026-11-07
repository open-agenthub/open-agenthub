# Model discovery proof (#461)

Scripts that run the pinned agent CLIs' own model-list sources against a recording mock of the
provider's service, so account switches, refusals and endpoint lists can be observed without a
real login. See `docs/design/model-discovery.md` for what they established.

- `mock.mjs <port> <log> <rules.json> [bundled-models.json]` — answers `/…/models` per caller
  (account id, key or bearer) from `rules.json`; `{ "status": 403 }` refuses. The log keeps only
  the first 12 characters of a credential. `start-mock.sh <port>` (re)starts it with a pidfile.
- `codex-models.mjs <codex>` — `initialize` + `model/list` over `codex app-server`.
  `mkacct.sh <CODEX_HOME> <account>` writes a fake subscription login pointed at the mock.
  `bundled-models.json` is the pinned CLI's embedded catalogue; extract it from the binary
  (`"models": [` with `"slug"` entries) so the mock can serve real-shaped rows.
- `claude-models.mjs <claude>` — SDK `initialize` over stream-json; prints the `models[]`.

Generated logs and outputs stay out of the repository.
