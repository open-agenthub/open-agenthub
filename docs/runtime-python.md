# Python in session runtime images

Every default agent runtime installs `python3`, `python3-pip` and `python3-venv` in its final
stage through the base distribution's package repositories. Previously Python existed only
in the native-module builder, which does not become part of the running session image.

Use `python3 -m venv /workspace/.venv`, then `/workspace/.venv/bin/python -m pip install …`.
The system interpreter is distribution-managed; a virtual environment keeps dependencies
writable by uid 1000 with the pod's read-only root filesystem. Installing packages into the
system interpreter or using a separate downloaded interpreter was rejected: the former needs
root and changes shared system packages, while the latter duplicates the distribution's
security-update path. Package versions follow the base distribution rather than a separate
language installer. A custom image must provide its own Python packages.

## Proof and size

`agent-runtime/tests/python-smoke.sh` runs the actual interpreter, creates a venv, installs a
locally generated wheel without a package index, and imports it. The runtime CI job invokes it
in all five built images with uid 1000, a read-only root, writable `/tmp`, and no network.
Each Dockerfile also checks Python, pip and venv creation during the build.

Locally, the same smoke script passed against the actual distribution packages unpacked into
a rootless path overlay, as uid 1000: Python 3.11.2 and pip 23.0.1. The added dependency closure
on the current slim amd64 runtime base is 38,400 KiB installed (37.5 MiB), with 11,767,040 bytes
of package archives. This measures package payload, not the compressed image-layer delta;
bytecode generated during package configuration and layer compression change the final size.
CI prints the built image sizes. No container engine was available for a local full-image run;
CI provides the read-only filesystem check. Other architectures may have different sizes.
