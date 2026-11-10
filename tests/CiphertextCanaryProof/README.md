# Ciphertext canary proof

Run `bash tests/CiphertextCanaryProof/repeat.sh` with the .NET 10 SDK and
`AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` pointing to a disposable real Postgres.
The script runs both affected test classes, then repeats the reported database test
200 times, stopping at the first failure. Keep generated output outside the repository.

The tests use fixed 32-character canaries because short plaintext can occur by chance
in randomized base64 ciphertext. Keeping the plaintext checks and round-trip assertions
preserves the intended coverage; removing the plaintext checks would weaken it.
