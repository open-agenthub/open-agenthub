import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { SessionsBackendClient } from './client.mjs';
import { withExpiry } from './expiry.mjs';
import { resolveAgentTarget } from './resolve.mjs';
import { sanitizeSession } from './sanitize.mjs';
import { waitForSession } from './wait.mjs';

const text = value => ({
  content: [{ type: 'text', text: JSON.stringify(value) }]
});

const client = new SessionsBackendClient();
const server = new McpServer({ name: 'agenthub_sessions', version: '1.0.0' });

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
  mode: z.enum(['Interactive', 'Autonomous', 'Scheduled']).optional().default('Interactive'),
  agent: z.enum(['Claude', 'Codex', 'Cursor']).optional(),
  authMode: z.enum(['Auto', 'Subscription', 'ApiKey']).optional(),
  repos: z.array(z.object({
    url: z.string().max(2048),
    branch: z.string().max(256).optional()
  })).max(32).optional(),
  projectId: z.string().max(128).optional(),
  parentSessionId: z.string().max(128).optional(),
  schedule: z.string().max(128).optional(),
  mcpConfigJson: z.string().max(512_000).optional(),
  mcpServerIds: z.array(z.string().max(128)).max(64).optional(),
  policy: z.record(z.string(), z.unknown()).optional(),
  image: z.string().max(512).optional(),
  runAsRoot: z.boolean().optional(),
  cpu: z.string().max(32).optional(),
  memory: z.string().max(32).optional(),
  // Text with a unit, converted to seconds before the HTTP call — see expiry.mjs for why.
  autoDeleteAfter: z.string().max(16).optional(),
  autoDeleteFrom: z.enum(['start', 'lastActivity']).optional()
});

register('session_create', {
  description: 'Create and start a child session under this agent session. It joins this session\'s '
    + 'project and, unless mcpConfigJson or mcpServerIds are given, gets the same MCP servers — so it '
    + 'shows up in agents_list and can be reached with agent_send, and has agent_inbox/agent_send '
    + 'itself to report back. Default mode is Interactive: a person can watch and answer it, and '
    + 'tool requests outside its allow list wait for their approval. Use Autonomous only for '
    + 'unattended work, where such requests are approved automatically. hasMcp in the result counts '
    + 'only user MCP servers; the built-in agenthub tools are there in every mode except Scheduled. '
    + 'autoDeleteAfter ("90m", "12h", "3d") makes the child delete itself after that long since '
    + 'its last activity (autoDeleteFrom "lastActivity", the default) or since its start.',
  inputSchema: createSchema
}, async (body) => text(sanitizeSession(await client.create(withExpiry(body)))));

register('session_get', {
  description: 'Get a descendant session by id.',
  inputSchema: z.object({ id: z.string().min(1).max(128) })
}, async ({ id }) => text(sanitizeSession(await client.get(id))));

register('session_list', {
  description: 'List direct child sessions of this agent session.',
  inputSchema: z.object({})
}, async () => text(sanitizeSession(await client.listChildren())));

register('session_wait', {
  description: 'Poll a descendant session until Succeeded or Failed, or until timeout.',
  inputSchema: z.object({
    id: z.string().min(1).max(128),
    timeoutMs: z.number().int().min(1).max(86_400_000).optional(),
    intervalMs: z.number().int().min(1).max(60_000).optional()
  })
}, async ({ id, timeoutMs, intervalMs }) =>
  text(await waitForSession(childId => client.get(childId), id, { timeoutMs, intervalMs })));

register('session_delete', {
  description: 'Delete a descendant session (pod and record). Does not cascade to its children.',
  inputSchema: z.object({ id: z.string().min(1).max(128) })
}, async ({ id }) => text(sanitizeSession(await client.delete(id))));

register('session_convert', {
  description: 'Continue a finished or paused Autonomous descendant session as an Interactive one, so '
    + 'a person can take its conversation on (hand them its id). Claude and Codex keep the conversation; '
    + 'Cursor and OpenClaw start a new one in the same workspace. Resumes right away unless resume is '
    + 'false. Fails with sessions_backend_http_409 while the session is running or is not Autonomous.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    uiMode: z.enum(['terminal', 'chat']).optional(),
    // Off by default: a person is now there to answer tool requests.
    autoApprove: z.boolean().optional(),
    resume: z.boolean().optional()
  })
}, async ({ sessionId, ...body }) => text(sanitizeSession(await client.convert(sessionId, body))));

register('agents_list', {
  description: 'List the agents (sessions) of this session\'s project: id, title (= agent name), '
    + 'description (what the agent is for), phase. Also lists this session\'s parent chain and the '
    + 'children it created, whatever their project.',
  inputSchema: z.object({})
}, async () => text(await client.listProjectAgents()));

register('agent_send', {
  description: 'Send a message/task to a peer agent (anyone in agents_list). "to" is a session id or a '
    + 'unique agent title (see agents_list); an ambiguous title fails with the candidate list. '
    + 'The peer reads it via its agent_inbox tool.',
  inputSchema: z.object({
    to: z.string().min(1).max(256),
    message: z.string().min(1).max(4000)
  })
}, async ({ to, message }) => {
  const agents = await client.listProjectAgents();
  let targetId;
  try {
    targetId = resolveAgentTarget((Array.isArray(agents) ? agents : []).filter(a => !a?.self), to);
  } catch (error) {
    if (error?.code !== 'agent_not_found' && error?.code !== 'agent_title_ambiguous') throw error;
    return {
      content: [{ type: 'text', text: JSON.stringify({ error: error.code, candidates: error.candidates ?? [] }) }],
      isError: true
    };
  }
  return text(await client.sendAgentMessage(targetId, message));
});

register('agent_inbox', {
  description: 'Fetch new messages/tasks sent to this agent and mark them delivered (max 4 per call — '
    + 'call again for more). Pass waitSeconds (up to 60) to long-poll; loop to keep waiting for new tasks. '
    + 'Reply with agent_send.',
  inputSchema: z.object({ waitSeconds: z.number().int().min(0).max(60).optional() })
}, async ({ waitSeconds }) => text(await client.inbox(waitSeconds ?? 0)));

function safeError(error) {
  const message = error instanceof Error ? error.message : '';
  const stable = [
    'sessions_backend_not_configured', 'sessions_backend_invalid_url',
    'sessions_backend_response_too_large', 'sessions_backend_invalid_json', 'autodelete_invalid_duration'
  ];
  return stable.find(code => message.includes(code)) ??
    (/sessions_backend_http_\d{3}/.exec(message)?.[0]) ?? 'sessions_operation_failed';
}

await server.connect(new StdioServerTransport());
