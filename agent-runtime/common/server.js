'use strict';
const crypto = require('node:crypto');

const { loadDriver, validateDriver } = require('./driver-contract');
const { LocalFileStore, LocalFileError } = require('../files/local-store');
const { AttachmentMaterializer } = require('../files/materialize');
const credentials = require('./credential-install');

// The scrollback window, in characters. The hub stores and pages exactly this much
// (ScrollbackLimits.MaxChars in backend/Services); the two have to agree, or a resume seeded
// from the hub's copy comes back shorter than what this process uploaded.
const MAX_BUFFER = 1_000_000;
// How long a stopped agent gets to exit on its own before the restart forces it; long enough
// for a CLI to flush its session file, short enough that a wedged one does not stall the swap.
const RESTART_KILL_GRACE_MS = 8_000;
// Upper bound for each archive/upload step of a persistence run.
const PERSIST_STEP_SECONDS = 120;
// Where an entrypoint records the pid of its credential watcher, and how long the server waits
// for that watcher's last upload: a little over the watcher's own 5s flush bound.
const DEFAULT_WATCHER_PIDFILE = '/tmp/agenthub-auth-watcher.pid';
const WATCHER_FLUSH_WAIT_MS = 6_000;
const WATCHER_POLL_MS = 100;
// Protocol chatter that the chat UI only needs live, never on replay.
const TRANSIENT_CHAT_EVENTS = new Set(['stream_event', 'control_response', 'control_request']);

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
  const setTimeoutImpl = supplied.setTimeout || setTimeout;
  const now = supplied.now || Date.now;
  const fileStore = supplied.fileStore || new LocalFileStore({
    root: env.AGENTHUB_FILE_ROOT || '/workspace/.agenthub/files' });
  const attachmentMaterializer = supplied.attachmentMaterializer ||
    new AttachmentMaterializer({ env, fetch: fetchImpl, store: fileStore });

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
  const statePut = env.AGENTHUB_STATE_PUT_URL || '';
  const scrollPut = env.AGENTHUB_SCROLLBACK_PUT_URL || '';
  const transcriptPut = env.AGENTHUB_TRANSCRIPT_PUT_URL || '';
  const home = env.HOME || '/home/agent';
  const workdir = env.AGENTHUB_WORKDIR || (hasRepo ? '/workspace/repo' : '/workspace');
  const cwd = fs.existsSync(workdir) ? workdir : '/workspace';
  const curlOption = env.AGENTHUB_S3_INSECURE === '1' ? '-k ' : '';
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
  let promptSent = false;
  let firstLaunchedAt = 0;
  let transcriptFile = null;
  let transcriptUploaded = { size: -1, mtimeMs: -1 };
  // Set while the agent is being stopped on purpose so that its exit starts it again instead
  // of ending the session.
  let restartReason = null;
  // The last size a client asked for, re-applied to a restarted PTY: the clients do not know
  // the terminal was replaced and would not send a resize until their own window changes.
  let lastSize = null;
  let persisting = false;
  let persistWaiters = null;

  function remember(chunk) {
    scrollback += chunk;
    if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER);
  }

  function safeSend(socket, data) {
    if (socket.readyState === socket.OPEN) {
      try { socket.send(data); } catch {}
    }
  }

  function broadcast(data) {
    for (const socket of clients) safeSend(socket, data);
  }

  function persistState(done) {
    if (!statePut) return done && done();
    const archive = driver.stateDir;
    const excludes = [driver.stateDir + '/' + driver.authFilename];
    if (Array.isArray(driver.stateExcludes)) {
      for (const entry of driver.stateExcludes) excludes.push(entry);
    }
    const excludeArgs = excludes.map(entry => '--exclude="' + entry + '"').join(' ');
    // tar exits 1 when a file changed while it was read — the live transcript of a running
    // agent always does — and the archive is still complete, so only >1 counts as a failure;
    // treating 1 as one meant an active session never uploaded its state at all. It writes
    // to a side file so an aborted run never replaces the last good archive, runs niced
    // and bounded so a large state directory cannot starve the agent and its hooks.
    execFile('/bin/sh', ['-c',
      'nice -n 10 timeout ' + PERSIST_STEP_SECONDS + ' tar czf /tmp/state.tgz.part -C "' + home +
      '" ' + excludeArgs + ' "' + archive + '" 2>/dev/null; ' +
      'if [ $? -le 1 ]; then mv -f /tmp/state.tgz.part /tmp/state.tgz && curl -fsS --max-time ' +
      PERSIST_STEP_SECONDS + ' ' + curlOption + '-T /tmp/state.tgz "' + statePut + '"; ' +
      'else rm -f /tmp/state.tgz.part; exit 1; fi'
    ], () => done && done());
  }

  function persistScrollback(done) {
    if (!scrollPut) return done && done();
    try { fs.writeFileSync('/tmp/scrollback.log', scrollback); } catch {}
    execFile('/bin/sh', ['-c',
      'curl -fsS --max-time ' + PERSIST_STEP_SECONDS + ' ' + curlOption +
      '-T /tmp/scrollback.log "' + scrollPut + '"'
    ], () => done && done());
  }

  // The provider's own conversation file, once the driver can name it. Asked again on every
  // persistence tick until found: providers create the file on the first turn or pick its
  // name themselves after starting, so it does not exist when this process comes up.
  function locateTranscript() {
    if (transcriptFile || typeof driver.findTranscript !== 'function') return transcriptFile;
    let found = null;
    try {
      found = driver.findTranscript({ env, home, cwd, fs, launchedAt: firstLaunchedAt });
    } catch {}
    // The path is interpolated into a shell command below; anything a driver could not have
    // derived from a home directory, a slug and a uuid is not a transcript.
    if (typeof found === 'string' && found && !/["'$`\\\s]/.test(found)) {
      transcriptFile = found;
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

  // Uploads the native transcript next to the scrollback: the whole file to S3, the capped
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
    const toHub = () => {
      if (!callback) return done && done();
      let text;
      try { text = fs.readFileSync(file, 'utf8'); } catch { return done && done(); }
      fetchImpl(callback + '/transcript', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/x-ndjson', 'X-Agent-Token': token },
        body: tailLines(text, MAX_BUFFER)
      }).catch(() => {}).finally(() => done && done());
    };
    if (!transcriptPut) return toHub();
    execFile('/bin/sh', ['-c',
      'curl -fsS ' + curlOption + '-T "' + file + '" "' + transcriptPut + '"'
    ], toHub);
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
  function persistAll(done) {
    if (persisting) {
      if (done) (persistWaiters || (persistWaiters = [])).push(done);
      return;
    }
    persisting = true;
    postResources();
    syncSkillsUp(() => backupScrollback(() => persistScrollback(() => persistTranscript(() =>
      persistState(() => {
        persisting = false;
        const waiters = persistWaiters;
        persistWaiters = null;
        if (waiters) persistAll(() => { for (const waiter of waiters) waiter(); });
        if (done) done();
      })))));
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
    persistAll(finish);
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
    if (chatMode) return agenthubEvent('info', { text: reason + ' — restarting the agent and resuming the conversation.' });
    return '\r\n[agent] ' + reason + ' — restarting the agent and resuming the conversation.\r\n';
  }

  /**
   * Stops the agent so that handleAgentExit starts it again with the provider's resume command.
   * The conversation this pod ran is on its own disk, which is what the resume flags describe;
   * a session that started fresh in this pod therefore resumes exactly like one restored from
   * the archive would.
   */
  function restartAgent(reason) {
    if (exited || !term) return false;
    // A provider that names its conversation itself does so only once it has written the file,
    // and that name is what its resume command takes; looking now means the restarted agent
    // resumes this conversation and not whichever one is newest on disk.
    locateTranscript();
    restartReason = reason;
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
        if (line.trim()) deliverChatLine(line);
      }
    });

    child.stderr.on('data', chunk => {
      const text = chunk.toString();
      attemptOutput += text;
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
      void chat.sendUser(env.AGENTHUB_PROMPT).catch(() => {});
    }
  }

  function startAgent(allowResume) {
    const command = driver.buildCommand(env, allowResume);
    attemptedResume = driver.isResumeCommand(command);
    attemptOutput = '';
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

    term.onData(data => {
      attemptOutput += data;
      remember(data);
      broadcast(data);
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
    pendingChatDeliveries += 1;
    chatDelivery = chatDelivery
      .then(() => attachments.length ? deliverAttachmentTurn({ ...message, attachments }) : chat.sendUser(message.text))
      .then(() => { if (message.clientTurnId) safeSend(socket, deliveryEvent('chat_delivered', message)); })
      .catch(() => safeSend(socket, agenthubEvent('error', { code: 'attachment_delivery_failed', clientTurnId: message.clientTurnId })))
      .finally(() => { pendingChatDeliveries -= 1; });
  }
  function handleAgent(socket) {
    clients.add(socket);
    if (scrollback) safeSend(socket, scrollback);
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (exited) return;
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
        term.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0) {
        lastSize = { cols: message.cols, rows: message.rows };
        try { term.resize(message.cols, message.rows); } catch {}
      }
    });
    socket.on('close', () => clients.delete(socket));
    socket.on('error', () => clients.delete(socket));
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
   */
  async function handleCredentialRequest(request, response) {
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
    if (exited) {
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
    const restarting = restartAgent('Provider account switched');
    console.log('[agent] Provider credential replaced' + (restarting ? '; restarting with resume.' : '.'));
    sendJson(response, 202, { installed: true, restarting });
  }

  function handleHttpRequest(request, response) {
    const requestPath = (request.url || '').split('?')[0];
    if (requestPath === '/agenthub/credentials') return handleCredentialRequest(request, response);
    return handleFileRequest(request, response);
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
    if (!token || typeof requestToken !== 'string') return false;
    const expected = Buffer.from(token);
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
        const opened = await fileStore.open(id);
        if (!opened) {
          fileError(response, 404, 'file_not_found');
          return;
        }
        response.statusCode = 200;
        response.setHeader('Content-Type', 'application/octet-stream');
        response.setHeader('Content-Length', opened.size);
        response.setHeader('X-Agent-File-Name', opened.name);
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
  restoreScrollback(() => startAgent(true));

  setIntervalImpl(() => {
    if (!exited) persistAll();
  }, 30_000);

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
  console.log('[agent] Agent server listening on :' + port + ' (paths: /, /shell, /agenthub/files/:id, /agenthub/credentials)');
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
