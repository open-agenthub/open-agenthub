# Backend TLS trust primitive

Run `bash tests/BackendCaProof/prove.sh` with .NET 10, Node and OpenSSL installed.
A generated private root signs a local HTTPS issuer. The same real .NET HttpClient
fails with a chain-trust error before SSL_CERT_FILE is supplied and succeeds afterwards.
This checks the runtime trust mechanism before pushing; the separate workflow-dispatch
private-CA install proof checks the chart mount and actual backend OIDC/admin paths.
All certificates, keys and output are temporary and must not be committed.

The positive probe also fetches `https://example.com` using the same HttpClient and
private-CA-only `SSL_CERT_FILE`, proving that public HTTPS trust remains available.
Set `PROOF_PUBLIC_TLS_URL` when running the executable directly to enable this check.
The private-CA install dispatch publishes this executable with the .NET SDK, copies
only its managed output to the ready backend pod's `/tmp`, confirms that the mounted
bundle is byte-identical to the single private root, then runs it with the pod's real
TLS environment and runtime. It does not change the backend image or pod trust settings.
