#!/usr/bin/env bash
set -euo pipefail
# Run after Factory writes manifests, the disposable worker is Ready and restricted
# enforcement is enabled on tmp-proof. Fail on any unexpected mode or writable root.
script_dir=$(cd "$(dirname "$0")" && pwd)
for pod in agent-tmp-proof browser-tmp-proof; do
  bash "$script_dir/ssh.sh" "sudo k3s kubectl -n tmp-proof exec $pod -- sh -ec 'test \"\$(id -u)\" = 1000; test \"\$(stat -c %a /tmp)\" = 1777; touch /tmp/writable; if touch /root-write-test 2>/dev/null; then exit 1; fi; grep -q \" / .*ro,\" /proc/mounts; stat -c \"%a %u:%g\" /tmp; id; grep \"^CapEff:\" /proc/self/status'"
done
