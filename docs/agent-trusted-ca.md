# Additional certificate authorities for agent runtimes

Attachments stored in S3 are downloaded by the session's Node process. Backend TLS settings do
not affect this connection. For storage using a private CA, create a ConfigMap in the sessions
namespace containing the verified PEM CA bundle under `ca.crt`, and set
`agent.trustedCaConfigMap` to its name.

Every provider receives a read-only mount at `/etc/agenthub/ca` and
`NODE_EXTRA_CA_CERTS=/etc/agenthub/ca/ca.crt`. Node adds these authorities to its normal public
trust roots; certificate verification stays enabled. The bundle contains public CA certificates,
never private keys. Keep environment-specific certificates outside the repository.

The setting applies when a session pod starts. Pause and resume existing sessions to pick it up;
changing a ConfigMap alone does not reload the certificates in an already running Node process.
