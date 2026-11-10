# Ciphertext canary proof

Run `bash tests/CiphertextCanaryProof/repeat.sh` with the .NET 10 SDK and
`AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` pointing to a disposable real Postgres.
The script runs both affected test classes, then repeats the reported database test
200 times by invoking the actual test method in a console runner, stopping at the first
failure. Every invocation creates its own database schema and randomized ciphertext.
This avoids starting 200 test hosts while retaining the exact test assertions. Keep generated output outside the repository.

The tests use fixed 32-character canaries because short plaintext can occur by chance
in randomized base64 ciphertext. Keeping the plaintext checks and round-trip assertions
preserves the intended coverage; removing the plaintext checks would weaken it.
