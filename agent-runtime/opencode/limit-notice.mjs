import { isInteractive } from '../common/turn-notify-hook.mjs';

// The retry reasons OpenCode 1.18.35 attaches when an OpenCode Go or Zen account, not the
// request, is at its limit (opencode/driver.js has the sentences).
const ACCOUNT_LIMIT_REASONS = new Set(['account_rate_limit', 'free_tier_limit']);

/**
 * Puts OpenCode's limit notice where the session agent's limit detector can read it, in
 * unattended mode. There `opencode run` prints nothing while a session is in its retry status
 * (verified on 1.18.35 with a mock model answering 429 GoUsageLimitError): it waits out the
 * retry-after, hours for a Go window, and the run looks like a run that is thinking. The plugin
 * receives the same status as a `session.status` event, with the sentence the TUI would show.
 *
 * Interactive sessions print nothing: the TUI shows the status itself, and a line on stderr
 * would be drawn over its screen. Once per process, because OpenCode retries a free-tier limit
 * several times and the detector only needs the first.
 */
export function createLimitNotice({ env = process.env, write = text => process.stderr.write(text) } = {}) {
  let printed = false;
  return function onEvent(event) {
    if (printed || !event || event.type !== 'session.status') return;
    const status = event.properties && event.properties.status;
    if (!status || status.type !== 'retry' || typeof status.message !== 'string') return;
    if (!status.action || !ACCOUNT_LIMIT_REASONS.has(status.action.reason)) return;
    if (isInteractive(env)) return;
    printed = true;
    write('\n[agenthub] ' + status.message + '\n');
  };
}
