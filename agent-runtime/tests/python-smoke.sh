#!/bin/sh
set -eu
# Run with a read-only root and a writable /tmp to match session pods, without network access.
work=$(mktemp -d /tmp/python-smoke.XXXXXX)
trap 'rm -rf "$work"' EXIT
python3 --version
python3 -m pip --version
python3 -m venv "$work/venv"
"$work/venv/bin/python" - "$work" <<'PY'
import pathlib
import subprocess
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
wheel = root / 'runtime_probe-1.0-py3-none-any.whl'
with zipfile.ZipFile(wheel, 'w') as archive:
    archive.writestr('runtime_probe.py', 'value = 42\n')
    archive.writestr('runtime_probe-1.0.dist-info/METADATA', 'Metadata-Version: 2.1\nName: runtime-probe\nVersion: 1.0\n')
    archive.writestr('runtime_probe-1.0.dist-info/WHEEL', 'Wheel-Version: 1.0\nGenerator: runtime-proof\nRoot-Is-Purelib: true\nTag: py3-none-any\n')
    archive.writestr('runtime_probe-1.0.dist-info/RECORD', '')
subprocess.run([sys.executable, '-m', 'pip', 'install', '--no-index', '--disable-pip-version-check', str(wheel)], check=True)
subprocess.run([sys.executable, '-c', 'import runtime_probe; assert runtime_probe.value == 42'], check=True)
assert sys.prefix != sys.base_prefix
print('venv, offline pip install and import passed')
PY
