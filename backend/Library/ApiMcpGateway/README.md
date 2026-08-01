# API → MCP HTTP gateway (in-process)

**Choice:** In-process ASP.NET under `backend/Library/ApiMcpGateway/` instead of a Node
sidecar under `mcp/api-gateway/`.

**Why:** Catalog lookups, secret decryption, and session-bound tokens already live in the
API process. A single deployable avoids K8s sidecar wiring for v1. Agents reach
`{McpGateway:BaseUrl}/mcp/api/{id}` with an `X-AgentHub-Mcp-Token` header issued at spawn.

OpenAPI only in this folder for Task 8; GraphQL + ephemeral session routes are Task 9.
