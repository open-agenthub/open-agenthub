'use strict';
const crypto = require('node:crypto');
const { createWorkspaceDiff } = require('./workspace-diff');
const { listSlashCommands } = require('./slash-commands');

const { loadDriver, validateDriver } = require('./driver-contract');
const { LocalFileStore, LocalFileError } = require('../files/local-store');
const { AttachmentMaterializer } = require('../files/materialize');
const credentials = require('./credential-install');
const { injectTerminalInput, formatFleetMessage } = require('./terminal-inject');
const { createNextTurnDelivery, IDLE_QUIET_MS } = require('./next-turn');
const { createContextClear } = require('./context-clear');
const { createLimitDetector, stripAnsi, toIso } = require('./limit-detector');
const { signatureScript, archiveScript, uploadArgs } = require('./state-archive');
const { createSecretMask } = require('./secret-mask');
const gitIdentity = require('./git-identity');

// The scrollback window, in characters. The hub stores and pages exactly this much
// (ScrollbackLimits.MaxChars in backend/Services); the two have to agree, or a resume seeded
// from the hub's copy comes back shorter than what this process uploaded.
const MAX_BUFFER = 1_000_000;
// How long a stopped agent gets to exit on its own before the restart forces it; long enough
// for a CLI to flush its session file, short enough that a wedged one does not stall the swap.
const RESTART_KILL_GRACE_MS = 8_000;
// How many 30s persistence ticks pass between two state archives. Scrollback and transcript are
// small and go up on every tick; the state directory is tens of megabytes to compress, and a
// resume needs it only as recent as the last turn. Two minutes bounds what a pod that dies
// without its SIGTERM loses, at a quarter of the compression (docs/state-archive.md).
const STATE_ARCHIVE_EVERY_TICKS = 4;
// Where an entrypoint records the pid of its credential watcher, and how long the server waits
// for that watcher's last upload: a little over the watcher's own 5s flush bound.
const DEFAULT_WATCHER_PIDFILE = '/tmp/agenthub-auth-watcher.pid';
const WATCHER_FLUSH_WAIT_MS = 6_000;
const WATCHER_POLL_MS = 100;
// Protocol chatter that the chat UI only needs live, never on replay.
const TRANSIENT_CHAT_EVENTS = new Set(['stream_event', 'control_response', 'control_request']);
// A fleet message as the hub posts it: a 4000-character body plus a few fields.
const MAX_MESSAGE_BYTES = 64 * 1024;
// How long after its last poll the provider's in-process mod counts as alive. It polls every 3 s; a
// window of five polls survives a slow turn without handing a message to a mod that is gone.
const MOD_HEARTBEAT_TTL_MS = 15_000;
// A mod's limit report: kind, percent, reset time — small by construction.
const MAX_LIMIT_REPORT_BYTES = 4 * 1024;
// How long a restarted TUI gets to draw its prompt box before a message held back during the
// restart is typed into it. Text written earlier lands in a terminal nothing reads yet.
const RESTART_INPUT_DELAY_MS = 3_000;
// How many 30s persistence ticks pass between two refreshes of the presigned upload urls: six
// hours, half of the twelve the hub signs them for, so one failed refresh still leaves time
// for the next before the old urls stop working.
const URL_REFRESH_EVERY_TICKS = 720;
// The most transcript one GET /agenthub/transcript returns. A first read of a long session
// takes a few of these; the hub asks again from where the last one stopped.
const MAX_TRANSCRIPT_CHUNK = 8 * 1024 * 1024;
// A POST /agenthub/attachments body: at most five 32-character file ids.
const MAX_ATTACHMENT_REQUEST_BYTES = 4 * 1024;
// What a path typed into a CLI's input box may contain. The CLIs recognise a pasted image path
// only as the whole paste, and a space or quote would need each CLI's own escaping; the composer
// names its uploads within this set, so anything else is refused rather than half-recognised.
const TERMINAL_PATH = /^\/[A-Za-z0-9._/-]+$/;
// The longest reason a credential swap may carry into the scrollback.
const MAX_SWITCH_REASON_CHARS = 300;
// How often the skill library is pulled while the session runs. A minute is "shortly" for a
// skill another session just saved, and with the ETag an unchanged library is one 304.
const DEFAULT_SKILLS_SYNC_SECONDS = 60;

/** AGENTHUB_SKILLS_SYNC_SECONDS; 0 turns the live pull off and leaves only the one at start. */
function skillsSyncIntervalMs(env) {
  const raw = env.AGENTHUB_SKILLS_SYNC_SECONDS;
  const seconds = raw === undefined || raw === '' ? DEFAULT_SKILLS_SYNC_SECONDS : Number(raw);
  if (!Number.isFinite(seconds) || seconds <= 0) return 0;
  return Math.max(10, seconds) * 1000;
}

