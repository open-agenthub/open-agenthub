# Session MCP restrictions proof

Run the host with the .NET 10 SDK:

```sh
dotnet run --project tests/SessionMcpRestrictionsProof --urls http://127.0.0.1:5324
```

The synthetic callback host invokes the real internal controller and policy matcher.
`/config?fixture=<absolute MCP fixture path>&blocked=true` runs the production configuration assembler.
`/token-proof` returns the omitted configuration and the list of issued gateway tokens.
No model account or production service is required. Keep generated output outside Git.
