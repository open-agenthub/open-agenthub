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

## Deleted sessions (#365)

`DeletedSessionScenario.cs` seeds seventeen sessions bound to one forum chat plus three bindings
of sessions deleted before the fix, then drives `/sessions`, the prune, a delete through the
real `KubernetesSessionService.DeleteSessionAsync` (the method the REST, MCP and internal paths
call), `/sessions` again, a plain message, `/status` and `/use` for the deleted session, the real
expiry sweep with the real notifier, a message in the expired session's topic, and a switch to
another session. Session and binding stores are the production Postgres ones; the Bot API and
the Kubernetes API are answered by one loopback stand-in, whose cluster has no pods left.

```sh
dotnet run --project tests/TelegramSessionNamesProof -- --deleted-sessions > /tmp/deleted-after.json
bash tests/TelegramSessionNamesProof/record-deleted-baseline.sh /tmp/deleted-baseline /tmp/deleted-before.json
```

The first prints every step (what was sent, every `sendMessage`, the row counts) and then
asserts; the second runs the same steps at the revision before the fix, where the assertions
fail by design. The scenario also runs as `TelegramSessionPathsTests.DeletedSessionsLeaveTheChat`.


## Pinned session (#398)

`PinnedSessionScenario.cs` runs `/use A`, questions from B and from a new session C, a plain
message, a message in B's forum topic, the real `KubernetesSessionService.DeleteSessionAsync` of
A, another question from B, a plain message and `/status`, then `/unpin`. It runs once in a
private chat and once in a forum supergroup. Each session has its own stand-in pod on its own
loopback address (127.0.0.2/.3/.4), and the production `AgentTerminal` WebSocket types into it,
so every step records which session's terminal received the plain message.

```sh
dotnet run --project tests/TelegramSessionNamesProof -- --pinned-session > /tmp/pinned-after.json
bash tests/TelegramSessionNamesProof/record-pinned-baseline.sh /tmp/pinned-baseline /tmp/pinned-before.json
```

The baseline runs the same steps at `680f9e1` without assertions. It also runs as
`TelegramSessionPathsTests.UseKeepsTheChosenSessionWhileOthersPost`.

## Displayed tag input (#396)

The `use-hash` and `use-bang-hash` paths send `/use #AB12` and `!use #ab12`
through the real router; `use-empty` and `use-bang-empty` assert the usage hint.
Run the same proof sources against the parent revision with `--record-baseline`
to record the previous “No session matches that tag.” response without assertions.
Only the shared formatting and relay production files differ for that comparison.

`bash tests/TelegramSessionNamesProof/record-tag-baseline.sh /tmp/tag-baseline /tmp/tag-before.json`
creates an isolated worktree at the parent revision and copies only the scenario sources.
Set the database connection string and put the .NET SDK on PATH as above.
