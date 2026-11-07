# Quickstart team ingress proof

The install-smoke workflow runs `bash tests/TeamIngressProof/prove.sh` after the real
quickstart install. It starts a synthetic issuer, enables team ingress and verifies
that the former class fails before selecting `traefik`. A local one-day certificate
allows strict HTTPS verification through the node ingress. Frontend HTML, OIDC config,
PKCE/refresh and the authenticated admin API must all succeed. Service port-forwarding
was rejected because it would bypass the controller mismatch this proof detects.
The issuer's HTTP metadata is a local-test concession, not a team deployment setting.
Keys and request output remain temporary; only this script is versioned.
