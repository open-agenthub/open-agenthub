#!/bin/bash
# Prepares <dir> for mock-gitlab.mjs: a throwaway CA and a server certificate for 127.0.0.10, the
# OAuth client secret (random, never printed), and a private bare repository with a main branch.
set -e
D=${1:?dir}; mkdir -p $D/certs $D/repos
cd $D/certs
openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 2 -subj /CN=proof-ca 2>/dev/null
printf '[ext]\nsubjectAltName=IP:127.0.0.10\nbasicConstraints=CA:FALSE\n' > san.cnf
openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj /CN=127.0.0.10 2>/dev/null
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out server.crt -days 2 -extfile san.cnf -extensions ext 2>/dev/null
# The system bundle plus this CA, for the backend (SSL_CERT_FILE) and git (http.sslCAInfo).
cat /etc/ssl/certs/ca-certificates.crt ca.crt > bundle.crt
openssl rand -hex 24 > $D/client-secret.txt
rm -rf $D/repos/group && mkdir -p $D/repos/group
git init -q --bare -b main $D/repos/group/project.git
git -C $D/repos/group/project.git config http.receivepack true
W=$(mktemp -d); git -C $W init -q -b main; git -C $W -c user.name=proof -c user.email=proof@example.com commit -q --allow-empty -m init
git -C $W push -q $D/repos/group/project.git main; rm -rf $W
echo "ready: $D"
