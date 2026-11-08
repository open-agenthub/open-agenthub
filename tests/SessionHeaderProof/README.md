# Session header proof (#551)

Screenshots and layout measurements of the Workspace session header in real Chromium, at
1280, 1024 and 768 px, for an owner, a viewer (session shared with them) and a shared-link reader.

`fixture.cjs` is a stand-in API on 127.0.0.1:8080 with explicit test data: a Codex session on a
subscription account whose e-mail is long, a model list, a provider-switch status, open fleet
messages, a viewer session under `/api/sessions/shared` and a read-only link `/shared/view-token`.
It proves layout and roles only; no pod, provider or backend controller is involved.

```bash
cd agent-runtime/session-agent && npm ci --ignore-scripts && npx playwright-core install chromium-headless-shell
AGENTHUB_REPO=$PWD/../.. node ../../tests/SessionHeaderProof/fixture.cjs &
cd ../../frontend && npm ci && npx vite --host 0.0.0.0 --port 5173 &
CHROME_PATH=~/.cache/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell \
  SHOTS=/tmp/header-shots LABEL=after node ../tests/SessionHeaderProof/header-shots.cjs
```

Set `AGENTHUB_CDP_URL` instead of `CHROME_PATH` to drive an existing browser over CDP. In a bare
session pod the headless shell needs system libraries; fetch them without root as described in the
rootless Chromium skill. The script prints, per view and width, the bar height, the number of rows
its visible controls occupy, whether it overflows horizontally, and the visible control labels; it
writes PNGs (closed and with the "⋯" menu open) and `<label>-measurements.json` to `SHOTS`.
Never commit the generated files.
