#!/usr/bin/env node
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { AgentHubClient } from './client.mjs';
import { safeError, sharingErrorCode } from './errors.mjs';
import { resolveAgentTarget } from './resolve.mjs';
import { sanitizeSession } from './sanitize.mjs';
import { waitForSession } from './wait.mjs';

const text = value => ({
  content: [{ type: 'text', text: JSON.stringify(value) }]
});

const client = new AgentHubClient();
const server = new McpServer({ name: 'agenthub', version: '1.0.0' });

const register = (name, config, handler) => server.registerTool(name, config, async input => {
  try { return await handler(input ?? {}); }
  catch (error) {
    const code = safeError(error);
    return { content: [{ type: 'text', text: JSON.stringify({ error: code }) }], isError: true };
  }
});

const createSchema = z.object({
  title: z.string().max(256).optional(),
  prompt: z.string().max(100_000).optional(),
  systemPrompt: z.string().max(20_000).optional(),
  mode: z.enum(['Interactive', 'Autonomous', 'Scheduled']).optional().default('Interactive'),
  // OpenClaw was missing here while the backend accepted it, so the stdio server was the one
  // client that could not create an OpenClaw session. The enum is the whole AgentKind set.
  agent: z.enum(['Claude', 'Codex', 'Cursor', 'OpenClaw']).optional(),
  authMode: z.enum(['Auto', 'Subscription', 'ApiKey']).optional(),
  repos: z.array(z.object({
    url: z.string().max(2048),
    branch: z.string().max(256).optional(),
    // Names a Git provider this account has connected; its OAuth token then authenticates the
    // clone and any push. Without it here the field was dropped before the HTTP call, so the
    // stdio server could only ever clone public repositories.
    providerId: z.string().max(128).optional()
    // 16 to match the backend's own cap. The old 32 was the only limit anywhere and did not
    // apply to the REST API, so it described nothing the server actually enforced.
  })).max(16).optional(),
  projectId: z.string().max(128).optional(),
  parentSessionId: z.string().max(128).optional(),
  schedule: z.string().max(128).optional(),
  mcpConfigJson: z.string().max(512_000).optional(),
  policy: z.record(z.string(), z.unknown()).optional(),
  image: z.string().max(512).optional(),
  runAsRoot: z.boolean().optional(),
  cpu: z.string().max(32).optional(),
  memory: z.string().max(32).optional()
});

register('session_create', {
  description: 'Create and start an AgentHub session. Default mode is Interactive: the session '
    + 'starts working on its prompt and stays live, tool requests outside its allow list wait for '
    + 'a person\'s approval, and the response carries "url" — the page to hand to that person. '
    + 'Use Autonomous only for unattended work, where such requests are approved automatically.',
  inputSchema: createSchema
}, async (body) => text(sanitizeSession(await client.create(body))));

register('session_get', {
  description: 'Get a session by id.',
  inputSchema: z.object({ id: z.string().min(1).max(128) })
}, async ({ id }) => text(sanitizeSession(await client.get(id))));

register('session_transcript', {
  description: 'Poll a session transcript for what is new. Pass the previous response\'s nextOffset '
    + 'as offset, so following a long-running session does not re-transfer the whole transcript. '
    + '"running" is false once the session has finished — stop polling then.',
  inputSchema: z.object({
    id: z.string().min(1).max(128),
    offset: z.number().int().min(0).optional(),
    maxChars: z.number().int().min(1).max(1_000_000).optional()
  })
  // Not passed through sanitizeSession: this is a transcript page, not a session record, and the
  // allowlist there would strip every field of it.
}, async ({ id, offset, maxChars }) => text(await client.transcript(id, { offset, maxChars })));

register('session_list', {
  description: 'List sessions for the token owner. Optional filters: parentSessionId, phase.',
  inputSchema: z.object({
    parentSessionId: z.string().max(128).optional(),
    phase: z.string().max(64).optional()
  })
}, async (filters) => text(sanitizeSession(await client.list(filters))));

