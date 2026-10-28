#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT=${TASK_ROOT:-/tmp/agenthub-video-rc5}
script_dir=$(cd "$(dirname "$0")" && pwd)
release='v1.34.1%2Bk3s1'
curl -fL --retry 2 "https://github.com/k3s-io/k3s/releases/download/$release/k3s" -o "$TASK_ROOT/k3s"
curl -fsSL "https://github.com/k3s-io/k3s/releases/download/$release/sha256sum-amd64.txt" -o "$TASK_ROOT/k3s-checksums"
node - "$TASK_ROOT" <<'NODE'
const fs=require('fs'),crypto=require('crypto'),p=process.argv[2];
const expected=fs.readFileSync(p+'/k3s-checksums','utf8').split('\n').find(x=>/\s+k3s$/.test(x))?.split(/\s+/)[0];
if(crypto.createHash('sha256').update(fs.readFileSync(p+'/k3s')).digest('hex')!==expected)throw Error('k3s checksum mismatch');
NODE
scp -i "$TASK_ROOT/private/demo-ssh" -P 2222 -o BatchMode=yes -o "UserKnownHostsFile=$TASK_ROOT/private/known_hosts" "$TASK_ROOT/k3s" demo@127.0.0.1:/tmp/k3s
bash "$script_dir/ssh.sh" 'sudo install -m755 /tmp/k3s /usr/local/bin/k3s'
bash "$script_dir/ssh.sh" 'sudo tee /etc/systemd/system/video-k3s.service >/dev/null; sudo systemctl daemon-reload; sudo systemctl enable --now video-k3s' < "$script_dir/video-k3s.service"
