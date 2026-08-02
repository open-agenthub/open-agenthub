# API → MCP HTTP gateway (in-process)

**Choice:** In-process ASP.NET under `backend/Library/ApiMcpGateway/` instead of a Node
sidecar under `mcp/api-gateway/`.

**Why:** Catalog lookups, secret decryption, and session-bound tokens already live in the
API process. A single deployable avoids K8s sidecar wiring for v1. Agents reach:

- Catalog API: `{McpGateway:BaseUrl}/mcp/api/{id}`
- Ephemeral session API: `{McpGateway:BaseUrl}/mcp/session/{sessionId}/{name}`

with an `X-AgentHub-Mcp-Token` header issued at spawn.

**Specs:** OpenAPI (`specType: openapi|auto`) maps one tool per operation. GraphQL
(`specType: graphql`, or `auto` when the URL contains `graphql`) maps one tool per
Query/Mutation field from an SDL schema URL or introspection against the endpoint.
