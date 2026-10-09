#!/usr/bin/env node
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { AgentHubClient } from './client.mjs';
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

function safeError(error) {
  const message = error instanceof Error ? error.message : '';
  const stable = [
    'agenthub_not_configured', 'agenthub_invalid_url',
    'agenthub_response_too_large', 'agenthub_invalid_json'
  ];
  return stable.find(code => message.includes(code)) ??
    (/agenthub_http_\d{3}/.exec(message)?.[0]) ?? 'agenthub_operation_failed';
}

await server.connect(new StdioServerTransport());
