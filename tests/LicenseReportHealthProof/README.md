# Licence report health proof

Run with the .NET 10 SDK and a disposable real Postgres database:

```sh
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres' \
  dotnet run --project tests/LicenseReportHealthProof
```

The proof creates and drops an isolated schema, starts an actual HTTP licence-service
stand-in, runs the production SeatUsageReporter, LicenseStore and EnterpriseLicense, and
reads the production AdminController over HTTP. A generated local signing key is supplied
only through the existing verifier test constructor; the production compiled key is unchanged.

Checks cover initial never-reported state, signed expiry, rejected HTTP reports, missing and
invalid renewals preserving the previous token, shared database persistence through a new
store instance, successful clearing, a real connection-refused failure, preserving last success,
configurable warning lead time, zero lead time and expired tokens. No output is committed.
