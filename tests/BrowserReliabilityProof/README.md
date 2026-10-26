# Browser reliability proofs (#236, #237, #240)

The browser scripts run against the browser runtime's real `entrypoint.sh` with the image's distribution Chromium,
Xvfb and x11vnc, rootless in a session pod (skill `rootless-image-chromium-xvfb-rig`). The rig
must also run what the packages' postinst steps do in the image (`update-mime-database`,
`glib-compile-schemas`): without them GTK's file dialog aborts Chromium, which the image never
does.

| Script | Issue | What it checks |
|---|---|---|
| `windows.mjs <playwright-core dir>` | #236 | Top-level window count after a `target=_blank` link, a plain `window.open`, a popup with window features, a Shift+click on a link, CDP `Target.createTarget` with and without `newWindow`, and that an OAuth-style popup still reaches its opener. |
| `mcp-click.mjs <session-agent dir> <browser dir>` | #237 | Drives the real `agent-runtime/browser/server.mjs` over stdio, as an agent CLI does, through clicks that navigate slowly, hang the renderer, close the page, detach the element, open popups, swap the renderer process, churn iframes, download, open the file chooser, raise dialogs, and kill the browser mid-click (`PROOF_SCENARIOS=kill`). After each it calls `browser_navigate` and records whether the call is answered and whether the transport is still open. |
| `PodGoneProof/` | #240 | The real `KubernetesBrowserService` and `KubernetesBrowserClusterClient` against a real kube-apiserver (skill `rootless-kube-apiserver-admission-proof`, kubeconfig in `~/.kube/config`). Without a kubelet, the program marks each browser pod Running and Ready itself, then evicts the pod (phase Failed, the object stays) and deletes it under a Running lease, and records `browser_status` and `browser_start` after each. `dotnet run -p:BackendDir=<worktree>/backend` builds it against main or the branch. |
| `fixtures.mjs` | — | Fixture pages and a stand-in for the backend's `/internal/sessions/{id}/browser` route. |

`windows.mjs` connects to `http://127.0.0.1:9223`, the CDP port the entrypoint gives Chromium.

`mcp-click.mjs` with `PROOF_SCENARIOS=freeze` or `kill` and `PROOF_REPLACEMENT_CDP` set to a second
running browser stands in for a replaced pod: the first browser is stopped (SIGSTOP leaves its
sockets open, as a vanished pod does) or killed mid-click, and the stand-in backend then hands
`browser_start` the second endpoint. A browser resumed after SIGSTOP stays degraded; start a
fresh one before the next run.
