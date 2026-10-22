// Kept in parity with agent-runtime/common/cli-sign-in.js.

// Read the live CLI screen, including soft-wrapped addresses. Prose containing a URL alone
// must never become an actionable sign-in card (docs/proofs/242-cli-sign-in.md).
export function detectSignIn(lines) {
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
  const code = mode === 'device' ? (deviceIndex >= 0 ? rows.slice(deviceIndex + 1).find(row => /^[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+$/.test(row)) : /Enter code:\s*([A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+)/.exec(text)?.[1]) || '' : '';
  return { kind: 'signin', mode, status: 'waiting', url, code, invalid: mode === 'credential' && /Expected token starting|└\s+Required/.test(text), options: [] };
}

