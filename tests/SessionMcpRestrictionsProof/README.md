# Session MCP restrictions proof

Run the host with the .NET 10 SDK:

```sh
dotnet run --project tests/SessionMcpRestrictionsProof --urls http://127.0.0.1:5324
```

The synthetic callback host invokes the real internal controller and policy matcher.
`/config?fixture=<absolute MCP fixture path>&blocked=true` runs the production configuration assembler.
`/token-proof` returns the omitted configuration and the list of issued gateway tokens.
No model account or production service is required. Keep generated output outside Git.

Generate runtime configuration using the running proof host, the actual common-entrypoint
preamble, and all five runtime converters:

```sh
node tests/SessionMcpRestrictionsProof/configuration-matrix.cjs "$PWD" /tmp/mcp-config-proof \
  'http://127.0.0.1:5324/config?fixture=/absolute/path/to/mcp-fixture.mjs&blocked=true'
```

Pass a baseline repository and its proof-host URL to compare configurations. The output
contains isolated homes for each CLI. Verify them using `claude mcp list`, `codex mcp list`,
`opencode mcp list`, `agent mcp list`, and `openclaw mcp list` / `mcp probe`. Set each CLI's
HOME/config path to its generated directory, with a clean environment and no real credentials.
The callback's permission response is synthetic; MCP policy decisions use the real controller.
Real-model transport harnesses and generated evidence are retained separately in session Files.

The Chromium component proof bundles the actual Vue dialog with Vite, loads that bundle
into real Chromium, and supplies synthetic overview responses. It verifies restart text,
built-in names and agent-specific disabled controls. Its script and screenshots are in
session Files, alongside the private runtime transport scripts.

To verify replacing an archived Cursor configuration when all servers are omitted:

```sh
node tests/SessionMcpRestrictionsProof/cursor-empty-config.cjs "$PWD" /tmp/cursor-empty-proof
```

The script executes the actual entrypoint configuration section, stopping before login.
Point the login-free `agent mcp list` command at that output via `CURSOR_CONFIG_DIR`.
