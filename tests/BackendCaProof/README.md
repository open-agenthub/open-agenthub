# Backend TLS trust primitive

Run `bash tests/BackendCaProof/prove.sh` with .NET 10, Node and OpenSSL installed.
A generated private root signs a local HTTPS issuer. The same real .NET HttpClient
fails with a chain-trust error before SSL_CERT_FILE is supplied and succeeds afterwards.
This checks the runtime trust mechanism before pushing; the separate workflow-dispatch
private-CA install proof checks the chart mount and actual backend OIDC/admin paths.
All certificates, keys and output are temporary and must not be committed.
