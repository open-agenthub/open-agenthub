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