register('session_wait', {
  description: 'Poll a session until Succeeded or Failed, or until timeout.',
  inputSchema: z.object({
    id: z.string().min(1).max(128),
    timeoutMs: z.number().int().min(1).max(86_400_000).optional(),
    intervalMs: z.number().int().min(1).max(60_000).optional()
  })
}, async ({ id, timeoutMs, intervalMs }) =>
  text(await waitForSession(sessionId => client.get(sessionId), id, { timeoutMs, intervalMs })));

register('session_delete', {
  description: 'Delete a session (pod and record). Does not cascade to its children.',
  inputSchema: z.object({ id: z.string().min(1).max(128) })
}, async ({ id }) => text(sanitizeSession(await client.delete(id))));

register('agents_list', {
  description: 'List your agents (sessions) with title (= agent name), description (what the agent '
    + 'is for), and phase — optionally scoped to one projectId.',
  inputSchema: z.object({ projectId: z.string().max(128).optional() })
}, async ({ projectId }) => text(sanitizeSession(await client.listAgents(projectId))));

register('agent_send', {
  description: 'Send a message/task to one of your agents. "to" is a session id or a unique title '
    + '(scope the lookup with projectId); an ambiguous title fails with the candidate list. The agent '
    + 'reads it via its in-session agent_inbox tool.',
  inputSchema: z.object({
    to: z.string().min(1).max(256),
    message: z.string().min(1).max(4000),
    projectId: z.string().max(128).optional()
  })
}, async ({ to, message, projectId }) => {
  const agents = await client.listAgents(projectId);
  let targetId;
  try {
    targetId = resolveAgentTarget(agents, to);
  } catch (error) {
    if (error?.code !== 'agent_not_found' && error?.code !== 'agent_title_ambiguous') throw error;
    return {
      content: [{ type: 'text', text: JSON.stringify({ error: error.code, candidates: error.candidates ?? [] }) }],
      isError: true
    };
  }
  return text(await client.sendAgentMessage(targetId, message));
});

// ------------------------------------------------------------------ sharing (enterprise)
//
// Same tool names and codes as the remote MCP server's SessionSharingMcpTools. Share answers are
// not passed through sanitizeSession: they are grants and links, not session records, and the
// allowlist there would strip every field of them. The in-pod server deliberately has none of
// these — an agent must not widen who can see its own session (docs/session-sharing-api.md).

const sharing = async fn => {
  try { return await fn(); }
  catch (error) {
    const coded = new Error(sharingErrorCode(error));
    coded.code = coded.message;
    throw coded;
  }
};

const roleSchema = z.enum(['Viewer', 'Collaborator']).optional().default('Viewer');

register('session_share', {
  description: 'Share one of your sessions with another user of this instance. Viewer (default) '
    + 'can watch the terminal and read the transcript; Collaborator can also type. Sharing again '
    + 'with a different role changes it. Fails with license_required on a Community instance and '
    + 'unknown_recipient if that username has never signed in here.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    recipient: z.string().min(1).max(256),
    role: roleSchema
  })
}, ({ sessionId, recipient, role }) => sharing(async () => text(await client.shareWithUser(sessionId, recipient, role))));

register('session_unshare', {
  description: 'Revoke a user\'s access to one of your sessions. Links are revoked separately.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    recipient: z.string().min(1).max(256)
  })
}, ({ sessionId, recipient }) => sharing(async () => text(await client.unshareUser(sessionId, recipient))));

register('session_share_link', {
  description: 'Create a secret link to one of your sessions. Anyone holding the link gets the '
    + 'role, so treat the url as a secret — it is returned exactly once. Returns {url, linkId}.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    role: roleSchema,
    // ISO-8601; the backend reads it and rejects a time in the past.
    expiresAt: z.string().max(64).optional()
  })
}, ({ sessionId, role, expiresAt }) => sharing(async () => {
  const created = await client.createShareLink(sessionId, { role, expiresAt });
  return text({
    url: created?.url,
    linkId: created?.link?.id,
    role: created?.link?.role,
    expiresAt: created?.link?.expiresAt ?? null
  });
}));

register('session_shares', {
  description: 'Who a session of yours is shared with: direct user grants and the links that '
    + 'exist (ids and roles, never the link tokens).',
  inputSchema: z.object({ sessionId: z.string().min(1).max(128) })
}, ({ sessionId }) => sharing(async () => text(await client.listShares(sessionId))));

await server.connect(new StdioServerTransport());
