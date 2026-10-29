#!/bin/bash
set -eu
RIG=${RIG:?Set RIG to the unpacked distribution package directory}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
export LD_LIBRARY_PATH="$RIG/root/usr/lib/x86_64-linux-gnu:$RIG/root/lib/x86_64-linux-gnu:$RIG/root/usr/lib/x86_64-linux-gnu/pulseaudio"
export FONTCONFIG_FILE="$RIG/fonts.conf"
export LIBGL_DRIVERS_PATH="$RIG/root/usr/lib/x86_64-linux-gnu/dri"
export XDG_DATA_DIRS="$RIG/root/usr/share"
export GSETTINGS_SCHEMA_DIR="$RIG/root/usr/share/glib-2.0/schemas"
export PYTHONPATH="$RIG/root/usr/lib/python3/dist-packages"
mkdir -p "$RIG/shim" "$RIG/fccache" /tmp/xkb
cat > "$RIG/fonts.conf" <<XML
<?xml version="1.0"?><!DOCTYPE fontconfig SYSTEM "fonts.dtd"><fontconfig><dir>$RIG/root/usr/share/fonts</dir><cachedir>$RIG/fccache</cachedir></fontconfig>
XML
cp "$RIG/root/usr/bin/xkbcomp" /tmp/xkb/xkbcomp
# Xvfb's compiled-in helper path needs a same-length replacement outside the root filesystem.
python3 - "$RIG" <<'PY'
import sys
w=sys.argv[1]
s=open(w+'/root/usr/bin/Xvfb','rb').read().replace(b'/usr/bin',b'/tmp/xkb')
open(w+'/shim/Xvfb-bin','wb').write(s)
PY
chmod +x "$RIG/shim/Xvfb-bin"
cat > "$RIG/shim/Xvfb" <<'SH'
#!/bin/sh
exec "$RIG/shim/Xvfb-bin" -xkbdir "$RIG/root/usr/share/X11/xkb" "$@"
SH
cat > "$RIG/shim/chromium" <<'SH'
#!/bin/sh
exec "$RIG/root/usr/lib/chromium/chromium" --disable-dev-shm-usage "$@"
SH
cat > "$RIG/shim/node" <<'SH'
#!/bin/sh
if [ "$1" = /opt/browser/supervisor.mjs ]; then shift; exec "$REAL_NODE" "$REPO/browser-runtime/supervisor.mjs" "$@"; fi
exec "$REAL_NODE" "$@"
SH
chmod +x "$RIG/shim/Xvfb" "$RIG/shim/chromium" "$RIG/shim/node"
"$RIG/root/usr/bin/update-mime-database" "$RIG/root/usr/share/mime"
"$RIG/root/usr/lib/x86_64-linux-gnu/glib-2.0/glib-compile-schemas" "$RIG/root/usr/share/glib-2.0/schemas"
export REAL_NODE=$(command -v node) RIG REPO
export PATH="$RIG/shim:$RIG/root/usr/bin:$PATH"
export AGENTHUB_BROWSER_DATA_DIR="$RIG/browser-data" AGENTHUB_BROWSER_SCREEN=2560x1600
exec sh "$REPO/browser-runtime/entrypoint.sh"
