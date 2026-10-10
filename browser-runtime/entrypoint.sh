#!/bin/sh
set -eu

export DISPLAY=:99
SCREEN="${AGENTHUB_BROWSER_SCREEN:-2560x1600}"
X11_SOCKET_DIR="${AGENTHUB_BROWSER_X11_SOCKET_DIR:-/tmp/.X11-unix}"
# Overridable for the same reason as the socket directory: the entrypoint test runs this
# script outside its image, where /data is neither present nor creatable.
DATA_DIR="${AGENTHUB_BROWSER_DATA_DIR:-/data}"
X11_READY_ATTEMPTS="${AGENTHUB_BROWSER_X11_READY_ATTEMPTS:-100}"
PIDS=""
SUPERVISOR_PID=""
STOPPING=0

case "$X11_READY_ATTEMPTS" in
  ''|*[!0-9]*|0) X11_READY_ATTEMPTS=100 ;;
esac

start_child() {
  "$@" &
  CHILD_PID=$!
  PIDS="$PIDS $CHILD_PID"
}

stop_children() {
  [ "$STOPPING" -eq 1 ] && return
  STOPPING=1

  if [ -n "$SUPERVISOR_PID" ] && kill -0 "$SUPERVISOR_PID" 2>/dev/null; then
    kill -TERM "$SUPERVISOR_PID" 2>/dev/null || true
    attempts=0
    while kill -0 "$SUPERVISOR_PID" 2>/dev/null && [ "$attempts" -lt 47 ]; do
      sleep 1
      attempts=$((attempts + 1))
    done
  fi

  for pid in $PIDS; do
    [ "$pid" = "$SUPERVISOR_PID" ] || kill -TERM "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
}

trap 'stop_children; exit 0' TERM INT
trap 'stop_children' EXIT

mkdir -p "$DATA_DIR/chromium" "$DATA_DIR/home" /tmp/runtime "$X11_SOCKET_DIR"

start_child Xvfb :99 -screen 0 "${SCREEN}x24" -nolisten tcp
XVFB_PID=$CHILD_PID
attempt=0
while [ ! -e "$X11_SOCKET_DIR/X99" ]; do
  if ! kill -0 "$XVFB_PID" 2>/dev/null; then
    wait "$XVFB_PID" || status=$?
    echo "[browser-runtime] Xvfb exited before display :99 became ready: status=${status:-0}" >&2
    exit "${status:-1}"
  fi
  if [ "$attempt" -ge "$X11_READY_ATTEMPTS" ]; then
    echo "[browser-runtime] display :99 did not become ready" >&2
    exit 1
  fi
  attempt=$((attempt + 1))
  sleep 0.1
done

start_child chromium \
  --display=:99 \
  --remote-debugging-address=127.0.0.1 \
  --remote-debugging-port=9223 \
  --user-data-dir="$DATA_DIR/chromium" \
  --no-first-run \
  --no-default-browser-check \
  --no-sandbox \
  --disable-background-networking \
  --disable-component-update \
  --disable-sync \
  about:blank
# -nosel: x11vnc would otherwise push every copy made in Chromium to all connected viewers,
# view-only ones included. The clipboard goes through the supervisor instead, per request and
# only for a viewer allowed to control the browser.
start_child x11vnc -display :99 -localhost -forever -shared -nosel -xrandr resize -nopw -rfbport 5900
start_child x11vnc -display :99 -localhost -forever -shared -nosel -viewonly -xrandr resize -nopw -rfbport 5901
start_child websockify 0.0.0.0:6080 127.0.0.1:5900
start_child websockify 0.0.0.0:6082 127.0.0.1:5901
start_child socat TCP-LISTEN:9222,fork,reuseaddr TCP:127.0.0.1:9223
start_child node /opt/browser/supervisor.mjs
SUPERVISOR_PID=$CHILD_PID

while :; do
  for pid in $PIDS; do
    if ! kill -0 "$pid" 2>/dev/null; then
      wait "$pid" || status=$?
      echo "[browser-runtime] required process exited: pid=$pid status=${status:-0}" >&2
      exit "${status:-1}"
    fi
  done
  sleep 1
done
