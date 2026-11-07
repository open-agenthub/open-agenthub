#!/usr/bin/env bash
# Puts the demo instance back to its starting state, so the demo can be run again: deletes
# alice's sessions and projects, clears the admin settings the demo makes (group roles, limits,
# allowed agents) and restarts the stand-in git host, whose repository lives in memory and is
# re-created with issue #42 unfixed. Users, licence and the team setup stay.
set -euo pipefail
cd "$(dirname "$0")/../../SalesPathAuditProof"
bash ssh.sh 'bash -s' <<'GUEST'
set -e; export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
H="Authorization: Bearer $(curl -s -d "grant_type=password&client_id=agenthub&username=alice&password=alice-demo-pw&scope=openid" \
  https://id.example.com/realms/team/protocol/openid-connect/token | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')"
api() { curl -sS -o /dev/null -w "%{http_code} $1 $2\n" -X "$1" "https://hub.example.com/api$2" -H "$H" -H 'content-type: application/json' ${3:+-d "$3"}; }
for id in $(curl -s "https://hub.example.com/api/sessions" -H "$H" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(" ".join(s["id"] for s in (d if isinstance(d,list) else d.get("items",[]))))'); do api DELETE "/sessions/$id"; done
for id in $(curl -s "https://hub.example.com/api/projects" -H "$H" | python3 -c 'import sys,json;print(" ".join(p["id"] for p in json.load(sys.stdin)))'); do api DELETE "/projects/$id"; done
for g in platform developers reviewers; do api PUT "/ee/admin/groups/$g/role" '{"role":null}'; done
api DELETE "/ee/admin/limits/group?target=developers" || true
api PUT /admin/allowed-agents '{"agents":[]}'
kubectl -n demo rollout restart deploy/git deploy/model >/dev/null
kubectl -n demo rollout status deploy/git --timeout=5m >/dev/null
until kubectl -n demo logs deploy/git 2>/dev/null | grep -q 'reproduces issue #42'; do sleep 5; done
echo "demo reset: git host has issue #42 unfixed"
GUEST
