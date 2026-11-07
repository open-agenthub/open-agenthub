#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT=${TASK_ROOT:-$HOME/rig/sales-audit-vm}
export LD_LIBRARY_PATH="$TASK_ROOT/root/usr/lib/x86_64-linux-gnu:$TASK_ROOT/root/lib/x86_64-linux-gnu"
export QEMU_MODULE_DIR="$TASK_ROOT/root/usr/lib/x86_64-linux-gnu/qemu"
node "$(dirname "$0")/cloud-seed.cjs" "$TASK_ROOT" &
seed_pid=$!
trap 'kill "$seed_pid" 2>/dev/null || true' EXIT
# 10 GiB guest / 8 vCPUs on a 16-CPU / 20 GiB pod.
# Conservative CPU avoids the guest sigreturn/page-fault panic observed with cpu=max.
"$TASK_ROOT/root/usr/bin/qemu-system-x86_64" -accel tcg,thread=multi -machine q35 -cpu Nehalem -smp 8 -m 10240 \
 -L "$TASK_ROOT/root/usr/share/qemu" -bios "$TASK_ROOT/root/usr/share/seabios/bios-256k.bin" \
 -display none -qmp "unix:$TASK_ROOT/qmp.sock,server=on,wait=off" -serial "file:$TASK_ROOT/console.log" -no-reboot \
 -drive "file=$TASK_ROOT/worker.qcow2,format=qcow2,if=virtio" \
 -drive "file=$TASK_ROOT/seed.iso,if=virtio,format=raw,readonly=on" \
 -smbios 'type=1,serial=ds=nocloud-net;s=http://10.0.2.2:18000/' \
 -device virtio-net-pci,netdev=net0 \
 -netdev user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=tcp:127.0.0.1:16444-:6443,hostfwd=tcp:127.0.0.1:18080-:8080
