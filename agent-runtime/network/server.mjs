import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { NetworkBackendClient } from './client.mjs';
import { waitForDecision } from './poll.mjs';

const text = value => ({
  content: [{ type: 'text', text: JSON.stringify(value) }]
});

const client = new NetworkBackendClient();
const server = new McpServer({ name: 'agenthub_network', version: '1.0.0' });

const register = (name, config, handler) => server.registerTool(name, config, async input => {
  try { return await handler(input ?? {}); }
  catch (error) {
    const code = safeError(error);
    return { content: [{ type: 'text', text: JSON.stringify({ error: code }) }], isError: true };
  }
});

register('port_request', {
  description: 'Ask the session owner to open an extra network port for this session: ' +
    'direction "egress" lets this pod reach the port anywhere (e.g. Postgres 5432); ' +
    '"browser_to_agent" lets the session browser reach a server running in this pod. ' +
    'Only ports on the instance allowlist can be requested; the call waits for the ' +
    'owner’s approval (up to timeoutMs).',
  inputSchema: z.object({
    direction: z.enum(['egress', 'browser_to_agent']),
    port: z.number().int().min(1).max(65535),
    protocol: z.enum(['TCP', 'UDP']).optional().default('TCP'),
    reason: z.string().min(1).max(300),
    timeoutMs: z.number().int().min(1_000).max(1_800_000).optional()
  })
}, async ({ direction, port, protocol, reason, timeoutMs }) => {
  const created = await client.requestPort({ direction, port, protocol, reason });
  // Immediate decision: outside the allowlist (deny) or already granted / auto-approved.
  if (created?.decision) {
    return text(result(direction, port, protocol, created.decision, created.reason ?? null));
  }
  const outcome = await waitForDecision(
    () => client.decision(created.id),
    () => client.expire(created.id),
    { timeoutMs });
  const reasonText = outcome.decision === 'allow' ? null
    : outcome.decision === 'deny' ? 'The session owner denied the request.'
    : 'Nobody decided in time. Ask the user to approve the port request, then try again.';
  return text(result(direction, port, protocol, outcome.decision, reasonText));
});

register('port_list', {
  description: 'List the extra network ports already opened for this session.',
  inputSchema: z.object({})
}, async () => text(await client.listPorts()));

function result(direction, port, protocol, decision, reason) {
  return {
    granted: decision === 'allow',
    decision,
    direction,
    port,
    protocol,
    ...(reason ? { reason } : {})
  };
}

function safeError(error) {
  const message = error instanceof Error ? error.message : '';
  const stable = [
    'network_backend_not_configured', 'network_backend_invalid_url',
    'network_backend_response_too_large', 'network_backend_invalid_json'
  ];
  return stable.find(code => message.includes(code)) ??
    (/network_backend_http_\d{3}/.exec(message)?.[0]) ?? 'network_operation_failed';
}

await server.connect(new StdioServerTransport());
