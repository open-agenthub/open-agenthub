# Backend outbound TLS trust

The backend accepts `backend.trustedCaConfigMap`, an existing ConfigMap in the release
namespace with a PEM bundle at `ca.crt`. The chart mounts only that key read-only and
sets `SSL_CERT_FILE` for the whole backend process, including OIDC discovery, signing
keys and other outbound TLS. Empty keeps the runtime trust configuration unchanged.
The runtime also retains its system certificate directory; include any required roots
in the supplied bundle when using a custom backend image. Restart the deployment after
updating the ConfigMap so cached trust and HTTP connections are recreated.

A ConfigMap was chosen because CA certificates are public trust material. Reusing
`agent.trustedCaConfigMap` was rejected: it lives in the sessions namespace and should
not silently change control-plane trust. Disabling certificate validation was rejected
because it would accept an impersonated issuer. The chart does not modify the read-only
root filesystem or require a privileged certificate installation step.
