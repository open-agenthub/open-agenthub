# Sales-path audit rig (#480)

Walks the path a prospective customer takes, against the **published** artefacts (website
`install.sh`, Helm repository chart, `ghcr.io` images), inside a disposable Debian guest that a
non-root session pod can run: QEMU in TCG mode, no KVM, no docker, no root on the host.

| Script | What it does |
|---|---|
| `bootstrap-vm.sh` | Unpacks QEMU locally, downloads and SHA-512-verifies the Debian 12 cloud image, makes an overlay disk, an SSH key and a cloud-init seed. |
| `start-vm.sh` | Boots the guest (4 vCPU / 10 GiB). Forwards SSH to `127.0.0.1:2222` and guest `:8080` to `127.0.0.1:18080`. Keep it running. |
| `ssh.sh` | SSH into the guest as `demo` (password-less sudo, as a fresh cloud VM would have). |
| `run-quickstart.sh` | Runs `curl -fsSL https://open-agenthub.github.io/install.sh \| sh` in the guest unchanged and timestamps every output line to `~/qs.log`. |
| `guest-browser.sh` | In the guest: the installer's port-forward and a headless Chromium; the host reaches its DevTools port through `ssh.sh -N -L 19222:127.0.0.1:9222`. |
| `guest-forwards.sh` | Copied to the guest as `~/audit/forwards.sh`: restarts the hub and IdP port-forwards (run it with `setsid`). |
| `walk.mjs`, `checkout.mjs` | Playwright over CDP: API-key sign-in, Settings, the licence screen, and the trial checkout up to the payment page (never submitted). |
| `standins.yaml`, `mock-model.mjs` | Stand-ins in namespace `audit`: a git daemon that accepts pushes and a scripted Anthropic Messages endpoint, both on container port 443, which the chart's agent egress policy already allows. |
| `first-session.sh` | Creates the first task as an Autonomous Claude session on those stand-ins, through the same REST call the UI makes. |
| `keycloak.yaml` | IdP stand-in (realm `team`, users alice and bob, public PKCE client with an audience mapper) plus the CoreDNS override that gives browser and backend the same issuer URL. |
| `oidc.mjs`, `user.mjs` | Sign a Keycloak user in through the real browser flow; `user.mjs activate` pastes a licence token in Admin, `user.mjs takeover <title> <text>` opens a shared session and sends a message. |
| `share-session.sh` | alice starts an Interactive session and shares it with bob as Collaborator (`UI_MODE=terminal` as the UI creates it, or `chat`). |
| `testkey-backend-patch.json` | Mounts a backend build whose only change is the licence verification key over the image's `/app` (see `licence/README.md`); for sharing tests, never for a real install. |

Order: `bootstrap-vm.sh`, `start-vm.sh` (keep running), `run-quickstart.sh`, `guest-browser.sh`, the
stand-ins (`kubectl apply`, then `kubectl -n audit create configmap mock-model --from-file=mock-model.mjs`),
`first-session.sh`, then `keycloak.yaml`, the documented `helm upgrade` with the `oidc.*` values
(plus `oidc.requireHttpsMetadata=false`, the issuer being plain HTTP), the test-key backend, and
`share-session.sh` / `user.mjs`. The emulated CPU occasionally crashes a JIT (Keycloak's JVM once);
delete the pod and it starts.

State lives under `$HOME/rig/sales-audit-vm` (override with `TASK_ROOT`); the private SSH key and
disks never enter the repository. TCG is several times slower than a real VM, so durations measured
here are upper bounds; the audit quotes the GitHub-hosted install-smoke runs for real-hardware
timing.

The results are recorded in `docs/design/sales-path-audit.md`; generated logs are not committed.
