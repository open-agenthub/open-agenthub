// Starts the REAL common/server.js the way session-agent/server.js does, with one difference:
// the PTY is wrapped so a fault can be injected on the wire between the session agent and the
// real CLI, right after the context-clear command went in. Everything else is the shipped code.
//   PROOF_FAULT=drop     the first input after the clear is lost (a CLI that had not attached its
//                        input handler yet after redrawing)
//   PROOF_FAULT=busy     the terminal keeps producing output for PROOF_BUSY_MS after the clear
//                        (a redraw loop), so it never looks ready within the 15 s wait
//   PROOF_FAULT=restart  the pod dies 1 s after the clear went in, before the handover
'use strict';
const realPty = require('/opt/session-agent/node_modules/node-pty');
const { startFromEnvironment } = require('/opt/session-agent/common/server.js');
const fault = process.env.PROOF_FAULT || '';
const clearCommand = process.env.PROOF_CLEAR_COMMAND;
const log = line => console.log('[fault] ' + line + ' at ' + Date.now());

function spawn(cmd, args, options) {
  const child = realPty.spawn(cmd, args, options);
  const listeners = [];
  let cleared = false;
  let dropped = false;
  function afterClear() {
    log('clear command written');
    if (fault === 'busy') {
      const until = Date.now() + Number(process.env.PROOF_BUSY_MS || 20000);
      const timer = setInterval(() => {
        if (Date.now() > until) { clearInterval(timer); log('busy output stopped'); return; }
        // Save/restore cursor: output the screen never shows.
        for (const fn of listeners) fn('\x1b7\x1b8');
      }, 200);
    }
    if (fault === 'restart') {
      setTimeout(() => {
        log('killing the CLI and the session agent (pod lost)');
        try { process.kill(child.pid, 'SIGKILL'); } catch {}
        process.exit(0);
      }, 1000);
    }
  }
  return new Proxy(child, {
    get(target, key) {
      if (key === 'onData') return fn => { listeners.push(fn); return target.onData(fn); };
      if (key === 'write') return data => {
        if (!cleared && data === clearCommand) { cleared = true; afterClear(); }
        else if (cleared && fault === 'drop' && !dropped && typeof data === 'string' && data.startsWith('\x1b[200~')) {
          dropped = true;
          log('dropped the first input after the clear: ' + JSON.stringify(data.slice(0, 60)));
          return;
        }
        return target.write(data);
      };
      const value = target[key];
      return typeof value === 'function' ? value.bind(target) : value;
    }
  });
}

startFromEnvironment({ dependencies: { pty: { spawn } } });
