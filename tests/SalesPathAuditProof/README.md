# Sales-path audit rig (#480)

Walks the path a prospective customer takes, against the **published** artefacts (website
`install.sh`, Helm repository chart, `ghcr.io` images), inside a disposable Debian guest that a
non-root session pod can run: QEMU in TCG mode, no KVM, no docker, no root on the host.

| Script | What it does |
|---|---|
| `bootstrap-vm.sh` | Unpacks QEMU locally, downloads and SHA-512-verifies the Debian 12 cloud image, makes an overlay disk, an SSH key and a cloud-init seed. |
| `start-vm.sh` | Boots the guest (8 vCPU / 10 GiB). Forwards SSH to `127.0.0.1:2222` and guest `:8080` to `127.0.0.1:18080`. Keep it running. |
| `ssh.sh` | SSH into the guest as `demo` (password-less sudo, as a fresh cloud VM would have). |
| `run-quickstart.sh` | Runs `curl -fsSL https://open-agenthub.github.io/install.sh \| sh` in the guest unchanged and timestamps every output line to `~/qs.log`. |

State lives under `$HOME/rig/sales-audit-vm` (override with `TASK_ROOT`); the private SSH key and
disks never enter the repository. TCG is several times slower than a real VM, so durations measured
here are upper bounds; the audit quotes the GitHub-hosted install-smoke runs for real-hardware
timing.

The results are recorded in `docs/design/sales-path-audit.md`; generated logs are not committed.
