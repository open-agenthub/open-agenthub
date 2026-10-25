'use strict';

// Read the live CLI screen, including soft-wrapped addresses. Prose containing a URL alone
// must never become an actionable sign-in card (docs/proofs/242-cli-sign-in.md).
function detectSignIn(lines) {
  if (signInFinished(lines)) return null;
  const rows = (lines || []).map(line => String(line ?? '').trim());
  const text = rows.join('\n');
  const outcome = (status, retry = false, confirm = false) => ({ kind: 'signin', mode: 'outcome', status, retry, confirm, options: [] });
  if (/^Login$/m.test(text) && /^OAuth error:/m.test(text)) return outcome('failed', /Press Enter to retry/i.test(text));
  if (/^Login$/m.test(text) && /^Login successful\. Press Enter to continue/m.test(text)) return outcome('succeeded', false, true);
  if (/Authentication tokens stored securely\./.test(text) && /Authenticating with Cursor/.test(text)) return outcome('succeeded');
  if (/Token provider/.test(text) && /Auth profile:/.test(text)) return outcome('saved');
  if (/Starting login process/.test(text) && /Login failed or timed out\.|Login error:/.test(text)) return outcome('failed');
  if (/^Successfully logged in$/m.test(text)) return outcome('succeeded');
  if (/^Error logging in with device code:/m.test(text)) return outcome('failed');
  if (/Add credential/.test(text) && /└\s+Done/.test(text)) return outcome('saved');
  if (/Token provider/.test(text) && /Setup cancelled\./.test(text)) return outcome('cancelled');
  let mode;
  if (/Paste code here if prompted\s*>/i.test(text) && /Browser didn't open\?/i.test(text)) mode = 'code';
  else if ((/Follow these steps to sign in.*device code authorization/i.test(text) && /Enter this one-time code/i.test(text)) || (/Waiting for authorization/i.test(text) && /Enter code:/.test(text))) mode = 'device';
  else if ((/Waiting for browser authentication/i.test(text) && /Open a browser and navigate to this link/i.test(text)) || (/Complete authorization in your browser/i.test(text) && /Waiting for authorization/i.test(text))) mode = 'browser';
  else if (/Paste .*setup-token|Enter your API key/i.test(text)) mode = 'credential';
  if (!mode) return null;
  let url = '';
  for (let i = 0; i < rows.length; i++) {
    const match = /https:\/\/[^\s]+/.exec(rows[i]);
    if (!match) continue;
    // An address can span several full terminal rows; its continuation has no spaces.
    let value = match[0];
    while (i + 1 < rows.length && /^[A-Za-z0-9%&=+_.~/?#:-]+$/.test(rows[i + 1])) value += rows[++i];
    try {
      const parsed = new URL(value);
      if (parsed.protocol === 'https:' && !parsed.username && !parsed.password) url = parsed.href;
    } catch {}
  }
  const deviceIndex = rows.findIndex(row => /Enter this one-time code/i.test(row));
  const codeRows = deviceIndex >= 0 ? rows.slice(deviceIndex + 1) : [];
  const isDeviceCode = row => /^(?:[A-Z0-9]{8,}|[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+)$/.test(row);
  const wrappedCode = codeRows.map((row, i) => row + (codeRows[i + 1] || '')).find(isDeviceCode);
  const code = mode === 'device' ? (deviceIndex >= 0 ? codeRows.find(row => /^[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+$/.test(row)) || wrappedCode : /Enter code:\s*([A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+|[A-Z0-9]{8,})/.exec(text)?.[1]) || '' : '';
  return { kind: 'signin', mode, status: 'waiting', url, code, invalid: mode === 'credential' && /Expected token starting|└\s+Required/.test(text), options: [] };
}

// Login wrappers can leave their output above the new TUI. A composer below every auth
// prompt proves that the CLI moved on; a composer above a newly opened login dialog does not.
function signInFinished(lines) {
  const rows = (lines || []).map(line => String(line ?? '').trim());
  const lastAuth = rows.findLastIndex(row => /Select login method|Starting login process|Follow these steps to sign in|Paste code here|Enter this one-time code|Enter code:|Waiting for (?:browser authentication|authorization)|Complete authorization|Enter your API key|Paste .*setup-token|Token provider|Token method|OAuth error:|Press Enter to retry|Login successful\. Press Enter|^Successfully logged in$/.test(row));
  const lastPrompt = rows.findLastIndex(row => /^❯\s*$/.test(row));
  if (lastPrompt > lastAuth) return true;
  const header = rows.findLastIndex(row => />_ .*\bCodex\b/.test(row));
  const composer = rows.findLastIndex(row => /^›(?:\s|$)/.test(row) && !/^›\s*\d+[.)]/.test(row));
  if (header >= 0 && composer > header && composer > lastAuth) return true;
  const input = rows.findLastIndex(row => /^┃\s+Ask anything(?:…)?(?:\s|$)/.test(row));
  const footer = rows.findLastIndex(row => /\btab\s+agents\s+ctrl\+p\s+commands\b/.test(row));
  return input > lastAuth && footer > input;
}

module.exports = { detectSignIn, signInFinished };
