const DEFAULT_INTERVAL_MS = 3_000;
const DEFAULT_TIMEOUT_MS = 5 * 60 * 1_000;

const TERMINAL = new Set(['allow', 'deny', 'expired']);

/**
 * Polls the decision for a pending port request until it is decided or the
 * timeout elapses. `getDecision` resolves to { decision } ("pending" while open);
 * `expire` marks the request expired backend-side and resolves to the FINAL
 * decision — an approval that won the race against the timeout is still honored.
 */
export async function waitForDecision(getDecision, expire, options = {}) {
  const intervalMs = options.intervalMs ?? DEFAULT_INTERVAL_MS;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const deadline = Date.now() + timeoutMs;

  while (true) {
    const { decision } = await getDecision() ?? {};
    if (TERMINAL.has(decision)) return { decision, timedOut: false };
    const remaining = deadline - Date.now();
    if (remaining <= 0) break;
    await sleep(Math.min(intervalMs, remaining));
  }

  const { decision } = await expire() ?? {};
  return { decision: decision ?? 'expired', timedOut: decision !== 'allow' };
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