function createCommonServer(options = {}) {
  const env = options.env || process.env;
  const driver = validateDriver(options.driver);
  const supplied = options.dependencies || {};
  const pty = supplied.pty || require('node-pty');
  const spawnProcess = supplied.spawn || require('node:child_process').spawn;
  const WebSocketServer = supplied.WebSocketServer || require('ws').WebSocketServer;
  const http = supplied.http || require('node:http');
  const execFile = supplied.execFile || require('node:child_process').execFile;
  const fs = supplied.fs || require('node:fs');
  const fetchImpl = supplied.fetch || fetch;
  const processLike = supplied.process || process;
  const setIntervalImpl = supplied.setInterval || setInterval;
  const clearIntervalImpl = supplied.clearInterval || clearInterval;
  const setTimeoutImpl = supplied.setTimeout || setTimeout;
  const now = supplied.now || Date.now;
  const fileStore = supplied.fileStore || new LocalFileStore({
    root: env.AGENTHUB_FILE_ROOT || '/workspace/.agenthub/files' });
  const attachmentMaterializer = supplied.attachmentMaterializer ||
    new AttachmentMaterializer({ env, fetch: fetchImpl, store: fileStore });
  const workspaceDiff = supplied.workspaceDiff || createWorkspaceDiff(env.AGENTHUB_WORKSPACE_ROOT || '/workspace');
  const secretMask = supplied.secretMask || createSecretMask({ env, fs });
  const installGitIdentity = supplied.installGitIdentity ||
    (payload => gitIdentity.install(payload, { setupCliAuth: require('node:path').join(__dirname, 'setup-cli-auth.sh') }));

  const preparation = driver.prepare(env);
  const childEnv = preparation && preparation.childEnv;
  if (childEnv !== undefined && (!childEnv || typeof childEnv !== 'object' || Array.isArray(childEnv))) {
    throw new Error('Agent driver prepare childEnv must be an object');
  }
  // This scopes values to the provider PTY, not to the provider executable alone:
  // commands and tools launched by the provider inherit this environment. Code
  // running as the agent user is therefore inside the credential trust boundary.
  const agentEnv = childEnv === undefined ? env : { ...env, ...childEnv };

  const port = parseInt(env.AGENTHUB_PORT || '7681', 10);
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const hasRepo = env.AGENTHUB_HAS_REPO === '1';
  const callback = env.AGENTHUB_CALLBACK_URL || '';
  const token = env.AGENTHUB_CALLBACK_TOKEN || '';
  // Replaced by refreshArtifactUrls before they expire; the environment only holds the first set.
  let statePut = env.AGENTHUB_STATE_PUT_URL || '';
  let scrollPut = env.AGENTHUB_SCROLLBACK_PUT_URL || '';
  let transcriptPut = env.AGENTHUB_TRANSCRIPT_PUT_URL || '';
  const home = env.HOME || '/home/agent';
  const workdir = env.AGENTHUB_WORKDIR || (hasRepo ? '/workspace/repo' : '/workspace');
  const cwd = fs.existsSync(workdir) ? workdir : '/workspace';
  const insecureStorage = env.AGENTHUB_S3_INSECURE === '1';
  const configuredFileMax = Number.parseInt(env.AGENTHUB_FILE_MAX_BYTES || '', 10);
  const fileMaxBytes = Number.isSafeInteger(configuredFileMax) && configuredFileMax > 0
    ? configuredFileMax : 50 * 1024 * 1024;

  let scrollback = '';
  const clients = new Set();
  let exited = false;
  let term;
  let chat;
  let retriedFresh = false;
  let attemptedResume = false;
  let attemptOutput = '';
  let launchedAt = 0;
  let chatMode = false;
  let controlCounter = 0;
  let chatDelivery = Promise.resolve();
  let pendingChatDeliveries = 0;
  let outstandingRuntimeTurns = 0;
  let runtimeDraining = false;
  let promptSent = false;
  let firstLaunchedAt = 0;
  let transcriptFile = null;
  let conversationCleared = false;
  const conversationStateFile = require('node:path').join(home, driver.stateDir, 'agenthub-conversation.json');
  try {
    const saved = JSON.parse(fs.readFileSync(conversationStateFile, 'utf8'));
    transcriptFile = typeof saved.file === 'string' ? saved.file : null;
    conversationCleared = saved.cleared === true;
  } catch {}
  const conversationHistoryFile = require('node:path').join(home, driver.stateDir, 'agenthub-history.jsonl');
  try { if (fs.statSync(conversationHistoryFile).size > 0) conversationCleared = true; } catch {}
  function conversationHistory() {
    try { return fs.readFileSync(conversationHistoryFile, 'utf8').toString(); } catch { return ''; }
  }
  let transcriptUploaded = { size: -1, mtimeMs: -1 };
  // False while a resumed session's transcript may still be behind the hub's copy: until
  // catchUpTranscript has settled, the file is neither served to the hub nor uploaded over it.
  let transcriptCaughtUp = env.AGENTHUB_RESUME !== '1' || !callback;
  // Set while the agent is being stopped on purpose so that its exit starts it again instead
  // of ending the session: `{ text, subtype }`, the line the clients see and the agenthub event
  // the chat UI gets for it.
  let restartReason = null;
  // Fleet messages that arrived while the agent was restarting; handed to the new agent once it
  // is up, since the old PTY or pipe would have swallowed them (docs/account-limits.md).
  let deferredMessages = [];
  // The last size a client asked for, re-applied to a restarted PTY: the clients do not know
  // the terminal was replaced and would not send a resize until their own window changes.
  let lastSize = null;
  let persisting = false;
  let persistWaiters = null;
  // Starts at the interval so the first tick archives: a session that dies in its first two
  // minutes would otherwise leave nothing to resume from.
  let stateTicksSinceUpload = STATE_ARCHIVE_EVERY_TICKS - 1;
  let uploadedStateSignature = null;
  // Fleet messages waiting for the provider's in-process mod (docs/priority-messages.md), and when the
  // mod last showed itself. The token is minted by the entrypoint and shared with the CLI
  // process only, so nothing else on the pod's port can read the queue.
  const modToken = env.AGENTHUB_MOD_TOKEN || '';
  let modQueue = [];
  let lastModHeartbeat = 0;
  // The CLI's own usage-limit notice, read off its output (docs/account-limits.md). Armed once
  // per agent start; the hub hears the first hit and nothing more until the next start.
  const limitDetector = createLimitDetector({
    patterns: driver.limitPatterns,
    now,
    onHit: hit => {
      // A live mod reads the provider's rate-limit figures and reports them itself; a sentence
      // matched in the terminal would be a second, vaguer report of the same limit.
      if (modAlive()) return;
      void reportLimit({ source: 'output', resetsAt: hit.resetsAt, detail: hit.detail });
    }
  });
  // Mod reports already forwarded this start, keyed by window and reset time.
  const forwardedLimits = new Set();
  // Plain fleet messages as the agent's next turn, for a runtime whose hooks report its turns
  // (driver.turnHooks) and that has no mod to hand them to (docs/priority-messages.md, #173).
  const nextTurn = driver.turnHooks === true && mode === 'interactive'
    ? createNextTurnDelivery({
      now, fetchImpl, callback, token, takeMessages: driver.fleetNextTurn !== false,
      interruptKeys: driver.interruptKeys,
      wait: ms => new Promise(resolve => setTimeoutImpl(resolve, ms)),
      Terminal: supplied.HeadlessTerminal && (() => supplied.HeadlessTerminal),
      log: line => console.log('[agent] ' + line)
    })
    : null;
  // Keys a person sends while a message is being typed in: they go in after its Enter instead of
  // landing in the middle of it.
  let inputHeldForTyping = [];
  let clearTarget = null;
  const contextClear = nextTurn && driver.contextClearCommand && !chatMode
    ? createContextClear({
      scheduleAtSafePoint: action => nextTurn.scheduleAtSafePoint(action),
      clear: async () => {
        if (exited || !term || restartReason !== null || runtimeDraining) throw new Error('context_clear_unavailable');
        clearTarget = term;
        const previous = locateTranscript();
        await injectTerminalInput(clearTarget, driver.contextClearCommand, {
          wait: ms => new Promise(resolve => setTimeoutImpl(resolve, ms))
        });
        return previous;
      },
      waitForClear: async previous => {
        if (typeof driver.findTranscript === 'function' && !driver.contextClearScreen) {
          const deadline = now() + 15_000;
          while (locateTranscript() === previous && now() < deadline) {
            await new Promise(resolve => setTimeoutImpl(resolve, 100));
          }
          if (locateTranscript() === previous) throw new Error('context_clear_identity_unchanged');
        }
        await nextTurn.waitForInputReady(clearTarget, () => scrollback, lastSize || { cols: 120, rows: 32 }, driver.contextClearScreen);
        if (term !== clearTarget || exited || restartReason !== null) throw new Error('context_clear_agent_restarted');
      },
      deliver: text => injectTerminalInput(clearTarget, '\x1b[200~' + text + '\x1b[201~', {
        wait: ms => new Promise(resolve => setTimeoutImpl(resolve, ms))
      }),
      onError: () => {
        const notice = '\r\n[agent] Context clear failed; the follow-up was not sent. Check the terminal.\r\n';
        remember(notice);
        broadcast(notice);
      }
    }) : null;

  function remember(chunk) {
    scrollback += chunk;
    if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER);
  }

  function safeSend(socket, data) {
    if (socket && socket.readyState === socket.OPEN) {
      try { socket.send(data); } catch {}
    }
  }

  // Activity is opt-in so older terminal viewers keep receiving only PTY bytes. Control frames
  // are never remembered: replaying an old working state would leave a finished turn spinning.
  const activityClients = new Set();
  let activityTimer = null;
  // What a waiting turn waits for: 'approval' (a permission dialog) or 'answer' (a question), or
  // null while it works.
  let activityWaitingFor = null;
  let activityIdleOverride = false;
  let activityPromptAt = now();
  let activityLastOutput = 0;
  let activityLastFrame = '';
  function activityFrame() {
    let state = 'idle';
    const source = nextTurn ? 'hooks' : 'output';
    if (!activityIdleOverride && nextTurn?.state === 'busy' && !activityWaitingFor && driver.failedTurnEnded) {
      try { activityIdleOverride = driver.failedTurnEnded({ file: transcriptFile, after: activityPromptAt, fs }); } catch {}
    }
    let reason;
    if (!exited && restartReason === null && !runtimeDraining && !activityIdleOverride) {
      if (nextTurn) state = nextTurn.state === 'busy'
        ? (activityWaitingFor ? 'waiting' : 'working') : nextTurn.state;
      else state = activityLastOutput > 0 && now() - activityLastOutput < IDLE_QUIET_MS ? 'working' : 'idle';
    }
    if (state === 'waiting') reason = activityWaitingFor;
    return JSON.stringify({ type: 'agenthub', subtype: 'activity', state, source, ...(reason ? { reason } : {}) });
  }
  function removeActivityClient(socket) {
    activityClients.delete(socket);
    if (!activityClients.size && activityTimer !== null) {
      clearIntervalImpl(activityTimer);
      activityTimer = null;
    }
  }
  function publishActivity() {
    if (!activityClients.size) return;
    const frame = activityFrame();
    if (frame === activityLastFrame) return;
    activityLastFrame = frame;
    for (const socket of activityClients) safeSend(socket, frame);
  }

  function broadcast(data) {
    for (const socket of clients) safeSend(socket, data);
  }

  // The periodic run archives only when the interval has passed and the state differs from
  // what the last successful upload held; an idle session used to compress and upload the same
  // tens of megabytes every 30s around the clock. Only a successful upload is remembered, so a
  // failed one is retried on the next tick instead of waiting out the interval. The final run
  // (agent exit, SIGTERM) archives without asking: it is the last chance, and it must not
  // depend on the change detection being right.
  function persistState(final, done) {
    if (!statePut) return done && done(new Error('state storage unavailable'));
    const archive = signature => execFile('/bin/sh',
      ['-c', archiveScript(driver), 'sh', '/tmp/state.tgz'], { cwd: home }, archiveError => {
        if (archiveError) return done && done(archiveError);
        execFile('curl', uploadArgs('/tmp/state.tgz', statePut, insecureStorage), error => {
          if (!error) {
            uploadedStateSignature = signature;
            stateTicksSinceUpload = 0;
          }
          if (done) done(error);
        });
      });
    if (final) return archive(null);
    if (++stateTicksSinceUpload < STATE_ARCHIVE_EVERY_TICKS) return done && done();
    // The signature is taken before the archive, so a file written while tar runs makes the
    // next signature differ and is picked up then rather than lost.
    execFile('/bin/sh', ['-c', signatureScript(driver)], { cwd: home }, (error, stdout) => {
      const match = error ? null : /^[0-9a-f]{64}\b/.exec(String(stdout || ''));
      const signature = match ? match[0] : null;
      if (signature && signature === uploadedStateSignature) return done && done();
      archive(signature);
    });
  }

  function persistScrollback(done) {
    if (!scrollPut) return done && done();
    try { fs.writeFileSync('/tmp/scrollback.log', scrollback); } catch {}
    execFile('curl', uploadArgs('/tmp/scrollback.log', scrollPut, insecureStorage), () => done && done());
  }

  // Rediscover on reads and persistence: /clear and /new replace a conversation without
  // replacing the PTY, so retaining the first path would freeze both Workspace and resume.
  function locateTranscript() {
    if (!transcriptCaughtUp) return null;
    return findTranscriptFile();
  }

  // Before the first launch the restored file is asked for as "touched from now on", so a
  // driver that falls back to the newest file since launch (one without a recorded thread id)
  // finds nothing rather than whichever thread happens to be newest in the archive.
  function findTranscriptFile() {
    if (typeof driver.findTranscript !== 'function') return transcriptFile;
    let found = null;
    try {
      found = driver.findTranscript({ env, home, cwd, fs, launchedAt: firstLaunchedAt || now(), pid: term && term.pid });
    } catch {}
    // The path is interpolated into a shell command below; anything a driver could not have
    // derived from a home directory, a slug and a uuid is not a transcript.
    if (typeof found === 'string' && found && !/["'$`\\\s]/.test(found)) {
      if (found === transcriptFile) return transcriptFile;
      const previous = transcriptFile || env.AGENTHUB_PREVIOUS_TRANSCRIPT;
      const switched = previous && previous !== found;
      if (switched) {
        let history = '';
        try {
          const raw = fs.readFileSync(previous, 'utf8').toString();
          history = raw.slice(0, raw.lastIndexOf('\n') + 1);
        } catch {}
        const boundary = JSON.stringify({ type: 'agenthub_conversation', id: require('node:path').basename(found), text: 'Conversation cleared' }) + '\n';
        try { fs.appendFileSync(conversationHistoryFile, history + boundary); } catch (error) {
          console.log('[agent] Conversation history could not be preserved: ' + error.message);
        }
      }
      conversationCleared = conversationCleared || switched || env.AGENTHUB_CONVERSATION_CLEARED === '1';
      transcriptFile = found;
      transcriptUploaded = { size: -1, mtimeMs: -1 };
      stateTicksSinceUpload = STATE_ARCHIVE_EVERY_TICKS;
      try { fs.writeFileSync(conversationStateFile, JSON.stringify({ file: found, cleared: conversationCleared })); } catch {}
      if (conversationCleared) {
        const boundary = '\r\n[agent] Conversation cleared.\r\n';
        remember(boundary);
        broadcast(boundary);
      }
      console.log('[agent] Transcript: ' + found);
    }
    return transcriptFile;
  }

  // The last `max` characters, cut at a line boundary so the first line the hub keeps is a
  // whole JSON record rather than the tail of one.
  function tailLines(text, max) {
    if (text.length <= max) return text;
    const cut = text.slice(-max);
    const newline = cut.indexOf('\n');
    return newline === -1 ? cut : cut.slice(newline + 1);
  }

  // Uploads readable history plus the current native transcript: the whole view to S3, the capped
  // tail to the hub's Postgres copy. Skipped while the file has not changed — most 30-second
  // ticks of an idle session would otherwise re-upload megabytes for nothing.
  function persistTranscript(done) {
    const file = locateTranscript();
    if (!file || (!transcriptPut && !callback)) return done && done();
    let stat;
    try { stat = fs.statSync(file); } catch { return done && done(); }
    if (stat.size === transcriptUploaded.size && stat.mtimeMs === transcriptUploaded.mtimeMs) {
      return done && done();
    }
    transcriptUploaded = { size: stat.size, mtimeMs: stat.mtimeMs };
    const boundary = conversationHistory();
    const toHub = () => {
      if (file !== transcriptFile) return done && done();
      if (!callback) return done && done();
      let text;
      try { text = secretMask.mask(boundary + fs.readFileSync(file, 'utf8')); } catch { return done && done(); }
      fetchImpl(callback + '/transcript', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/x-ndjson', 'X-Agent-Token': token },
        body: tailLines(text, MAX_BUFFER)
      }).catch(() => {}).finally(() => done && done());
    };
    if (!transcriptPut) return toHub();
    if (secretMask.active() || conversationCleared) return uploadMaskedTranscript(file, toHub, boundary);
    // No shell here: the file name is whatever the CLI chose for its session file, and a quote
    // or `$(` in it would otherwise become part of the command.
    execFile('curl', uploadArgs(file, transcriptPut, insecureStorage), toHub);
  }

  // The presigned urls in the environment expire twelve hours after the pod was created, and
  // every upload after that failed with a 403 nobody saw: the state a resume unpacks and the
  // transcript the Workspace reads both froze there (#111). Only a url the hub actually
  // returned replaces one, so a failed refresh keeps the current set for the next attempt; and
  // only an http(s) url, since curl would read anything starting with a dash as an option.
  function refreshArtifactUrls() {
    if (!callback || !token || !(statePut || scrollPut || transcriptPut)) return;
    fetchImpl(callback + '/artifact-urls', { method: 'POST', headers: { 'X-Agent-Token': token } })
      .then(response => {
        if (!response || !response.ok) throw new Error('HTTP ' + (response && response.status));
        return response.json();
      })
      .then(urls => {
        const usable = value => typeof value === 'string' && /^https?:\/\/\S+$/.test(value);
        let replaced = 0;
        if (statePut && usable(urls.statePutUrl)) { statePut = urls.statePutUrl; replaced++; }
        if (scrollPut && usable(urls.scrollbackPutUrl)) { scrollPut = urls.scrollbackPutUrl; replaced++; }
        if (transcriptPut && usable(urls.transcriptPutUrl)) { transcriptPut = urls.transcriptPutUrl; replaced++; }
        console.log('[agent] Upload urls refreshed: ' + replaced);
      })
      .catch(error => console.log('[agent] Upload urls not refreshed (' + error.message + ')'));
  }

  // With secrets to mask, the object storage copy is the masked text, handed to curl on stdin.
  // The CLI's own file stays as it is: the agent resumes from it, and a value replaced there
  // would be gone from its memory of the conversation. Not through a copy in /tmp, which is a
  // shared, predictable path that would hold the text a second time.
  function uploadMaskedTranscript(file, done, boundary = '') {
    let finished = false;
    const finish = () => { if (!finished) { finished = true; done(); } };
    let masked;
    try { masked = boundary + secretMask.mask(fs.readFileSync(file, 'utf8')); } catch { return finish(); }
    try {
      const child = spawnProcess('curl', uploadArgs('-', transcriptPut, insecureStorage),
        { stdio: ['pipe', 'ignore', 'ignore'] });
      child.on('exit', finish);
      child.on('error', finish);
      child.stdin.on('error', () => {});
      child.stdin.write(masked);
      child.stdin.end();
    } catch {
      finish();
    }
  }

  function restoreScrollback(done) {
    if (env.AGENTHUB_RESUME !== '1' || !callback || scrollback) return done();
    let started = false;
    let stopped = false;
    // Starting the agent and fetching the history are deliberately decoupled. The agent
    // must come up even if the hub never answers, so a timer releases it; a reply that
    // arrives later still seeds the buffer, as long as nothing has been written to it —
    // prepending history behind live output would interleave the two.
    const start = reason => {
      if (started) return;
      started = true;
      console.log('[agent] History: ' + reason);
      done();
    };
    setTimeoutImpl(() => start('not restored in time, starting without it'), 30000);

    // Retried on purpose: this runs while the pod is still settling, and a single failed
    // lookup used to lose the whole conversation silently.
    const attempt = n => {
      if (stopped) return;
      fetchImpl(callback + '/scrollback', { headers: { 'X-Agent-Token': token } })
        .then(response => {
          if (!response || !response.ok) throw new Error('HTTP ' + (response && response.status));
          return response.text();
        })
        .then(text => {
          stopped = true;
          if (typeof text === 'string' && text && !scrollback) {
            remember(text);
            start('restored ' + text.length + ' characters');
          } else {
            start(text ? 'arrived too late, session already writing' : 'nothing stored yet');
          }
        })
        .catch(error => {
          if (stopped) return;
          if (n >= 4) {
            stopped = true;
            return start('unavailable after ' + n + ' attempts (' + error.message + ')');
          }
          console.log('[agent] History: attempt ' + n + ' failed (' + error.message + '), retrying');
          setTimeoutImpl(() => attempt(n + 1), n * 1000);
        });
    };
    attempt(1);
  }

  // The state archive a resume unpacks is written at most every two minutes and is lost with a
  // pod that dies without its SIGTERM, while the transcript reaches the hub every 30 seconds. So
  // the provider's file a resumed agent continues can be older than the hub's copy (#146). Left
  // alone, the agent resumed without its last turns, the Workspace read the older file live and
  // dropped them from view, and the next upload overwrote the hub's copy with it. The CLIs only
  // append to the file, so a hub copy that starts with what was restored is the same file further
  // on: what it has beyond that is appended before the agent starts. A copy that does not start
  // with it is a different history, and the restored file is kept as it is.
  function catchUpTranscript(done) {
    if (transcriptCaughtUp) return done();
    let settled = false;
    const settle = reason => {
      if (settled) return;
      settled = true;
      transcriptCaughtUp = true;
      console.log('[agent] Transcript history: ' + reason);
      done();
    };
    setTimeoutImpl(() => settle('hub did not answer in time, resuming from the archive'), 15000);
    const file = findTranscriptFile();
    if (!file) return settle('no transcript restored');
    fetchImpl(callback + '/transcript', { headers: { 'X-Agent-Token': token } })
      .then(response => {
        if (!response || !response.ok) throw new Error('HTTP ' + (response && response.status));
        return response.text();
      })
      .then(stored => {
        if (settled) return;
        const raw = fs.readFileSync(file);
        const restored = Buffer.isBuffer(raw) ? raw : Buffer.from(String(raw));
        // The hub was sent the masked file, so it is compared with the restored one masked the
        // same way; the turns appended from it reach the agent as the hub has them.
        secretMask.refresh();
        const local = secretMask.mask(restored.toString('utf8'));
        if (typeof stored !== 'string' || stored.length <= local.length) return settle('archive is current');
        // The owner's readable history includes previous contexts. Only the last segment
        // belongs in the native file the CLI resumes; restoring history would undo /clear.
        const boundaryAt = stored.lastIndexOf('{"type":"agenthub_conversation"');
        if (boundaryAt >= 0) stored = stored.slice(stored.indexOf('\n', boundaryAt) + 1);
        if (!stored.startsWith(local)) return settle('hub copy is a different history, kept the archive');
        const extended = Buffer.concat([restored, Buffer.from(stored.slice(local.length), 'utf8')]);
        fs.writeFileSync(file + '.agenthub-tmp', extended);
        fs.renameSync(file + '.agenthub-tmp', file);
        settle('appended ' + (extended.length - restored.length) + ' bytes the archive was missing');
      })
      .catch(error => settle('unavailable (' + error.message + '), resuming from the archive'));
  }

  function backupScrollback(done) {
    if (!callback) return done && done();
    fetchImpl(callback + '/scrollback', {
      method: 'PUT',
      headers: { 'Content-Type': 'text/plain', 'X-Agent-Token': token },
      body: scrollback
    }).catch(() => {}).finally(() => done && done());
  }

  // Reports skills the agent created in its local skill directory back to the
  // hub (unchanged ones are skipped server-side). Best-effort with a hard timeout
  // so a slow hub can never eat the grace period needed for the state upload.
  function syncSkillsUp(done) {
    if (!callback || !token) return done && done();
    const script = require('node:path').join(__dirname, 'skills-sync-up.js');
    let finished = false;
    const finish = () => { if (!finished) { finished = true; done && done(); } };
    try {
      const child = spawnProcess(process.execPath, [script], { env, stdio: 'ignore' });
      const timer = setTimeoutImpl(() => { try { child.kill(); } catch {} finish(); }, 8000);
      child.on('exit', () => { clearTimeout(timer); finish(); });
      child.on('error', () => { clearTimeout(timer); finish(); });
    } catch {
      finish();
    }
  }

  // One run at a time. The 30s tick used to start a new run whether or not the previous one
  // had finished, so once archiving a large state directory took longer than the tick, tar
  // and gzip processes piled up by the thousand, all writing the same archive, and starved
  // the pod until hooks timed out. A tick that finds a run in flight is skipped; a caller
  // that needs the final state (exit, SIGTERM) gets one more run after the current one.
  function persistAll(done, final = false) {
    if (persisting) {
      if (final && done) (persistWaiters || (persistWaiters = [])).push(done);
      return;
    }
    persisting = true;
    postResources();
    // The buffer itself is masked, once per tick and as a whole: every upload below then carries
    // the masked text, a reconnecting client is replayed it, and a value the PTY delivered in two
    // chunks is caught as well, which masking chunk by chunk would miss.
    if (secretMask.refresh()) scrollback = secretMask.mask(scrollback);
    postUsage(() => syncSkillsUp(() => backupScrollback(() => persistScrollback(() => persistTranscript(() =>
      persistState(final, error => {
        persisting = false;
        const waiters = persistWaiters;
        persistWaiters = null;
        if (waiters) persistAll(nextError => { for (const waiter of waiters) waiter(nextError); }, true);
        if (done) done(error);
      }))))));
  }

  // The credential watcher runs beside this server, not under it: on pod stop only PID 1 — this
  // server — gets SIGTERM, and once it exits the kernel kills the watcher before its own SIGTERM
  // handler can upload a token the CLI rotated since the last poll. A provider that spends the
  // old refresh token on rotation then leaves a dead credential in the secret. So the watcher is
  // signalled here and given a bounded moment to finish, alongside the state persistence.
  function flushCredentialWatcher(done) {
    let pid;
    try {
      pid = Number.parseInt(String(fs.readFileSync(
        env.AGENTHUB_AUTH_WATCHER_PIDFILE || DEFAULT_WATCHER_PIDFILE, 'utf8')).trim(), 10);
    } catch {
      return done();
    }
    if (!Number.isSafeInteger(pid) || pid <= 1) return done();
    try { processLike.kill(pid, 'SIGTERM'); } catch { return done(); }
    const deadline = now() + WATCHER_FLUSH_WAIT_MS;
    (function wait() {
      let alive = true;
      try { processLike.kill(pid, 0); } catch { alive = false; }
      if (!alive || now() >= deadline) return done();
      setTimeoutImpl(wait, WATCHER_POLL_MS);
    })();
  }

  let exitCallbacks = null;
  function beforeExit(done) {
    if (exitCallbacks) return void exitCallbacks.push(done);
    exitCallbacks = [done];
    let pending = 2;
    const finish = () => {
      if (--pending > 0) return;
      for (const callback of exitCallbacks) callback();
    };
    flushCredentialWatcher(finish);
    persistAll(finish, true);
  }

  // ---- Pod resource snapshot (CPU/memory from the cgroup, network from /proc/net/dev) ----

  function readNumberFile(file) {
    try {
      const value = parseInt(fs.readFileSync(file, 'utf8').trim(), 10);
      return Number.isFinite(value) ? value : null;
    } catch { return null; }
  }

  function readCpuSeconds() {
    // cgroup v2: cpu.stat has "usage_usec <n>"; v1 fallback: cpuacct.usage in nanoseconds.
    try {
      const stat = fs.readFileSync('/sys/fs/cgroup/cpu.stat', 'utf8');
      const match = /^usage_usec\s+(\d+)/m.exec(stat);
      if (match) return parseInt(match[1], 10) / 1e6;
    } catch {}
    const v1 = readNumberFile('/sys/fs/cgroup/cpuacct/cpuacct.usage');
    return v1 === null ? null : v1 / 1e9;
  }

  function readMemoryBytes() {
    const v2 = readNumberFile('/sys/fs/cgroup/memory.current');
    if (v2 !== null) return v2;
    return readNumberFile('/sys/fs/cgroup/memory/memory.usage_in_bytes');
  }

  function readNetworkBytes() {
    // /proc/net/dev: "iface: rx_bytes ... (8 cols) tx_bytes ..."; loopback is not traffic.
    try {
      let rx = 0, tx = 0, seen = false;
      for (const line of fs.readFileSync('/proc/net/dev', 'utf8').split('\n')) {
        const match = /^\s*([^:\s]+):\s*(\d+)(?:\s+\d+){7}\s+(\d+)/.exec(line);
        if (!match || match[1] === 'lo') continue;
        rx += parseInt(match[2], 10); tx += parseInt(match[3], 10);
        seen = true;
      }
      return seen ? { rx, tx } : null;
    } catch { return null; }
  }

  // Best-effort: reports cumulative pod counters; the hub adds up the deltas. Skipped
  // entirely when none of the sources are readable (e.g. non-Linux dev environments).
  function postResources(done) {
    if (!callback || !token) return done && done();
    const cpuSeconds = readCpuSeconds();
    const memoryBytes = readMemoryBytes();
    const network = readNetworkBytes();
    if (cpuSeconds === null && memoryBytes === null && network === null) return done && done();
    fetchImpl(callback + '/resources', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
      body: JSON.stringify({
        cpuSeconds: cpuSeconds || 0,
        memoryBytes: memoryBytes || 0,
        rxBytes: network ? network.rx : 0,
        txBytes: network ? network.tx : 0
      })
    }).catch(() => {}).finally(() => done && done());
  }

  // The CLI's own usage record, as cumulative totals (driver.readUsage, docs/usage-tracking.md).
  // Awaited as part of the persistence run rather than fired beside it like the resource
  // snapshot: an autonomous session often lives shorter than one tick, so the report on exit is
  // the only one, and it has to land before the process is gone. Only a changed total is sent,
  // and only one the hub accepted counts as sent, so a failed post is repeated next tick.
  let lastUsageReport = '';
  function postUsage(done) {
    if (!callback || !token || typeof driver.readUsage !== 'function') return done();
    let totals = null;
    try { totals = driver.readUsage({ env, home, fs }); } catch (error) {
      console.log('[agent] Usage not read: ' + (error && error.message ? error.message : error));
    }
    if (!totals) return done();
    const body = JSON.stringify(totals);
    if (body === lastUsageReport) return done();
    fetchImpl(callback + '/usage', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
      body
    }).then(response => { if (response && response.ok) lastUsageReport = body; })
      .catch(() => {}).finally(() => done());
  }

  function postStatus(status, done) {
    if (!callback) return done && done();
    fetchImpl(callback + '/status', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
      body: JSON.stringify({ status })
    }).catch(() => {}).finally(() => done && done());
  }

  function agenthubEvent(subtype, fields) {
    return JSON.stringify({ type: 'agenthub', subtype, ...fields }) + '\n';
  }

  /**
   * Tells the hub this session's account is at its usage limit. The hub decides what follows —
   * marking the account, moving the session to another one — and answers with what it did;
   * nothing here depends on that answer beyond a log line.
   */
  function reportLimit(report) {
    if (!callback || !token) return Promise.resolve();
    const body = {
      source: report.source,
      kind: report.kind || null,
      percentUsed: typeof report.percentUsed === 'number' ? report.percentUsed : null,
      resetsAt: report.resetsAt || null,
      detail: report.detail || null
    };
    console.log('[agent] Usage limit reported to the hub (' + report.source +
      (report.kind ? ', ' + report.kind : '') + (report.resetsAt ? ', resets ' + report.resetsAt : '') + ')');
    return fetchImpl(callback + '/account-exhausted', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
      body: JSON.stringify(body)
    }).then(response => {
      if (!response || !response.ok) {
        console.log('[agent] Usage limit report answered HTTP ' + (response && response.status));
        return;
      }
      return response.json().then(answer => {
        if (answer && answer.action) console.log('[agent] Hub action on the usage limit: ' + answer.action +
          (answer.switchedTo ? ' (' + answer.switchedTo + ')' : ''));
      }).catch(() => {});
    }).catch(error => {
      console.log('[agent] Usage limit report failed: ' + (error && error.message));
    });
  }

  function retryMessage() {
    if (chatMode) return agenthubEvent('info', { text: 'No saved conversation to resume — starting fresh.' });
    return '\r\n[agent] No saved conversation to resume — starting fresh.\r\n';
  }

  function endMessage(exitCode, signal) {
    if (chatMode) return agenthubEvent('exit', { code: exitCode, signal: signal || null });
    return '\r\n[agent] Session ended (code ' + exitCode +
      (signal ? ', signal ' + signal : '') + ').\r\n';
  }

  function restartMessage(reason) {
    const text = reason.text + ' — restarting the agent and resuming the conversation.';
    if (chatMode) return agenthubEvent(reason.subtype || 'info', { text });
    return '\r\n[agent] ' + text + '\r\n';
  }

  /**
   * Stops the agent so that handleAgentExit starts it again with the provider's resume command.
   * The conversation this pod ran is on its own disk, which is what the resume flags describe;
   * a session that started fresh in this pod therefore resumes exactly like one restored from
   * the archive would. `subtype` names the agenthub event the chat UI gets for the restart line.
   */
  function restartAgent(reason, subtype) {
    if (exited || !term) return false;
    // A provider that names its conversation itself does so only once it has written the file,
    // and that name is what its resume command takes; looking now means the restarted agent
    // resumes this conversation and not whichever one is newest on disk.
    locateTranscript();
    restartReason = { text: reason, subtype: subtype || 'info' };
    env.AGENTHUB_RESUME = '1';
    env.AGENTHUB_STATE_RESTORED = '1';
    const stopping = term;
    try { stopping.kill(); } catch {}
    setTimeoutImpl(() => {
      if (restartReason !== null && term === stopping) {
        try { stopping.kill('SIGKILL'); } catch {}
      }
    }, RESTART_KILL_GRACE_MS);
    return true;
  }

  function handleAgentExit(exitCode, signal) {
    if (restartReason !== null) {
      const message = restartMessage(restartReason);
      restartReason = null;
      // The restart gets its own one-time fallback to a fresh start, as a cross-pod resume has.
      retriedFresh = false;
      remember(message);
      broadcast(message);
      startAgent(true);
      flushDeferredMessages();
      return;
    }

    const elapsedMs = now() - launchedAt;
    if (attemptedResume && !retriedFresh &&
        driver.isMissingResume(attemptOutput, exitCode, elapsedMs)) {
      retriedFresh = true;
      remember(retryMessage());
      startAgent(false);
      return;
    }

    exited = true;
    const message = endMessage(exitCode, signal);
    remember(message);
    for (const socket of clients) {
      safeSend(socket, message);
      try { socket.close(1000); } catch {}
    }
    beforeExit(() => {
      postStatus(exitCode === 0 ? 'Succeeded' : 'Failed', () =>
        setTimeoutImpl(() => processLike.exit(exitCode || 0), mode === 'interactive' ? 1500 : 200));
    });
  }

  function chatEventType(line) {
    try {
      const event = JSON.parse(line);
      return event && typeof event.type === 'string' ? event.type : undefined;
    } catch {
      return undefined;
    }
  }

  function deliverChatLine(line) {
    if (chatEventType(line) === 'result' && outstandingRuntimeTurns > 0) outstandingRuntimeTurns -= 1;
    const payload = line + '\n';
    if (!TRANSIENT_CHAT_EVENTS.has(chatEventType(line))) remember(payload);
    broadcast(payload);
  }

  function startPiped(command) {
    const child = spawnProcess(command.cmd, command.args, {
      cwd, env: agentEnv, stdio: ['pipe', 'pipe', 'pipe']
    });
    // Writable streams can emit `error` in addition to invoking the write callback.
    // The callback assigns failure to its chat turn; this listener keeps the runtime alive.
    child.stdin.on('error', () => {});

    let pendingLine = '';
    child.stdout.on('data', chunk => {
      const text = chunk.toString();
      attemptOutput += text;
      pendingLine += text;
      let newline;
      while ((newline = pendingLine.indexOf('\n')) !== -1) {
        const line = pendingLine.slice(0, newline);
        pendingLine = pendingLine.slice(newline + 1);
        if (line.trim()) {
          deliverChatLine(line);
          limitDetector.feedChatLine(line);
        }
      }
    });

    child.stderr.on('data', chunk => {
      const text = chunk.toString();
      attemptOutput += text;
      limitDetector.feedStderr(text);
      const payload = agenthubEvent('stderr', { text });
      remember(payload);
      broadcast(payload);
    });

    child.on('exit', (code, signal) =>
      handleAgentExit(code == null ? 1 : code, signal || undefined));

    chat = {
      async sendUser(text, delivery = {}) {
        const agentText = delivery.agentText ?? text;
        const event = {
          type: 'user',
          message: { role: 'user', content: [{ type: 'text', text: agentText }] }
        };
        await new Promise((resolve, reject) => {
          try {
            child.stdin.write(JSON.stringify(event) + '\n', error => error ? reject(error) : resolve());
          } catch (error) {
            reject(error);
          }
        });
        // The CLI never echoes user input on stdout, so replays need our copy.
        const echoEvent = {
          type: 'user',
          message: { role: 'user', content: [{ type: 'text', text }] },
          agenthub_echo: true
        };
        if (delivery.attachments?.length) echoEvent.attachments = delivery.attachments;
        const echo = JSON.stringify(echoEvent) + '\n';
        remember(echo);
        broadcast(echo);
      },
      interrupt() {
        controlCounter += 1;
        const request = {
          type: 'control_request',
          request_id: 'agenthub-' + controlCounter,
          request: { subtype: 'interrupt' }
        };
        try { child.stdin.write(JSON.stringify(request) + '\n'); } catch {}
      },
      kill() { try { child.kill(); } catch {} }
    };
    term = { write() {}, resize() {}, kill: chat.kill };

    if (!attemptedResume && !promptSent && env.AGENTHUB_PROMPT) {
      promptSent = true;
      outstandingRuntimeTurns += 1;
      void chat.sendUser(env.AGENTHUB_PROMPT).catch(() => {});
    }
  }

  function startAgent(allowResume) {
    activityPromptAt = now();
    activityIdleOverride = false;
    const command = driver.buildCommand(env, allowResume);
    attemptedResume = driver.isResumeCommand(command);
    attemptOutput = '';
    limitDetector.reset();
    forwardedLimits.clear();
    launchedAt = now();
    if (!firstLaunchedAt) firstLaunchedAt = launchedAt;
    chatMode = command.pipe === true;
    console.log('[agent] driver=' + driver.name + ' mode=' + mode + ' resume=' + attemptedResume +
      (chatMode ? ' ui=chat' : '') +
      ' cwd=' + cwd + ' cmd=' + command.cmd + ' ' + command.args.join(' '));

    if (chatMode) {
      startPiped(command);
      return;
    }

    term = pty.spawn(command.cmd, command.args, {
      name: 'xterm-256color', cols: lastSize ? lastSize.cols : 120, rows: lastSize ? lastSize.rows : 32,
      cwd, env: agentEnv
    });

    if (nextTurn) nextTurn.agentStarted();
    term.onData(data => {
      activityLastOutput = now();
      if (nextTurn) nextTurn.agentOutput();
      attemptOutput += data;
      remember(data);
      broadcast(data);
      limitDetector.feedTerminal(data);
    });

    term.onExit(({ exitCode, signal }) => handleAgentExit(exitCode, signal));
  }

  function attachmentDelivery() {
    const capabilities = driver.attachmentCapabilities;
    if (capabilities.localImagePaths) return 'localImagePaths';
    if (capabilities.mcpImages) return 'mcpImages';
    // The current stream-json transport has no native image argument. A future
    // provider protocol may select nativeImages before reaching this fallback.
    throw new Error('attachment_visual_delivery_unavailable');
  }

  function attachmentPrompt(text, attachments) {
    const lines = attachments.map(file => {
      const source = file.visualDelivery === 'localImagePaths'
        ? 'local path "' + file.localPath + '"'
        : 'file id "' + file.id + '" through agenthub_files.read_file';
      return '- ' + file.name + ' (' + file.mimeType + ', ' + file.size + ' bytes): ' + source;
    });
    const instruction = [
      '[AgentHub attachments]',
      ...lines,
      '',
      'Inspect each attached image visually before answering. Use the local visual Read path or agenthub_files.read_file as indicated. Do not infer image contents from the filename.',
      text ? '' : null,
      text || null
    ].filter(value => value !== null);
    return instruction.join('\n');
  }

  async function deliverAttachmentTurn(message) {
    const records = await attachmentMaterializer.materialize(message.attachments);
    const delivery = attachmentDelivery();
    const attachments = records.map(file => Object.freeze({
      ...file,
      visualDelivery: delivery
    }));
    const safeAttachments = attachments.map(file => ({
      id: file.id,
      name: file.name,
      mimeType: file.mimeType,
      size: file.size,
      visualDelivery: file.visualDelivery
    }));
    await chat.sendUser(message.text, {
      agentText: attachmentPrompt(message.text, attachments),
      attachments: safeAttachments
    });
  }

  function deliveryEvent(subtype, message) {
    if (!message.clientTurnId) return;
    return agenthubEvent(subtype, message.clientTurnId
      ? { clientTurnId: message.clientTurnId }
      : {});
  }
  function queueChatMessage(socket, message) {
    const attachments = message.attachments === undefined ? [] : message.attachments;
    if (!Array.isArray(attachments)) {
      safeSend(socket, agenthubEvent('error', { code: 'attachment_delivery_failed', clientTurnId: message.clientTurnId }));
      return;
    }
    if (runtimeDraining) {
      safeSend(socket, agenthubEvent('error', { code: 'runtime_updating', clientTurnId: message.clientTurnId }));
      return;
    }
    outstandingRuntimeTurns += 1;
    pendingChatDeliveries += 1;
    chatDelivery = chatDelivery
      .then(() => attachments.length ? deliverAttachmentTurn({ ...message, attachments }) : chat.sendUser(message.text))
      .then(() => { if (message.clientTurnId) safeSend(socket, deliveryEvent('chat_delivered', message)); })
      .catch(() => safeSend(socket, agenthubEvent('error', { code: 'attachment_delivery_failed', clientTurnId: message.clientTurnId })))
      .finally(() => { pendingChatDeliveries -= 1; });
  }
  function handleAgent(socket) {
    clients.add(socket);
    // Replay is a history read too; waiting for persistence leaves a 30-second disclosure window.
    secretMask.refresh();
    if (scrollback) safeSend(socket, secretMask.mask(scrollback));
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (message.type === 'subscribe' && message.channel === 'activity') {
        activityClients.add(socket);
        if (activityTimer === null) {
          activityTimer = setIntervalImpl(publishActivity, 500);
          activityTimer?.unref?.();
        }
        safeSend(socket, activityFrame());
        return;
      }
      if (exited) return;
      if (runtimeDraining) {
        safeSend(socket, agenthubEvent('error', { code: 'runtime_updating', clientTurnId: message.clientTurnId }));
        return;
      }
      if (chatMode) {
        if (message.type === 'chat' && typeof message.text === 'string' && chat) {
          if (message.clientTurnId !== undefined &&
              (typeof message.clientTurnId !== 'string' || !/^[A-Za-z0-9-]{1,64}$/.test(message.clientTurnId))) return;
          const hasAttachments = message.attachments !== undefined;
          if (message.text.trim() || hasAttachments) {
            queueChatMessage(socket, message);
          }
        } else if (message.type === 'interrupt' && chat) {
          chat.interrupt();
        }
        return;
      }
      if (message.type === 'input' && typeof message.data === 'string') {
        // The driver's interruption key cancels the whole turn inside a permission or question
        // dialog too, as it does while the turn works, and no hook follows (verified with the
        // pinned CLI, #252). Kept waiting, the Workspace asked for an approval at an idle prompt
        // until the next one.
        if (nextTurn?.state === 'busy' && driver.isTurnInterrupt?.(message.data)) {
          activityIdleOverride = true;
          activityWaitingFor = null;
          publishActivity();
        }
        // An approval selected in the terminal resumes the same turn, so there is no new prompt
        // hook. The client still gives a visible menu precedence until its screen clears.
        if (activityWaitingFor && /[\r\n]|^[yYnN]$/.test(message.data)) {
          activityWaitingFor = null;
          publishActivity();
        }
        if (nextTurn) {
          nextTurn.personInput(message.data);
          if (nextTurn.isTyping()) {
            inputHeldForTyping.push(message.data);
            return;
          }
        }
        term.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0) {
        lastSize = { cols: message.cols, rows: message.rows };
        try { term.resize(message.cols, message.rows); } catch {}
      }
    });
    socket.on('close', () => { clients.delete(socket); removeActivityClient(socket); });
    socket.on('error', () => { clients.delete(socket); activityClients.delete(socket); });
  }

  async function readBoundedBody(request, maxBytes) {
    const chunks = [];
    let total = 0;
    for await (const chunk of request) {
      total += chunk.length;
      if (total > maxBytes) return null;
      chunks.push(chunk);
    }
    return Buffer.concat(chunks);
  }

  /**
   * PUT /agenthub/credentials: the hub hands over another of the owner's logins for this
   * provider. The request names no path — the file goes where the driver says and nowhere else
   * (docs/provider-accounts.md, "Switching the account"). The watcher baseline is written first
   * so the poll that follows the install does not upload the file straight back.
   *
   * PUT /agenthub/credentials/refresh (`live`): the same login with tokens the hub renewed
   * (docs/provider-accounts.md, "The hub owns each login"). Installed without a restart, and only
   * for a driver whose CLI is known to re-read its file; an older pod answers 404 here, which the
   * hub takes as "not this time" rather than falling back to a restart.
   */
  async function handleCredentialRequest(request, response, live = false) {
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }
    if (request.method !== 'PUT') {
      response.setHeader('Allow', 'PUT');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    const provider = String(request.headers['x-agent-provider'] || '').toLowerCase();
    if (provider !== driver.name.toLowerCase()) {
      fileError(response, 409, 'provider_mismatch');
      return;
    }
    if (live && driver.reloadsCredentialLive !== true) {
      fileError(response, 409, 'live_reload_unsupported');
      return;
    }
    // A refresh still lands after the agent exited: whatever starts it next reads the file.
    if (exited && !live) {
      fileError(response, 409, 'agent_exited');
      return;
    }
    const declaredLength = Number.parseInt(request.headers['content-length'] || '', 10);
    if (Number.isFinite(declaredLength) && declaredLength > credentials.MAX_CREDENTIAL_BYTES) {
      fileError(response, 413, 'credential_too_large');
      request.resume();
      return;
    }
    const body = await readBoundedBody(request, credentials.MAX_CREDENTIAL_BYTES);
    if (runtimeDraining) return fileError(response, 409, 'runtime_updating');
    if (!body) {
      fileError(response, 413, 'credential_too_large');
      return;
    }
    const valid = typeof driver.validCredential === 'function'
      ? driver.validCredential(body)
      : credentials.looksLikeJsonObject(body);
    if (!valid) {
      fileError(response, 400, 'invalid_credential');
      return;
    }

    const target = credentials.credentialTarget(env, driver);
    credentials.writeBaselineHash(credentials.baselineFile(env), credentials.sha256(body), fs);
    if (typeof driver.installCredential === 'function') driver.installCredential(env, body, target, fs);
    else credentials.writeCredentialFile(target, body, fs);
    if (live) {
      // The same login with new tokens: the CLI notices the file changed and uses it on its
      // next request, so a restart would only cost the user a running turn.
      console.log('[agent] Provider credential refreshed by the hub.');
      sendJson(response, 200, { installed: true, restarting: false });
      return;
    }
    // A reason from the hub (the automatic failover says why) replaces the generic line and
    // names the event, so a person reading the scrollback or the chat sees the switch happen.
    const reason = switchReason(request.headers['x-agent-switch-reason']);
    const restarting = reason
      ? restartAgent(reason, 'account-switched')
      : restartAgent('Provider account switched');
    console.log('[agent] Provider credential replaced' + (restarting ? '; restarting with resume.' : '.'));
    sendJson(response, 202, { installed: true, restarting });
  }

  /**
   * PUT /agenthub/git-credentials: the owner chose another stored git token for this session
   * (docs/git-credentials.md, "Switching the token of a running session"). Body
   * `{credentials, authors}` — the whole store and the per-host authors, rebuilt by the hub the
   * same way as at spawn. Nothing restarts: git reads the store and its config on every command,
   * and gh/glab read theirs on every call, so the next push already uses the new token.
   */
  async function handleGitCredentialRequest(request, response) {
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }
    if (request.method !== 'PUT') {
      response.setHeader('Allow', 'PUT');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    // Room for the store plus the authors list around it.
    const body = await readBoundedBody(request, gitIdentity.MAX_CREDENTIALS_BYTES * 2);
    if (!body) {
      fileError(response, 413, 'git_credentials_too_large');
      return;
    }
    let payload;
    try { payload = gitIdentity.parsePayload(body); } catch (error) {
      fileError(response, 400, error.message);
      return;
    }
    try {
      installGitIdentity({ home: env.HOME || '/home/agent', ...payload });
    } catch (error) {
      console.error('[agent] Installing git credentials failed:', error.message);
      fileError(response, 500, 'git_credentials_install_failed');
      return;
    }
    console.log('[agent] Git credentials replaced by the hub.');
    sendJson(response, 200, { installed: true });
  }

  // Percent-encoded by the hub so a label with any character survives the header; control
  // characters are dropped so nothing in it can drive the terminal.
  function switchReason(header) {
    if (typeof header !== 'string' || !header) return null;
    let text;
    try { text = decodeURIComponent(header); } catch { text = header; }
    text = stripAnsi(text).replace(/[\x00-\x1f\x7f]/g, ' ').replace(/\s+/g, ' ').trim();
    if (!text) return null;
    return text.length > MAX_SWITCH_REASON_CHARS ? text.slice(0, MAX_SWITCH_REASON_CHARS) : text;
  }

  // Messages held back during a restart go to the new agent: the mod queue keeps itself, the
  // chat pipe buffers, and a TUI gets them typed once it has had time to draw its prompt.
  function flushDeferredMessages() {
    const held = deferredMessages;
    deferredMessages = [];
    for (const message of held) {
      if (modAlive() && (message.priority || mode === 'interactive')) {
        modQueue.push(message);
      } else if (chatMode) {
        if (chat) queueChatMessage(null, { text: formatFleetMessage(message) });
      } else if (mode === 'interactive' && term) {
        const target = term;
        setTimeoutImpl(() => {
          if (term !== target || exited) return;
          if (nextTurn) {
            void nextTurn.typeMessage(target, formatFleetMessage(message), () => scrollback,
              lastSize || { cols: 120, rows: 32 }, { id: message.id })
              .catch(error => console.log('[agent] Deferred message not typed: ' + (error && error.message || error)))
              .finally(() => releaseHeldKeys(target));
            return;
          }
          void injectTerminalInput(target, formatFleetMessage(message), {
            interrupt: false,
            wait: ms => new Promise(resolve => setTimeoutImpl(resolve, ms))
          });
        }, RESTART_INPUT_DELAY_MS);
      }
    }
  }

  /**
   * POST /agenthub/messages: the hub pushes a fleet message that should not wait for the agent's
   * next inbox poll. Where it goes depends on how the agent runs (docs/priority-messages.md):
   * the provider's in-process mod when one is alive, the chat pipe, the terminal as typed input — or
   * nowhere, in which case the hub keeps it in the inbox and tells the sender so. Delivery here
   * is the only place that knows those three channels; the hub only reads the answer.
   */
  // The provider's transcript from a byte offset, for the hub to follow a running session live
  // instead of waiting for the next 30-second upload (#111, docs/transcripts.md). Answers only
  // whole lines: the CLI may be mid-write, and a half line would be parsed as nothing and then
  // skipped for good, since the hub's next read starts after it. X-Transcript-Size lets the hub
  // see a file that got shorter than its cursor and start over.
  function handleTranscriptRequest(request, response) {
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }
    if (request.method !== 'GET') {
      response.setHeader('Allow', 'GET');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    const query = new URLSearchParams((request.url || '').split('?')[1] || '');
    const from = /^\d{1,15}$/.test(query.get('from') || '0') ? Number(query.get('from') || '0') : -1;
    if (from < 0) {
      fileError(response, 400, 'invalid_offset');
      return;
    }
    const file = locateTranscript();
    const segments = [conversationHistoryFile, file].filter(Boolean).map(file => {
      try { return { file, size: fs.statSync(file).size }; } catch { return { file, size: 0 }; }
    });
    const size = segments.reduce((total, segment) => total + segment.size, 0);
    const send = (next, body) => {
      response.statusCode = 200;
      response.setHeader('Content-Type', 'application/x-ndjson');
      response.setHeader('X-Transcript-Id', segments[0]?.size ? 'agenthub-history' : file ? require('node:path').basename(file) : '');
      response.setHeader('X-Transcript-Size', String(size));
      response.setHeader('X-Transcript-Next', String(next));
      response.setHeader('Content-Length', body.length);
      response.end(body);
    };
    if (from >= size) return send(Math.min(from, size), Buffer.alloc(0));
    const length = Math.min(size - from, MAX_TRANSCRIPT_CHUNK);
    const chunk = Buffer.alloc(length);
    let read = 0;
    let start = 0;
    for (const segment of segments) {
      let fd;
      try {
        if (from + read >= start + segment.size || read >= length) continue;
        fd = fs.openSync(segment.file, 'r');
        let position = Math.max(0, from + read - start);
        while (read < length && position < segment.size) {
          const count = fs.readSync(fd, chunk, read, Math.min(length - read, segment.size - position), position);
          if (count <= 0) break;
          read += count;
          position += count;
        }
      } catch {
        return send(from, Buffer.alloc(0));
      } finally {
        if (fd !== undefined) try { fs.closeSync(fd); } catch {}
        start += segment.size;
      }
    }
    let whole = read ? chunk.lastIndexOf(0x0a, read - 1) + 1 : 0;
    // Mask complete records before they leave the pod, including reads before the first upload
    // and newly projected secrets. Cursors must still count source bytes, not replacement bytes.
    secretMask.refresh();
    let body = Buffer.from(secretMask.mask(chunk.subarray(0, whole).toString('utf8')));
    // A short value can expand to a longer marker. Keep the wire body within the backend's
    // 8 MiB bound without splitting a record or advancing past a record we did not return.
    while (body.length > MAX_TRANSCRIPT_CHUNK && whole > 0) {
      const shorter = chunk.lastIndexOf(0x0a, Math.floor(whole / 2)) + 1;
      const first = chunk.indexOf(0x0a) + 1;
      whole = shorter || (first < whole ? first : 0);
      body = Buffer.from(secretMask.mask(chunk.subarray(0, whole).toString('utf8')));
    }
    send(from + whole, body);
  }

  async function handleMessageRequest(request, response) {
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    const raw = await readBoundedBody(request, MAX_MESSAGE_BYTES);
    if (runtimeDraining) return fileError(response, 409, 'runtime_updating');
    const message = raw && parseFleetMessage(raw);
    if (!message) {
      fileError(response, raw ? 400 : 413, raw ? 'invalid_message' : 'message_too_large');
      return;
    }
    const unavailable = reason => sendJson(response, 200, { delivered: 'unavailable', reason });
    if (exited) return unavailable('agent_exited');

    // One channel per message. A mod that is alive takes priority messages always and plain
    // ones in interactive sessions, where it can show them and hand them over with the next
    // prompt; in an autonomous run nobody types a next prompt, so a plain message stays in the
    // inbox for agent_inbox. Writing to the terminal as well would deliver it twice.
    if (modAlive() && (message.priority || mode === 'interactive')) {
      modQueue.push(message);
      sendJson(response, 200, { delivered: 'queued-for-mod' });
      return;
    }
    if (!message.priority) {
      if (!nextTurn || driver.fleetNextTurn === false || chatMode) return unavailable('not_priority');
      // Not typed now: the agent may be mid-turn or in a menu. The hub leaves the row in the
      // inbox, and the turn's end (or an idle prompt) takes it from there.
      nextTurn.accept();
      sendJson(response, 200, { delivered: 'next-turn' });
      deliverNextTurnIfIdle();
      return;
    }
    // The agent is being swapped out (an account switch): the pipe or PTY it is about to leave
    // would swallow the text, so the message waits for the one that comes up. The answer names
    // the channel it will take, since that is what the hub records.
    const restarting = restartReason !== null;
    if (chatMode) {
      if (!chat) return unavailable('agent_not_started');
      if (restarting) {
        deferredMessages.push(message);
        sendJson(response, 200, { delivered: 'chat', deferred: true });
        return;
      }
      if (message.interrupt) chat.interrupt();
      queueChatMessage(null, { text: formatFleetMessage(message) });
      sendJson(response, 200, { delivered: 'chat' });
      return;
    }
    // A -p run reads its prompt from the command line; keystrokes into its PTY reach nothing.
    if (mode !== 'interactive') return unavailable('non_interactive');
    if (!term) return unavailable('agent_not_started');
    if (restarting) {
      deferredMessages.push(message);
      sendJson(response, 200, { delivered: 'pty', deferred: true });
      return;
    }
    if (nextTurn) {
      // Typed as plain keys, a message of 1 KB or more stayed in one TUI's prompt for good and
      // took another 15 s and more to arrive (#222, docs/priority-messages.md). Not taken, it
      // stays in the inbox for agent_inbox rather than counting as delivered.
      const target = term;
      let submitted = false;
      try {
        submitted = await nextTurn.typeMessage(target, formatFleetMessage(message), () => scrollback,
          lastSize || { cols: 120, rows: 32 }, { interrupt: message.interrupt, id: message.id });
      } finally {
        releaseHeldKeys(target);
      }
      if (!submitted) return unavailable('not_submitted');
      sendJson(response, 200, { delivered: 'pty' });
      return;
    }
    await injectTerminalInput(term, formatFleetMessage(message), {
      interrupt: message.interrupt,
      interruptKeys: driver.interruptKeys,
      wait: ms => new Promise(resolve => setTimeoutImpl(resolve, ms))
    });
    sendJson(response, 200, { delivered: 'pty' });
  }

  /** Writes the person's keys held while a message was typed, unless another one is typing now. */
  function releaseHeldKeys(target) {
    if (nextTurn.isTyping()) return;
    const keys = inputHeldForTyping;
    inputHeldForTyping = [];
    if (term === target && !exited) for (const data of keys) target.write(data);
  }

  function deliverNextTurnIfIdle() {
    if (!nextTurn || chatMode || exited || restartReason !== null || runtimeDraining) return;
    const target = term;
    nextTurn.deliverIfIdle(target, () => scrollback, lastSize || { cols: 120, rows: 32 })
      .catch(error => console.log('[agent] next-turn: delivery failed: ' + (error && error.message || error)))
      // A check that found another one typing must leave the keys to that one's end.
      .finally(() => releaseHeldKeys(target));
  }

  /**
   * POST /agenthub/turn: the agent's own turn hooks (common/fleet-turn-hook.mjs) say a prompt was
   * submitted, an approval is being asked, or the turn ended. The answer to a Stop is the text of
   * the next turn when fleet messages wait. Loopback only, with the callback token the hooks
   * already carry for their calls to the hub.
   */
  async function handleTurnRequest(request, response) {
    const address = request.socket && request.socket.remoteAddress;
    if (!nextTurn || !isLoopback(address) || !tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, nextTurn ? 401 : 404, nextTurn ? 'unauthorized' : 'not_found');
      return;
    }
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    const raw = await readBoundedBody(request, MAX_LIMIT_REPORT_BYTES);
    let event = null;
    try { event = raw && JSON.parse(raw.toString('utf8')).event; } catch {}
    if (!['prompt', 'permission', 'question', 'stop', 'idle'].includes(event)) return fileError(response, 400, 'invalid_event');
    // A Stop during an update or restart is answered with nothing: the agent is about to go.
    // An interrupted/failed plugin turn is idle to viewers but is not a safe point for fleet
    // continuation. Keep that distinction instead of turning its idle report into a Stop.
    if (event === 'prompt') {
      activityPromptAt = now();
      // Complete before the prompt hook returns so a later question cannot be cleared by a
      // delayed callback from the previous prompt. Failure must not prevent the native turn.
      if (callback && token) {
        try {
          await fetchImpl(callback + '/turn-started', { method: 'POST',
            headers: { 'X-Agent-Token': token }, signal: AbortSignal.timeout(5000) });
        } catch {}
      }
    }
    activityIdleOverride = event === 'idle';
    activityWaitingFor = event === 'permission' ? 'approval' : event === 'question' ? 'answer' : null;
    const text = event === 'idle' || runtimeDraining || restartReason !== null ? null : await nextTurn.turnEvent(event);
    publishActivity();
    sendJson(response, 200, text ? { text } : {});
    if (event === 'stop' && !text) setTimeoutImpl(deliverNextTurnIfIdle, IDLE_QUIET_MS + 100);
  }

  async function handleContextClearRequest(request, response) {
    if (!tokenMatches(request.headers['x-agent-token'])) return fileError(response, 401, 'unauthorized');
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST');
      return fileError(response, 405, 'method_not_allowed');
    }
    if (!contextClear || chatMode || exited || !term || runtimeDraining || restartReason !== null)
      return fileError(response, 400, 'context_clear_unsupported');
    const raw = await readBoundedBody(request, 128 * 1024);
    if (!raw) return fileError(response, 413, 'context_clear_too_large');
    let input;
    try { input = JSON.parse(raw.toString('utf8')); } catch { return fileError(response, 400, 'invalid_follow_up'); }
    if (!input || Object.keys(input).some(key => key !== 'followUp')) return fileError(response, 400, 'invalid_follow_up');
    try { sendJson(response, 202, contextClear.schedule(input.followUp)); }
    catch (error) { fileError(response, error.message === 'context_clear_pending' ? 409 : 400, error.message); }
  }

  function parseFleetMessage(raw) {
    let parsed;
    try { parsed = JSON.parse(raw.toString('utf8')); } catch { return null; }
    if (!parsed || typeof parsed !== 'object' || typeof parsed.body !== 'string' || !parsed.body.trim()) return null;
    const text = field => typeof parsed[field] === 'string' && parsed[field] ? parsed[field] : null;
    return {
      id: text('id') || '',
      from: text('from'),
      fromTitle: text('fromTitle'),
      body: parsed.body,
      priority: parsed.priority === true,
      interrupt: parsed.interrupt === true
    };
  }

  function modAlive() {
    return Boolean(modToken) && now() - lastModHeartbeat < MOD_HEARTBEAT_TTL_MS;
  }

  /**
   * The in-process mod's side: GET /agenthub/mod/inbox hands over and clears the queue, POST
   * /agenthub/mod/heartbeat only says "still here", POST /agenthub/mod/limit relays a rate-limit
   * reading to the hub (docs/account-limits.md). All are loopback-only and carry the mod
   * token, not the callback token: the hub's token never reaches the agent process, and the
   * mod's token never leaves the pod.
   */
  async function handleModRequest(request, response, requestPath) {
    if (!modToken) {
      fileError(response, 404, 'not_found');
      return;
    }
    const address = request.socket && request.socket.remoteAddress;
    const header = String(request.headers['authorization'] || '');
    const bearer = header.startsWith('Bearer ') ? header.slice('Bearer '.length) : '';
    if (!isLoopback(address) || !secretMatches(modToken, bearer)) {
      fileError(response, 401, 'unauthorized');
      return;
    }
    lastModHeartbeat = now();
    if (requestPath === '/agenthub/mod/heartbeat') {
      if (request.method !== 'POST') {
        response.setHeader('Allow', 'POST');
        fileError(response, 405, 'method_not_allowed');
        return;
      }
      response.statusCode = 204;
      response.end();
      return;
    }
    if (requestPath === '/agenthub/mod/limit') {
      if (request.method !== 'POST') {
        response.setHeader('Allow', 'POST');
        fileError(response, 405, 'method_not_allowed');
        return;
      }
      const raw = await readBoundedBody(request, MAX_LIMIT_REPORT_BYTES);
      const report = raw && parseLimitReport(raw);
      if (!report) {
        fileError(response, raw ? 400 : 413, raw ? 'invalid_report' : 'report_too_large');
        return;
      }
      // The mod reports once per window and reset; this is the belt to that brace, so a mod
      // that reloaded mid-session does not make the hub mark the same window twice.
      const key = report.kind + '|' + (report.resetsAt || '');
      if (!forwardedLimits.has(key)) {
        forwardedLimits.add(key);
        void reportLimit({ source: 'mod', ...report });
      }
      sendJson(response, 202, { accepted: true });
      return;
    }
    if (request.method !== 'GET') {
      response.setHeader('Allow', 'GET');
      fileError(response, 405, 'method_not_allowed');
      return;
    }
    if (runtimeDraining) return fileError(response, 409, 'runtime_updating');
    // Reserve the fresh conversation's first input for its own handover.
    if (contextClear && contextClear.executing) return sendJson(response, 200, { messages: [] });
    const messages = modQueue;
    if (chatMode) outstandingRuntimeTurns += messages.length;
    modQueue = [];
    sendJson(response, 200, { messages });
  }

  function parseLimitReport(raw) {
    let parsed;
    try { parsed = JSON.parse(raw.toString('utf8')); } catch { return null; }
    if (!parsed || typeof parsed !== 'object') return null;
    const kind = typeof parsed.kind === 'string' && parsed.kind.trim() ? parsed.kind.trim().slice(0, 64) : null;
    const percentUsed = typeof parsed.percentUsed === 'number' && Number.isFinite(parsed.percentUsed) ? parsed.percentUsed : null;
    if (!kind && percentUsed === null) return null;
    return { kind: kind || 'unknown', percentUsed, resetsAt: toIso(parsed.resetsAt) };
  }

  function isLoopback(address) {
    return address === '127.0.0.1' || address === '::1' || address === '::ffff:127.0.0.1';
  }

  function handleHttpRequest(request, response) {
    const requestPath = (request.url || '').split('?')[0];
    if (requestPath === '/agenthub/diff') return handleDiffRequest(request, response);
    if (requestPath === '/agenthub/commands') return handleCommandsRequest(request, response);
    if (requestPath === '/agenthub/attachments') return handleAttachmentRequest(request, response);
    if (requestPath === '/agenthub/runtime-drain') {
      if (!tokenMatches(request.headers['x-agent-token'])) return fileError(response, 401, 'unauthorized');
      if (request.method === 'DELETE') {
        runtimeDraining = false;
        return sendJson(response, 200, { draining: false });
      }
      if (request.method !== 'POST') return fileError(response, 405, 'method_not_allowed');
      // Only structured chat results prove turn completion; silence in a terminal proves nothing.
      if (!chatMode || !chat || exited || restartReason !== null || outstandingRuntimeTurns ||
          pendingChatDeliveries || modQueue.length || deferredMessages.length || runtimeDraining || persisting)
        return fileError(response, 409, 'runtime_busy_or_unsupported');
      runtimeDraining = true;
      // Confirm the current conversation is durable before the hub removes this process.
      return persistAll(error => {
        if (error) {
          runtimeDraining = false;
          return fileError(response, 503, 'runtime_state_upload_failed');
        }
        if (!runtimeDraining) return fileError(response, 409, 'runtime_drain_cancelled');
        sendJson(response, 200, { draining: true });
      }, true);
    }
    if (runtimeDraining && requestPath.startsWith('/agenthub/credentials'))
      return fileError(response, 409, 'runtime_updating');
    if (requestPath === '/agenthub/credentials') return handleCredentialRequest(request, response);
    if (requestPath === '/agenthub/credentials/refresh') return handleCredentialRequest(request, response, true);
    if (requestPath === '/agenthub/git-credentials') return handleGitCredentialRequest(request, response);
    if (requestPath === '/agenthub/messages') return handleMessageRequest(request, response);
    if (requestPath === '/agenthub/transcript') return handleTranscriptRequest(request, response);
    if (requestPath === '/agenthub/context-clear') return handleContextClearRequest(request, response);
    if (requestPath === '/agenthub/turn') return handleTurnRequest(request, response);
    if (requestPath === '/agenthub/mod/inbox' || requestPath === '/agenthub/mod/heartbeat' ||
        requestPath === '/agenthub/mod/limit') {
      return handleModRequest(request, response, requestPath);
    }
    return handleFileRequest(request, response);
  }

  async function handleDiffRequest(request, response) {
    response.setHeader('Cache-Control', 'no-store');
    if (!tokenMatches(request.headers['x-agent-token'])) return fileError(response, 401, 'unauthorized');
    if (request.method !== 'GET') {
      response.setHeader('Allow', 'GET');
      return fileError(response, 405, 'method_not_allowed');
    }
    const comparison = new URL(request.url, 'http://example.com').searchParams.get('comparison') || 'base';
    if (!['base', 'head'].includes(comparison)) return fileError(response, 400, 'invalid_comparison');
    try { sendJson(response, 200, await workspaceDiff(comparison)); }
    catch { fileError(response, 503, 'diff_unavailable'); }
  }

  // Read from disk on every request rather than cached: a skill the library sync or the agent
  // itself adds mid-session is one the CLI may already offer, and the scan is bounded.
  function handleCommandsRequest(request, response) {
    response.setHeader('Cache-Control', 'no-store');
    if (!tokenMatches(request.headers['x-agent-token'])) return fileError(response, 401, 'unauthorized');
    if (request.method !== 'GET') {
      response.setHeader('Allow', 'GET');
      return fileError(response, 405, 'method_not_allowed');
    }
    if (!driver.slashCommands) return sendJson(response, 200, { agent: driver.name, commands: [], truncated: false });
    try {
      sendJson(response, 200, { agent: driver.name, ...listSlashCommands(driver.slashCommands, { env, home, cwd, fs }) });
    } catch {
      fileError(response, 503, 'commands_unavailable');
    }
  }

  /**
   * POST /agenthub/attachments: files the person attached in the Workspace composer or pasted into
   * the agent terminal of a terminal-transport session (#148). They are materialized exactly as a
   * chat-mode attachment is, and the answer is the path each one has to be pasted as: every pinned
   * CLI turns a pasted path to an image into an image of its own ("[Image #1]") and sends it to the
   * model as image content (docs/terminal-clipboard.md). The browser pastes the paths itself, ahead
   * of the message text, so the bytes keep the single order of the one terminal connection.
   */
  async function handleAttachmentRequest(request, response) {
    response.setHeader('Cache-Control', 'no-store');
    if (!tokenMatches(request.headers['x-agent-token'])) return fileError(response, 401, 'unauthorized');
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST');
      return fileError(response, 405, 'method_not_allowed');
    }
    // A chat-mode turn carries its attachments in the message, with a delivery receipt.
    if (chatMode) return fileError(response, 409, 'chat_transport');
    if (exited) return fileError(response, 409, 'agent_exited');
    if (runtimeDraining) return fileError(response, 409, 'runtime_updating');
    const raw = await readBoundedBody(request, MAX_ATTACHMENT_REQUEST_BYTES);
    if (!raw) return fileError(response, 413, 'request_too_large');
    let fileIds;
    try { fileIds = JSON.parse(raw.toString('utf8')).fileIds; } catch { return fileError(response, 400, 'invalid_request'); }
    let records;
    try {
      records = await attachmentMaterializer.materialize(fileIds);
    } catch (error) {
      return fileError(response, error && error.message === 'attachment_batch_invalid' ? 400 : 502, 'attachment_delivery_failed');
    }
    const attachments = [];
    try {
      for (const file of records) {
        const pastePath = typeof driver.terminalAttachmentPath === 'function'
          ? driver.terminalAttachmentPath(file, { env, home, fs })
          : file.localPath;
        if (!TERMINAL_PATH.test(pastePath)) return fileError(response, 422, 'attachment_path_unsafe');
        attachments.push({ id: file.id, name: file.name, mimeType: file.mimeType, size: file.size, path: pastePath });
      }
    } catch {
      return fileError(response, 502, 'attachment_delivery_failed');
    }
    sendJson(response, 200, { attachments });
  }

  function handleShell(socket) {
    let shell;
    try {
      shell = pty.spawn('bash', ['-l'], {
        name: 'xterm-256color', cols: 120, rows: 32, cwd, env
      });
    } catch (error) {
      safeSend(socket, '\r\n[agent] Failed to start shell: ' + (error && error.message) + '\r\n');
      try { socket.close(1011); } catch {}
      return;
    }

    shell.onData(data => safeSend(socket, data));
    shell.onExit(() => { try { socket.close(1000); } catch {} });
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (message.type === 'input' && typeof message.data === 'string') {
        shell.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0) {
        try { shell.resize(message.cols, message.rows); } catch {}
      }
    });
    const cleanup = () => { try { shell.kill(); } catch {} };
    socket.on('close', cleanup);
    socket.on('error', cleanup);
  }

  function tokenMatches(requestToken) {
    return secretMatches(token, requestToken);
  }

  function secretMatches(secret, requestToken) {
    if (!secret || typeof requestToken !== 'string') return false;
    const expected = Buffer.from(secret);
    const received = Buffer.from(requestToken);
    const length = Math.max(expected.length, received.length, 1);
    const left = Buffer.alloc(length);
    const right = Buffer.alloc(length);
    expected.copy(left);
    received.copy(right);
    return expected.length === received.length && crypto.timingSafeEqual(left, right);
  }

  function sendJson(response, status, value) {
    const body = JSON.stringify(value);
    response.statusCode = status;
    response.setHeader('Content-Type', 'application/json; charset=utf-8');
    response.setHeader('Content-Length', Buffer.byteLength(body));
    response.end(body);
  }

  function fileError(response, status, code) {
    sendJson(response, status, { error: code });
  }

  // One "bytes=" range, or null for the whole file — a malformed or multi-range header is
  // answered in full, which RFC 9110 permits and the backend handles either way.
  function parseByteRange(header) {
    const match = /^bytes=(\d*)-(\d*)$/.exec(typeof header === 'string' ? header.trim() : '');
    if (!match || (match[1] === '' && match[2] === '')) return null;
    if (match[1] === '') return { suffix: Number(match[2]) };
    const start = Number(match[1]);
    const end = match[2] === '' ? null : Number(match[2]);
    if (!Number.isSafeInteger(start) || (end !== null && (!Number.isSafeInteger(end) || end < start)))
      return null;
    return { start, end };
  }

  async function handleFileRequest(request, response) {
    const requestPath = (request.url || '').split('?')[0];
    const match = /^\/agenthub\/files\/([a-f0-9]{32})$/.exec(requestPath);
    if (!match) {
      fileError(response, 404, 'not_found');
      return;
    }
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }

    const id = match[1];
    try {
      if (request.method === 'PUT') {
        const name = request.headers['x-agent-file-name'];
        if (typeof name !== 'string' || !name) {
          fileError(response, 400, 'invalid_file_name');
          return;
        }
        const declaredLength = Number.parseInt(request.headers['content-length'] || '', 10);
        if (Number.isFinite(declaredLength) && declaredLength > fileMaxBytes) {
          fileError(response, 413, 'file_too_large');
          request.resume();
          return;
        }
        const stored = await fileStore.put(id, name, request, fileMaxBytes);
        sendJson(response, 201, { id: stored.id, name: stored.name, size: stored.size });
        return;
      }

      if (request.method === 'HEAD') {
        const metadata = await fileStore.head(id);
        if (!metadata) {
          response.statusCode = 404;
          response.end();
          return;
        }
        response.statusCode = 200;
        response.setHeader('Content-Length', metadata.size);
        response.setHeader('X-Agent-File-Name', metadata.name);
        response.end();
        return;
      }

      if (request.method === 'GET') {
        // The backend asks for one range when the Files pane seeks in a video on a pod-backed
        // file. Without this every seek streamed the file from its first byte to the backend,
        // which discarded everything before the offset.
        const range = parseByteRange(request.headers.range);
        const opened = await fileStore.open(id, range);
        if (!opened) {
          fileError(response, 404, 'file_not_found');
          return;
        }
        if (opened.unsatisfiable) {
          response.statusCode = 416;
          response.setHeader('Content-Range', `bytes */${opened.size}`);
          response.end();
          return;
        }
        response.setHeader('Accept-Ranges', 'bytes');
        response.setHeader('Content-Type', 'application/octet-stream');
        response.setHeader('X-Agent-File-Name', opened.name);
        if (opened.range) {
          response.statusCode = 206;
          response.setHeader('Content-Range',
            `bytes ${opened.range.start}-${opened.range.end}/${opened.size}`);
          response.setHeader('Content-Length', opened.range.end - opened.range.start + 1);
        } else {
          response.statusCode = 200;
          response.setHeader('Content-Length', opened.size);
        }
        opened.stream.on('error', () => response.destroy());
        opened.stream.pipe(response);
        return;
      }

      if (request.method === 'DELETE') {
        await fileStore.remove(id);
        response.statusCode = 204;
        response.end();
        return;
      }

      response.setHeader('Allow', 'PUT, GET, HEAD, DELETE');
      fileError(response, 405, 'method_not_allowed');
    } catch (error) {
      const code = error instanceof LocalFileError ? error.code : 'file_io_failed';
      const status = code === 'file_too_large' ? 413
        : code === 'invalid_file_name' || code === 'invalid_file_id' ? 400
          : code === 'managed_root_escape' ? 409 : 500;
      fileError(response, status, code);
    }
  }

  // A resumed session runs in a fresh pod, so the buffer every client is replayed on
  // connect would start empty and the conversation so far would look lost. Seed it from
  // the copy the hub kept before starting the agent, so the history is there for the
  // first client and stays part of what we persist from here on.
  restoreScrollback(() => catchUpTranscript(() => startAgent(true)));

  // An idle agent's waiting messages, once the screen has been still long enough to tell.
  if (nextTurn) setIntervalImpl(deliverNextTurnIfIdle, 1_000);

  let ticksSinceUrlRefresh = 0;
  setIntervalImpl(() => {
    if (!exited) persistAll();
    if (++ticksSinceUrlRefresh >= URL_REFRESH_EVERY_TICKS) {
      ticksSinceUrlRefresh = 0;
      refreshArtifactUrls();
    }
  }, 30_000);

  // Keeps the local skill directory in line with the library while the session runs, so a skill
  // another session saved is listed by the agent without a restart — the agent uses what its
  // skill directory lists and rarely thinks of searching. A poll rather than a push from the hub: the
  // pod already talks to the hub on its own schedule and nothing has to reach into it, and with
  // the ETag an unchanged library costs one small request per interval.
  // Discover even without a viewer: a coordinator can clear between persistence ticks.
  if (typeof driver.findTranscript === 'function') {
    setIntervalImpl(() => { if (!exited) locateTranscript(); }, 1000);
  }

  const skillsSyncMs = skillsSyncIntervalMs(env);
  if (skillsSyncMs > 0 && callback && token) {
    const skills = require('./skills');
    const pullSkillsImpl = supplied.pullSkills || skills.pullSkills;
    let pullingSkills = false;
    let lastSkillsError = '';
    let lastSkillsKept = '';
    let skillsEtag = null;
    setIntervalImpl(() => {
      if (exited || pullingSkills) return;
      pullingSkills = true;
      Promise.resolve()
        .then(() => pullSkillsImpl(env, { fetch: fetchImpl, etag: skillsEtag }))
        .then(result => {
          lastSkillsError = '';
          if (result && result.etag !== undefined) skillsEtag = result.etag;
          if (!result || result.notModified || result.skippedReason) return;
          // A skill kept for its local edits is reported on every pull that changes something;
          // repeating it each minute on its own would drown the log.
          const kept = (result.kept || []).join(',');
          if (result.written.length || result.removed.length || kept !== lastSkillsKept) {
            console.log('[agent] Skills: ' + skills.summary(result) + '.');
          }
          lastSkillsKept = kept;
        })
        .catch(error => {
          // Once per distinct failure: a hub that is down for an hour is one line, not sixty.
          if (error.message !== lastSkillsError) console.log('[agent] Skills: pull failed (' + error.message + ')');
          lastSkillsError = error.message;
        })
        .finally(() => { pullingSkills = false; });
    }, skillsSyncMs);
  }

  const httpServer = http.createServer((request, response) => {
    Promise.resolve(handleHttpRequest(request, response)).catch(() => {
      if (!response.headersSent) fileError(response, 500, 'file_io_failed');
      else response.destroy();
    });
  });
  const webSocketServer = new WebSocketServer({ server: httpServer, handleProtocols: () => 'tty' });
  webSocketServer.on('connection', (socket, request) => {
    const requestPath = (request && request.url ? request.url : '/').split('?')[0];
    if (requestPath === '/shell') handleShell(socket);
    else handleAgent(socket);
  });

  httpServer.listen(port);
  console.log('[agent] Agent server listening on :' + port +
    ' (paths: /, /shell, /agenthub/files/:id, /agenthub/credentials, /agenthub/git-credentials, /agenthub/messages, /agenthub/transcript, /agenthub/commands, /agenthub/attachments' +
    (modToken ? ', /agenthub/mod/*' : '') + ')');
  postStatus('Running');

  for (const signal of ['SIGTERM', 'SIGINT']) {
    processLike.on(signal, () => {
      beforeExit(() => {
        try { term.kill(); } catch {}
        processLike.exit(0);
      });
    });
  }

  return { env, httpServer, webSocketServer };
}

function startFromEnvironment(options = {}) {
  const env = options.env || process.env;
  const driver = loadDriver(env.AGENTHUB_DRIVER);
  return createCommonServer({ env, driver, dependencies: options.dependencies });
}

if (require.main === module) startFromEnvironment();

module.exports = { createCommonServer, startFromEnvironment, MAX_BUFFER };
