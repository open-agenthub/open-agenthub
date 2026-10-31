# Browser layout proof (#374)

Run `npm ci` in frontend and start Vite on 0.0.0.0:5173. Connect an isolated Chromium
to that server, then run:

```sh
CDP=http://example.com:9222 UI=http://example.com:5173 SHOTS=/tmp/browser-layout-shots node tests/BrowserLayoutProof/proof.mjs
```

The script mounts the real SessionWorkspace, BrowserPane and normal pop-out entrypoint.
API listings are isolated fixtures; this proves frontend layout and window lifecycle,
not provisioning or VNC transport. It uses real pointer dragging onto the visible target,
measures conversation width, checks Files fallback and both return paths, rejects page
errors and checks the child URL. It also tears the tab off with a trusted native
dragend beyond the viewport and checks its coordinates. Screenshots go only to SHOTS, never into Git.
The temporary frontend harness is removed on exit. Run one proof process at a time.

The window owner stays mounted under a hidden companion column: unmounting it would close
the child immediately. A compact return row lives outside that column, so it also works
when Files closes. Keeping a full-height placeholder was rejected because it reserves
conversation space without displaying browser content. Native drag/drop preserves the
existing button and keyboard path, and a visible target gives an alternative when the
window manager does not deliver an outside-window drag end. Screen coordinates are
window features only; the browser may clamp them or block the pop-up. The existing hint
still handles blocked windows. Credential handoff and both message checks are unchanged.

Physical monitor placement needs a manual desktop check. The automated proof covers
both the native drag/drop target and a pointer release beyond the viewport in Chromium.
