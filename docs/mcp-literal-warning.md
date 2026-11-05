# Raw MCP sharing warning

Raw entries are delivered unchanged by McpConfigAssembler. Library list responses hide
configJson from recipients, which does not prevent agents reading the mounted config.
Warn in both the raw editor and sharing expander so owners see this before either action.
Inspect header/env values rather than guessing credential names; ordinary literal settings
also warn. Empty values and whole variable references (optionally an auth scheme) do not.
Reference syntax and resolution remain the selected agent's responsibility.

Rejected: blocking sharing or stripping values would change existing delivery. Warning on
every raw entry would obscure the actionable case. Echoing field values in the warning
would expose credentials unnecessarily. This is advisory, not credential validation.
