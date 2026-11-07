#!/usr/bin/env bash
# In the guest: the port-forward the quickstart prints, plus a headless Chromium on the same host
# (so http://localhost:8080 is a secure context, as on a prospect's laptop). The host drives the
# browser over an SSH tunnel to its DevTools port.
set -u
cd "$(dirname "$0")"
bash ssh.sh 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
pkill -x kubectl; pkill -x chromium
nohup kubectl --context default -n agenthub port-forward svc/agenthub-frontend 8080:80 >/tmp/pf.log 2>&1 &
nohup chromium --headless=new --no-sandbox --disable-gpu --remote-debugging-port=9222 --user-data-dir=/tmp/chr --window-size=1440,900 about:blank >/tmp/chr.log 2>&1 &
sleep 5; curl -s -o /dev/null -w "frontend %{http_code}\n" http://localhost:8080/; curl -s localhost:9222/json/version | head -3'
