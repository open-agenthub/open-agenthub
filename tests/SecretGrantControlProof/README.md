# One secret control proof (#329)

`browser.mjs` runs against the real backend (dev auth) with real Postgres and a real kube-apiserver
(the rig of `tests/SessionResourcesProof/README.md`), in real Chromium on the branch's Vite app.
`npm i playwright-core` next to it.

```sh
node browser.mjs <chrome-headless-shell> <outdir> <kubectl> <admin kubeconfig>            # branch
node browser.mjs <chrome-headless-shell> <outdir> <kubectl> <admin kubeconfig> baseline   # main: counts the controls
```

It creates sessions with every kind of stored state through the API. It checks that each opens
at the level the mapping in `docs/session-secrets.md` gives, with one control and no checkboxes,
and that saving untouched sends neither field. It then changes levels in the edit dialog, the
session view and the project dialog, and reads the result back from the API, after a reload, and
from the session's copies (`sessionsecrets-<id>`, `sessionenv-<id>`).

Result: 28/28 on the branch (including the note when a session stops following its project). On main the same edit dialog shows 2 selection checkboxes next to
the grant buttons.
