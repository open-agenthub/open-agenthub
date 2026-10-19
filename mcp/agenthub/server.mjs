#!/usr/bin/env node
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { readSessionFile } from './files.mjs';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { AgentHubClient } from './client.mjs';
import { withCredentialSelection } from './credentials.mjs';
import { safeError, sharingErrorCode } from './errors.mjs';
import { withExpiry } from './expiry.mjs';
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
  agent: z.enum(['Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode']).optional(),
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
    .describe('Memory request in Mi or Gi, e.g. "1Gi"; at least 256Mi by default. "m" means milli, so "512m" is half a byte.'),
  cpuLimit: z.string().max(32).optional()
    .describe('CPU limit, e.g. "4"; omitted = the instance default. At most the instance ceiling and at least cpu.'),
  memoryLimit: z.string().max(32).optional()
    .describe('Memory limit in Mi or Gi, e.g. "8Gi"; omitted = the instance default. At most the instance ceiling and at least memory.'),
  // Text with a unit, converted to seconds before the HTTP call — see expiry.mjs for why.
  autoDeleteAfter: z.string().max(16).optional(),
  autoDeleteFrom: z.enum(['start', 'lastActivity']).optional(),
  // Which stored provider login (credentials_list → accounts) a Subscription session mounts;
  // omitted = the default account.
  credentialId: z.string().max(64).optional(),
  // Comma-separated ids from credentials_list → gitPats. Text, not an array, so an already
  // connected client's call does not fail schema validation (see credentials.mjs). Omitted or
  // "*" = the default token of each host, "none" = no token; at most one per host.
  gitPatIds: z.string().max(4096).optional(),
  // Comma-separated ids from credentials_list → secrets (docs/session-secrets.md). Omitted or "*" =
  // the project's selection, nothing outside a project; "none" = none.
  secretIds: z.string().max(4096).optional(),
  // Whether the hub moves the session to another account when its own hits a usage limit
  // (docs/account-limits.md); omitted = auto.
  accountFailover: z.enum(['auto', 'off']).optional()
});

register('session_create', {
  description: 'Create and start an AgentHub session. Default mode is Interactive: the session '
    + 'starts working on its prompt and stays live, tool requests outside its allow list wait for '
    + 'a person\'s approval, and the response carries "url" — the page to hand to that person. '
    + 'Use Autonomous only for unattended work, where such requests are approved automatically. '
    + 'autoDeleteAfter ("90m", "12h", "3d") makes the session delete itself after that long since '
    + 'its last activity (autoDeleteFrom "lastActivity", the default) or since its start; the '
    + 'response\'s expiresAt says when. '
    + 'credentialId picks the stored provider login and gitPatIds (comma-separated, one per host, "none" for no '
    + 'token) the git tokens the session gets; credentials_list shows what is available to this token. '
    + 'secretIds (comma-separated, "none" for none) the stored secrets it gets as variables or files. '
    + 'accountFailover "off" keeps the session on its account when that hits a usage limit; the '
    + 'default "auto" moves it to another available one.',
  inputSchema: createSchema
}, async (body) => text(sanitizeSession(await client.create(withCredentialSelection(withExpiry(body))))));

register('account_status', {
  description: 'Which provider account a session runs on, whether that account is at its usage limit '
    + '(isExhausted, exhaustedUntil), the session\'s accountFailover setting, and the other stored '
    + 'accounts of the same provider as alternatives — pass one of their ids to account_switch.',
  inputSchema: z.object({ sessionId: z.string().min(1).max(128) })
  // Not a session record; sanitizeSession's allowlist would strip all of it.
}, async ({ sessionId }) => text(await client.accountStatus(sessionId)));

register('account_switch', {
  description: 'Move a running Subscription session to another stored provider account (an id from '
    + 'account_status or credentials_list). The agent restarts with the other login and resumes its '
    + 'conversation. Fails with agenthub_http_409 when the session has no live pod, agenthub_http_400 '
    + 'for an unknown account or an API-key session, credential_not_allowed for an account this token '
    + 'may not use, and agenthub_http_502 when the pod did not take the file.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    credentialId: z.string().min(1).max(64)
  })
}, async ({ sessionId, credentialId }) => text(sanitizeSession(await client.switchAccount(sessionId, credentialId))));

register('credentials_list', {
  description: 'List the credentials a session created with this token may use: provider logins '
    + '(accounts, keyed by agent) with id, label and identity, stored git personal access tokens '
    + '(id, kind, host, label, isDefault — never the token; a host can have several and a session '
    + 'uses one of them), and which API keys are stored. A restricted token sees '
    + 'only what it is allowed to use.',
  inputSchema: z.object({})
  // Not a session record; sanitizeSession's allowlist would strip all of it.
}, async () => text(await client.credentials()));

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

