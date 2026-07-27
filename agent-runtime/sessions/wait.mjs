import { sanitizeSession } from './sanitize.mjs';

const TERMINAL = new Set(['Succeeded', 'Failed']);
const DEFAULT_INTERVAL_MS = 2_000;
const DEFAULT_TIMEOUT_MS = 30 * 60 * 1_000;

export async function waitForSession(get, id, options = {}) {
  const intervalMs = options.intervalMs ?? DEFAULT_INTERVAL_MS;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const deadline = Date.now() + timeoutMs;
  let last = await get(id);

  while (true) {
    if (TERMINAL.has(last?.phase)) {
      const session = sanitizeSession(last) ?? {};
      return {
        timedOut: false,
        ...session,
        phase: last.phase,
        id: last.id ?? id
      };
    }
    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      return { timedOut: true, phase: last?.phase, id: last?.id ?? id };
    }
    await sleep(Math.min(intervalMs, remaining));
    last = await get(id);
  }
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
