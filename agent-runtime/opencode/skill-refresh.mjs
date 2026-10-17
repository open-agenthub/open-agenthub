import fs from 'node:fs';
import path from 'node:path';
import { isInteractive } from '../common/turn-notify-hook.mjs';

/**
 * Makes OpenCode see skills the session agent pulled while it runs.
 *
 * OpenCode scans ~/.claude/skills once per instance and caches the result (Skill.state is an
 * InstanceState in 1.18.35), so a pulled skill stays invisible until the instance is rebuilt.
 * Verified with the real 1.18.35 TUI and a mock model: a skill added between two turns is missing
 * from the second turn's request; after POST /instance/dispose it is there, and the conversation
 * carries on — the TUI rebuilds the instance on its next request.
 *
 * So the plugin disposes the instance itself, through its own client, when the manifest's
 * generation moved (skills.js bumps it only when a directory changed) and no session is busy.
 * Disposing mid-turn would abort the turn, which is why busy sessions defer it; a pull that lands
 * while the agent works is picked up when the turn ends.
 *
 * Rejected: a fixed --port on the TUI so the session agent could call the endpoint. That opens an
 * unauthenticated server for the whole pod and still needs the busy state, which only an
 * in-process plugin sees.
 */

const POLL_MS = 5_000;

// Module-level, not per plugin call: OpenCode calls the plugin again for every new instance, and
// disposing one we did not trigger (a config reload) leaves the old timer behind. One refresher
// per process, replaced by the newest instance, means one timer and one client that is current.
let active = null;

export function readGeneration(manifestPath, fsImpl = fs) {
  try {
    const parsed = JSON.parse(fsImpl.readFileSync(manifestPath, 'utf8'));
    return Number.isSafeInteger(parsed.generation) ? parsed.generation : 0;
  } catch {
    return 0;
  }
}

export function createSkillRefresher({
  client,
  env = process.env,
  fsImpl = fs,
  setIntervalImpl = setInterval,
  clearIntervalImpl = clearInterval,
  pollMs = POLL_MS
} = {}) {
  const home = env.HOME;
  const manifestPath = home ? path.join(home, '.claude', 'skills', '.agenthub-managed.json') : null;
  const busy = new Set();
  let seen = manifestPath ? readGeneration(manifestPath, fsImpl) : 0;
  let disposing = false;
  let timer = null;

  function stop() {
    if (timer !== null) clearIntervalImpl(timer);
    timer = null;
  }

  function check() {
    if (timer === null || disposing || busy.size > 0) return false;
    const generation = readGeneration(manifestPath, fsImpl);
    if (generation === seen) return false;
    seen = generation;
    if (!client || !client.instance || typeof client.instance.dispose !== 'function') return false;
    disposing = true;
    // This instance is going away; the plugin call for the next one starts a fresh refresher.
    stop();
    Promise.resolve()
      .then(() => client.instance.dispose())
      .catch(() => {})
      .finally(() => { disposing = false; });
    return true;
  }

  function onEvent(event) {
    if (!event || typeof event.type !== 'string') return;
    const sessionID = event.properties && event.properties.sessionID;
    if (event.type === 'session.status' && typeof sessionID === 'string') {
      const type = event.properties.status && event.properties.status.type;
      if (type === 'idle') busy.delete(sessionID);
      else busy.add(sessionID);
    } else if (event.type === 'session.idle' && typeof sessionID === 'string') {
      busy.delete(sessionID);
    } else if (event.type === 'session.deleted' && event.properties && event.properties.info) {
      busy.delete(event.properties.info.id);
    } else {
      return;
    }
    // Picked up at the end of a turn rather than up to one poll later.
    check();
  }

  // Only interactive: `opencode run` is one turn, and its end is the process's end, where a
  // dispose would race the shutdown for nothing.
  if (manifestPath && isInteractive(env) && env.AGENTHUB_SKILLS_SYNC_SECONDS !== '0') {
    timer = setIntervalImpl(check, pollMs);
    if (timer && typeof timer.unref === 'function') timer.unref();
  }

  return { onEvent, check, stop, busy };
}

/** The process-wide refresher; a newer instance's call replaces the previous one. */
export function installSkillRefresher(options) {
  if (active) active.stop();
  active = createSkillRefresher(options);
  return active;
}
