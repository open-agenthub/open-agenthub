import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const WEB = new Set(['WebBrowser', 'WebFetch', 'WebSearch']);
const SHELL = new Set(['Bash', 'Monitor', 'PowerShell', 'Tmux']);
const NETWORK = /\b(curl|wget|https?|httpie|xh|nc|ncat|socat|telnet|ssh|scp|sftp|rsync|ftp|lftp|rclone|iwr|irm|invoke-webrequest|invoke-restmethod)\b/;
export const MESSAGE = 'Sending the account email was declined automatically in an unattended session.';

// PermissionRequest omits the plugin and its reason. Match the privacy plugin's exact
// account-email predicate instead, so unrelated native approvals keep their existing flow.
export function isAccountEmailRequest(payload, email) {
  if (payload?.hook_event_name !== 'PermissionRequest' || typeof email !== 'string' || !email.includes('@')) return false;
  if (!WEB.has(payload.tool_name) && !SHELL.has(payload.tool_name)) return false;
  const raw = JSON.stringify(payload.tool_input ?? {}).toLowerCase();
  const text = (raw + '\n' + raw.replace(/%([0-9a-f]{2})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))).toLowerCase();
  const account = email.toLowerCase();
  return text.includes(account) && (WEB.has(payload.tool_name) || NETWORK.test(text.replaceAll(account, ' ').replace(/\\(?:\\|[nrt])/g, ' ')));
}

export async function run(payload, env = process.env, fetchImpl = fetch) {
  let email;
  try { email = JSON.parse(fs.readFileSync(path.join(env.HOME, '.claude.json'), 'utf8')).oauthAccount?.emailAddress; } catch { return null; }
  if (!isAccountEmailRequest(payload, email)) return null;
  let unattended = (env.AGENTHUB_MODE || 'interactive') !== 'interactive' || env.AGENTHUB_AUTO_APPROVE === '1';
  if (env.AGENTHUB_CALLBACK_URL && env.AGENTHUB_CALLBACK_TOKEN) {
    try {
      const response = await fetchImpl(env.AGENTHUB_CALLBACK_URL + '/permission', {
        method: 'POST', redirect: 'error', signal: AbortSignal.timeout(5000),
        headers: { 'Content-Type': 'application/json', 'X-Agent-Token': env.AGENTHUB_CALLBACK_TOKEN },
        body: JSON.stringify({ tool: payload.tool_name, kind: 'account-email' })
      });
      if (response.ok) {
        const body = await response.json();
        if (body.decision === 'deny') unattended = true;
        else if (body.decision === 'ask') unattended = false;
      }
    } catch { /* A missing callback must not leave an unattended turn at a native prompt. */ }
  }
  return unattended ? { hookSpecificOutput: { hookEventName: 'PermissionRequest', decision: { behavior: 'deny', message: MESSAGE } } } : null;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  let raw = '';
  for await (const chunk of process.stdin) { raw += chunk; if (raw.length > 1_000_000) process.exit(0); }
  let payload;
  try { payload = JSON.parse(raw); } catch { process.exit(0); }
  const output = await run(payload);
  if (output) {
    console.error('[agent] ' + MESSAGE);
    process.stdout.write(JSON.stringify(output));
  }
}
