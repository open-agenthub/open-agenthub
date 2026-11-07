#!/bin/sh
# Runs inside the guest (copied to ~/audit): (re)starts the two port-forwards a prospect keeps open,
# the hub UI on localhost:8080 and the IdP stand-in on kc.example.test:18081. Matches kubectl by
# its exact name, so the calling shell's own command line can never match.
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
pkill -x kubectl
sleep 1
nohup kubectl -n agenthub port-forward svc/agenthub-frontend 8080:80 >/tmp/pf.log 2>&1 &
nohup kubectl -n audit port-forward svc/keycloak 18081:18081 >/tmp/kcpf.log 2>&1 &
sleep 4
curl -s -o /dev/null -w "hub %{http_code}\n" http://localhost:8080/
curl -s -o /dev/null -w "idp %{http_code}\n" http://kc.example.test:18081/realms/team
