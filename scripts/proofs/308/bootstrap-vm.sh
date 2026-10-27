#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT=${TASK_ROOT:-/tmp/agenthub-video-rc5}
mkdir -p "$TASK_ROOT"/{apt/lists/partial,apt/cache/archives/partial,debs,root,private}
chmod 700 "$TASK_ROOT/private"
apt_opts=(-o "Dir::State::Lists=$TASK_ROOT/apt/lists" -o "Dir::Cache=$TASK_ROOT/apt/cache" -o Debug::NoLocking=1)
apt-get "${apt_opts[@]}" update >"$TASK_ROOT/apt-update.log" 2>&1
apt-get "${apt_opts[@]}" --no-install-recommends --download-only -y install qemu-system-x86 qemu-utils genisoimage >"$TASK_ROOT/apt-download.log" 2>&1
for package in libfuse3-3 libnl-3-200; do (cd "$TASK_ROOT/debs"; apt-get "${apt_opts[@]}" download "$package"); done
for package in "$TASK_ROOT"/apt/cache/archives/*.deb "$TASK_ROOT"/debs/*.deb; do dpkg -x "$package" "$TASK_ROOT/root"; done
export LD_LIBRARY_PATH="$TASK_ROOT/root/usr/lib/x86_64-linux-gnu:$TASK_ROOT/root/lib/x86_64-linux-gnu"
export QEMU_MODULE_DIR="$TASK_ROOT/root/usr/lib/x86_64-linux-gnu/qemu"
if [ ! -f "$TASK_ROOT/debian.qcow2" ]; then
 curl -fL --retry 2 https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-genericcloud-amd64.qcow2 -o "$TASK_ROOT/debian.qcow2"
 curl -fsSL https://cloud.debian.org/images/cloud/bookworm/latest/SHA512SUMS -o "$TASK_ROOT/SHA512SUMS"
 node - "$TASK_ROOT" <<'NODE'
const fs=require('fs'),crypto=require('crypto'),p=process.argv[2];
const expected=fs.readFileSync(p+'/SHA512SUMS','utf8').split('\n').find(x=>x.endsWith('  debian-12-genericcloud-amd64.qcow2'))?.split(/\s+/)[0];
const actual=crypto.createHash('sha512').update(fs.readFileSync(p+'/debian.qcow2')).digest('hex');
if(!expected||actual!==expected)throw Error('Debian cloud image checksum mismatch');
fs.writeFileSync(p+'/cloud-image.sha512',actual+'\n');
NODE
fi
if [ ! -f "$TASK_ROOT/worker.qcow2" ]; then "$TASK_ROOT/root/usr/bin/qemu-img" create -f qcow2 -F qcow2 -b "$TASK_ROOT/debian.qcow2" "$TASK_ROOT/worker.qcow2" 40G; fi
if [ ! -f "$TASK_ROOT/private/demo-ssh" ]; then ssh-keygen -q -t ed25519 -N '' -C rc5-demo -f "$TASK_ROOT/private/demo-ssh"; fi
node - "$TASK_ROOT" <<'NODE'
const fs=require('fs'),p=process.argv[2],pub=fs.readFileSync(p+'/private/demo-ssh.pub','utf8').trim();
fs.writeFileSync(p+'/user-data',`#cloud-config\nhostname: rc5-demo\nusers:\n  - name: demo\n    groups: [sudo]\n    shell: /bin/bash\n    sudo: ALL=(ALL) NOPASSWD:ALL\n    ssh_authorized_keys:\n      - ${pub}\ndisable_root: true\nssh_pwauth: false\n`);
fs.writeFileSync(p+'/meta-data','instance-id: rc5-demo-local-001\nlocal-hostname: rc5-demo\n');
NODE
"$TASK_ROOT/root/usr/bin/genisoimage" -quiet -output "$TASK_ROOT/seed.iso" -volid cidata -joliet -rock "$TASK_ROOT/user-data" "$TASK_ROOT/meta-data"
echo "VM dependencies and verified image prepared at $TASK_ROOT"
