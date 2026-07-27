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
      return { timedOut: false, phase: last.phase, id: last.id ?? id, ...pickSession(last) };
    }
    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      return { timedOut: true, phase: last?.phase, id: last?.id ?? id };
    }
    await sleep(Math.min(intervalMs, remaining));
    last = await get(id);
  }
}

function pickSession(info) {
  if (!info || typeof info !== 'object') return {};
  const { phase, id, timedOut, ...rest } = info;
  return rest;
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