register('session_conversation', {
  description: 'Read the Workspace conversation page. Pass nextOffset back as offset; source selects native entries or scrollback text.',
  inputSchema: z.object({
    id: z.string().min(1).max(128),
    offset: z.number().int().min(0).optional(),
    max: z.number().int().min(1).max(1_000_000).optional()
  })
}, async ({ id, ...page }) => text(await client.conversation(id, page)));

register('session_messages', {
  description: 'Read owned session inbox history, newest first, without marking messages delivered. Pass nextCursor back as cursor.',
  inputSchema: z.object({
    id: z.string().min(1).max(128),
    cursor: z.string().max(256).optional(),
    limit: z.number().int().min(1).max(100).optional()
  })
}, async ({ id, ...page }) => text(await client.messages(id, page)));

register('projects_list', {
  description: 'List the token owner projects for session creation.',
  inputSchema: z.object({})
}, async () => text(await client.projects()));

register('project_get', {
  description: 'Read one project owned by the token owner.',
  inputSchema: z.object({ id: z.string().min(1).max(128) })
}, async ({ id }) => text(await client.project(id)));

register('mcp_servers_list', {
  description: 'List attachable MCP library entries by id and name only; shared entries follow the library licence policy.',
  inputSchema: z.object({})
}, async () => text(await client.mcpServers()));

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

register('context_clear', {
  description: 'As the owner, schedule clearing a running session after its current turn ends and '
    + 'no CLI menu is open, then deliver followUp as the first message of the new conversation. '
    + 'Include a short handover of current state and open tasks. Workspace retains previous history; '
    + 'memory and skills stay. Only the session owner may trigger this operation.',
  inputSchema: z.object({ id: z.string().min(1).max(128), followUp: z.string().min(1).max(16_000) }).strict()
}, async ({ id, followUp }) => text(await client.clearContext(id, followUp)));

register('session_convert', {
  description: 'Continue a finished or paused Autonomous session as an Interactive one, so a person '
    + 'can take the conversation on. Claude and Codex keep the conversation; Cursor and OpenClaw start '
    + 'a new one in the same workspace. Resumes right away unless resume is false. Fails with '
    + 'agenthub_http_409 while the session is running or is not Autonomous.',
  inputSchema: z.object({
    sessionId: z.string().min(1).max(128),
    uiMode: z.enum(['terminal', 'chat']).optional(),
    // Off by default: a person is now there to answer tool requests.
    autoApprove: z.boolean().optional(),
    resume: z.boolean().optional()
  })
}, async ({ sessionId, ...body }) => text(sanitizeSession(await client.convert(sessionId, body))));

register('agents_list', {
  description: 'List your agents (sessions) with title (= agent name), description (what the agent '
    + 'is for), and phase — optionally scoped to one projectId.',
  inputSchema: z.object({ projectId: z.string().max(128).optional() })
}, async ({ projectId }) => text(sanitizeSession(await client.listAgents(projectId))));

register('agent_send', {
  description: 'Send a message/task to one of your agents. "to" is a session id or a unique title '
    + '(scope the lookup with projectId); an ambiguous title fails with the candidate list. By default '
    + 'the agent reads it via its in-session agent_inbox tool. priority pushes it straight into the '
    + 'running agent\'s prompt; interrupt also stops its current work first (implies priority). The '
    + 'result\'s deliveredVia says where it went: inbox, injected, mod or next_turn (handed over when its current turn ends).',
  inputSchema: z.object({
    to: z.string().min(1).max(256),
    message: z.string().min(1).max(4000),
    projectId: z.string().max(128).optional(),
    priority: z.boolean().optional(),
    interrupt: z.boolean().optional()
  })
}, async ({ to, message, projectId, priority, interrupt }) => {
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
  return text(await client.sendAgentMessage(targetId, message, { priority, interrupt }));
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

register('list_files', {
  description: 'List ready files in an accessible AgentHub session. Use read_file to download a file locally.',
  inputSchema: z.object({ sessionId: z.string().regex(/^[A-Za-z0-9_-]{1,128}$/) })
}, async ({ sessionId }) => text({ files: await client.listFiles(sessionId) }));

register('read_file', {
  description: 'Download an AgentHub session file to this computer and return localPath. Small images and text also appear inline.',
  inputSchema: z.object({ sessionId: z.string().regex(/^[A-Za-z0-9_-]{1,128}$/), fileId: z.string().regex(/^[a-f0-9]{32}$/) })
}, input => readSessionFile(client, input));

await server.connect(new StdioServerTransport());
