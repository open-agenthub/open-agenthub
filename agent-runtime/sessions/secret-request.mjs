import fs from 'node:fs';
import path from 'node:path';

// The agent's side of a secret request (docs/session-secrets.md). The owner is asked in the web
// app and every chat relay, types the value there, and grants it to this session; this side
// learns only whether that happened, and under which name it now finds the secret. It never reads
// a value: not from the hub, which does not send one, and not from ~/.secrets either — whether a
// file is there is all it checks.

const POLL_MS = 3_000;
const DEFAULT_WAIT_MINUTES = 10;
const MAX_WAIT_MINUTES = 30;
// The kubelet carries a changed secret volume into the pod within its sync period, about a minute.
const FILE_APPEAR_MS = 120_000;

const NOT_PROVIDED = { status: 'not provided' };

export function secretsDir(env = process.env) {
  return env.AGENTHUB_SECRETS_DIR || path.join(env.HOME || '/home/agent', '.secrets');
}

/** Where the agent finds a secret, and whether it is there yet. */
export function availability(secret, env = process.env, exists = fs.existsSync) {
  if (secret.delivery === 'file') {
    const file = path.join(secretsDir(env), secret.name);
    return { availableAs: file, present: exists(file) };
  }
  return { availableAs: `$${secret.name}`, present: typeof env[secret.name] === 'string' };
}

function provided(answer, env, exists) {
  const secret = { name: answer.name, delivery: answer.delivery };
  const where = availability(secret, env, exists);
  const result = { status: 'provided', name: secret.name, delivery: secret.delivery, availableAs: where.availableAs };
  if (where.present) return result;
  result.note = secret.delivery === 'file'
    ? 'The file is on its way into this pod and should appear within a minute or two.'
    // A container's environment is fixed when it starts; the owner was offered a restart.
    : 'The variable is set for this session from its next start; ask the owner to restart it if you need it now.';
  return result;
}

/**
 * Asks for a secret and waits for the owner's answer: provided, or "not provided" for a refusal,
 * a timeout or a request the owner never saw. Bounded by `waitMinutes` (default 10, at most 30).
 */
export async function requestSecret(client, input, options = {}) {
  const env = options.env ?? process.env;
  const exists = options.exists ?? fs.existsSync;
  const sleep = options.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const now = options.now ?? Date.now;
  const pollMs = options.pollMs ?? POLL_MS;
  const minutes = Math.min(MAX_WAIT_MINUTES, Math.max(0, Number(input.waitMinutes ?? DEFAULT_WAIT_MINUTES) || 0));
  const deadline = now() + minutes * 60_000;

  let answer = await client.secretRequest({
    name: input.name, delivery: input.delivery ?? 'env', purpose: input.purpose
  });
  while (answer?.status === 'pending') {
    if (now() >= deadline) {
      answer = await client.secretRequestExpire(answer.id);
      break;
    }
    await sleep(pollMs);
    answer = await client.secretRequestStatus(answer.id);
  }
  if (answer?.status !== 'provided' || !answer.name) return NOT_PROVIDED;

  // A file granted a moment ago is usually not in the pod yet; waiting for it here saves the agent
  // a failing read and a second round of guessing.
  if (answer.delivery === 'file') {
    const fileDeadline = now() + FILE_APPEAR_MS;
    while (!availability(answer, env, exists).present && now() < fileDeadline) await sleep(pollMs);
  }
  return provided(answer, env, exists);
}

/** The listing `secrets_list` returns: names, delivery and where to find each, never a value. */
export async function listSecrets(client, env = process.env, exists = fs.existsSync) {
  const answer = await client.secrets();
  const secrets = Array.isArray(answer?.secrets) ? answer.secrets : [];
  return {
    secrets: secrets.map(secret => ({
      id: secret.id, name: secret.name, delivery: secret.delivery,
      ...(secret.description ? { description: secret.description } : {}),
      ...availability(secret, env, exists)
    }))
  };
}
