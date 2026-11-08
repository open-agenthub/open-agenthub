# Support diagnostics projection

An operator-side Python collector creates a readable, owner-only JSON file and never uploads it.
Logs are absent by default. Sharing remains a customer decision after local review. Opting into
backend logs prints a personal-data warning and records it in the file; agree with the provider
before sending logs (DPA).

The collector copies only enumerated boolean/integer Helm fields, version-shaped tags (never
registry hosts), component pod counts and fixed event-reason categories. Names, event messages,
annotations, environment variables, Secret objects and session resources are not exported.
Unknown values are omitted rather than guessed safe. Custom image tags appear as `omitted`.

A small admin-authorized read endpoint projects schema names/timestamps, verified licence state,
seat counts and OIDC presence flags. It uses an explicit migration-name allow-list and never loads
user profiles. This works with external databases too. Reusing the admin overview was rejected
because that response includes personal data; direct database exec was rejected because it needs
additional database privileges and assumes bundled database deployment details.

A raw command archive with pattern-based replacement was rejected: unknown keys, multiline
messages and short secrets defeat replacement lists. Log export therefore copies only four exact
framework lifecycle messages, additionally dropping token-like lines. All unknown and continuation
lines are discarded. This deliberately sacrifices diagnostic detail to prevent prompts, files and
short unknown secrets embedded in logs from escaping. Default bundles contain no log text.

There is no additional backend RBAC role or edition gate: the existing instance administrator may
read the endpoint in both editions. Cluster collection uses the operator's existing namespace read
permissions. It does not need access to Kubernetes Secret resources. The operator supplies an
existing admin API key or OIDC access token on standard input, never a command argument.
