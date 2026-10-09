# Remote MCP server

AgentHub can expose its own sessions over [MCP](https://modelcontextprotocol.io), so an AI
client drives them **as a signed-in user** rather than with a shared secret. It is served at
`<public_url>/mcp` over Streamable HTTP and protected by an OAuth 2.1 authorization server that
AgentHub runs itself and that federates the login to the instance's own OIDC provider.

There is also a stdio server in [`mcp/agenthub`](../mcp/agenthub) that talks to the same
operations using a personal API token. It exposes the same tool names; use it where a local
process is easier than a browser login.

## Enabling it

Off by default, and it fails closed. Set:

```yaml
mcp:
  enabled: true
  # Optional. Empty derives https://<ingress.host>.
  publicUrl: ""
```

The public URL becomes the OAuth issuer and the token audience, so it must be the externally
reachable HTTPS origin — it is taken from configuration rather than from the request host,
because a forwarded `Host` header must not be able to mint tokens for an issuer we do not own.

Without it, `/mcp` answers `503 MCP endpoint disabled` instead of accepting connections. That
is deliberate: an unauthenticated open endpoint would let a client *appear* connected while
every tool call failed.

## Connecting a client

Clients register themselves through OAuth 2.0 Dynamic Client Registration (RFC 7591) — there is
nothing to create by hand.

| Endpoint | Purpose |
|---|---|
| `GET <public_url>/.well-known/oauth-protected-resource` | RFC 9728 metadata, also pointed to by the `WWW-Authenticate` header of every `401` on `/mcp` |
| `GET <public_url>/.well-known/oauth-authorization-server` | Discovery document, including the registration endpoint |
| `POST <public_url>/connect/register` | Dynamic client registration (public client, PKCE, no secret) |
| `GET/POST <public_url>/connect/authorize` | Authorization endpoint; redirects to the OIDC login when no MCP session exists |
| `POST <public_url>/connect/token` | Token endpoint (authorization code + refresh token) |

PKCE is mandatory, and access tokens are bound to the resource `<public_url>/mcp` (RFC 8707),
scope `mcp`.

**Claude Code:**

```bash
claude mcp add --transport http agenthub https://agenthub.example.com/mcp
```

Then run `/mcp` and authenticate — a browser opens, you sign in, and the client stores the
token. Any MCP client with remote/HTTP transport and OAuth support works the same way.

### Prerequisite on the identity provider

The browser login uses a redirect back to `<public_url>/connect/oidc-callback`, so that URI has
to be allowed for the `Oidc:ClientId` client in your IdP (Keycloak: *Clients → agenthub → Valid
redirect URIs*). Without it the login fails at the provider with `invalid_redirect_uri`, before
AgentHub ever sees the request.

`Oidc:ClientSecret` is optional: a public client authenticates with PKCE alone.

### Instances without authentication

When `Oidc:Authority` is empty the backend already treats every caller as the single `dev` user.
The MCP login then has nowhere to federate to and is skipped — the flow goes straight to the
consent screen and issues a token for `dev`, matching how the rest of the API behaves in that
mode.

## Consent

Registration is anonymous, because a client must exist before anyone signs in. Registering
therefore grants nothing: the first time a client asks to act for a user, that user has to
approve it on a consent screen. Approvals are stored per (client, user), so a legitimate client
asks exactly once and reconnects silently afterwards.

Without this step, anyone could register a client pointing at a redirect URI they control and
obtain a signed-in user's token from a single link click, because the browser already carries a
valid session cookie.

Redirect URIs are restricted to `https`, or `http` on loopback (RFC 8252).

## Tools

The same surface as the stdio server:

| Tool | Purpose |
|---|---|
| `session_create` | Create and start a session (defaults to Interactive) |
| `session_get` | Fetch one session by id |
| `session_logs` | Read a session's transcript — what the agent actually printed |
| `session_list` | List your sessions, optionally filtered by parent or phase |
| `session_wait` | Poll until a session reaches Succeeded or Failed |
| `session_delete` | Delete a session; does not cascade to children |
| `agents_list` | Your agents with title, description and phase |
| `agent_send` | Send a message/task to an agent by id or unique title |

`session_logs` is the only way to see what a session did. `kubectl logs` on the pod shows the
entrypoint and the launch command but not the agent's output, which goes to the PTY; and once a
session finishes its pod is gone. The transcript is read from object storage, falling back to
the database copy, so it outlives the pod. It returns the tail by default — a long session's
transcript runs to megabytes, and the part that says how it ended is at the end.

`session_create` takes `runAsRoot` for tasks that need tooling the runtime image does not ship
(it has `node` and `npm`, but no `dotnet`, `docker` or `trivy`). It is off by default because a
root session gives up the read-only root filesystem; the pod stays unprivileged either way.

Every call runs as the user who approved the client, and the session service enforces that
user's ownership exactly as it does for the REST API.

## Operational notes

Registered clients live in Postgres (`mcp_clients`, `mcp_client_approvals`) and are replayed
into the in-memory OpenIddict store at startup. Without that replay, every restart would
invalidate existing registrations and clients would fail their next silent reconnect with
`invalid_client` instead of simply asking the user to sign in again.

The signing and encryption certificates are generated once and kept in Postgres
(`mcp_signing_keys`). Issued tokens therefore survive a restart, and every replica signs with
the same key, so no single-replica restriction applies.

OpenIddict's `AddDevelopment*Certificate` helpers are deliberately not used: they write the
generated certificate into the user's X509 store, which is a path on disk, and the backend
container runs with a read-only root filesystem. The write fails on every request that reaches
an OAuth endpoint — the app starts, then answers `500` on `/healthz` and crashloops. If you see
`CryptographicException: The X509 certificate could not be added to the store`, something has
reintroduced them.
