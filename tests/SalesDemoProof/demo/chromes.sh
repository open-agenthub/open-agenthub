#!/usr/bin/env bash
# One headless Chromium per demo user (alice 9301, bob 9302, carol 9303), each with its own
# profile, so sign-ins survive between script runs; scripts attach with connectOverCDP.
# Same proxy and SPKI pins as browser.mjs. Needs the libraries from the rootless-chromium setup
# on LD_LIBRARY_PATH (CHROME_LIBS, default ~/rig/chromium).
set -u
W=${CHROME_LIBS:-$HOME/rig/chromium}
export LD_LIBRARY_PATH="$W/root/usr/lib/x86_64-linux-gnu:$W/root/lib/x86_64-linux-gnu" FONTCONFIG_FILE="$W/fonts.conf"
CHROME=${CHROME:-$HOME/.cache/ms-playwright/chromium-1194/chrome-linux/chrome}
SPKI=$(cat "$HOME/rig/m2/pki/spki.txt")
port=9301
for user in alice bob carol; do
  "$CHROME" --headless=new --no-sandbox --disable-dev-shm-usage --remote-debugging-port=$port \
    --user-data-dir="$HOME/rig/m2/profiles/$user" --proxy-server=socks5://127.0.0.1:${SOCKS_PORT:-11080} \
    --ignore-certificate-errors-spki-list="$SPKI" --lang=en-US --window-size=1920,1080 \
    --hide-scrollbars --force-device-scale-factor=1 about:blank >"$HOME/rig/m2/chrome-$user.log" 2>&1 &
  port=$((port + 1))
done
wait
