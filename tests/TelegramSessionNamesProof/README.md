# Session names in chat

The recorder exercises the production chat router, notifier, permission relay and editor,
Bot API client, binding store, user directory and permission store with real Postgres.
Only session lookup and the Bot API destination are substituted. All titles are sent as
plain text without a parse mode, so markup characters need no entity escaping.

A shared, single-line title capped at 80 UTF-16 units is followed by the typing tag.
Missing titles fall back to the tag. Origin headers are repeated after splitting rather
than relying on forum topics or reply context: forwarded and shared-chat continuations
otherwise lose their source. Permission edits look up the current title instead of
keeping an in-memory title cache, which would fail across replicas.

Run the database-backed path coverage with:

```sh
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres' dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~TelegramSessionPathsTests
```

Generated recordings belong outside the repository. No real bot token is needed.

Run the standalone ASP.NET host (production backend classes, disposable HTTP Bot API
recorder and terminal WebSocket peer) and save its JSON outside the checkout:

```sh
dotnet run --project tests/TelegramSessionNamesProof > /tmp/session-names-after.txt
bash tests/TelegramSessionNamesProof/record-baseline.sh /tmp/session-names-baseline /tmp/session-names-before.json
```

Set the connection-string environment variable for both commands. The baseline runner
pins the pre-fix revision, copies only the proof sources into a separate worktree and
adapts the old constructor; it does not backport any formatting fixes. The host serves
and drives its proof routes over loopback HTTP. It covers notification chunks, terminal
events, permission relay posting, expiration, web and button decisions, callback replies,
session lists, switching, status, session creation and failure fallback, forum topics,
delivery errors and the actual router's working indicator after WebSocket terminal input.
Unknown/ambiguous tags are global errors; stale bindings cannot reveal another owner's
name. Long/control-character and missing-title cases test the title boundary.

The session service is a deterministic lookup/creation stand-in, and the pod identity
resolver confirms the loopback terminal peer. This proves chat behavior and real HTTP
serialization, not external bot delivery, live cluster lifecycle, or messaging-client UI.
