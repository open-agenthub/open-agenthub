# Browser pop-out proof (#342)

This host references the actual backend project and uses BrowserProxy, BrowserViewportController,
BrowserClipboardController and BrowserRuntimeClient without substitutes. Session storage, access
lookup and pod provisioning are stand-ins. A fixed test key protects the proof host's routes;
it is not a production authentication or Kubernetes admission proof.

1. Unpack the distribution dependency closure for chromium, xvfb, x11vnc, x11-xserver-utils,
   websockify, socat and libicu72 into `$RIG/root`, following the rootless browser runtime skill.
2. `npm ci` in frontend and browser-runtime. Set `RIG` to the unpacked package directory and run
   `bash tests/BrowserPopoutProof/start-runtime.sh`. This runs the real runtime entrypoint and
   supervisor; only executable paths and Xvfb's compiled helper path change for rootless use.
3. `dotnet run --project tests/BrowserPopoutProof` starts the real backend browser paths on 8080.
   Start frontend Vite on 5173. Start a separate distribution Chromium with `--headless`,
   `--no-sandbox`, `--disable-dev-shm-usage`, a separate profile and `--remote-debugging-port=9224`.
   Give it the same LD_LIBRARY_PATH (including pulseaudio) and FONTCONFIG_FILE as the runtime.
4. `PLAYWRIGHT=<playwright-core directory> node tests/BrowserPopoutProof/proof.mjs`.
   The script temporarily copies harness.html into the frontend and removes it on exit.

The parent harness mounts the real BrowserPane; the child loads the normal frontend entrypoint.
The proof checks authenticated VNC, a credential-free pop-out URL, parent placeholder/disconnect,
an 1800px viewport, real VNC mouse and keyboard input, local clipboard paste and copy through the
backend and runtime, Bring back, and OS-style window close. It rejects browser page errors.
The local-network permission is needed by recent Chromium for loopback WebSockets. Explicitly
focus the pop-out before typing; a background window's keyboard does not control the remote page.

Results from distribution Chromium 156 and the branch backend/runtime are in results.txt.
Deployment, OIDC renewal and a physical second monitor are outside this proof.
