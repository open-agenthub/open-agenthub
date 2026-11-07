# Replacement pod proof

Run the explicit `install-smoke` workflow dispatch with `prove_provider_switch=true`
on the feature branch. It builds the branch's backend and pinned Claude/Codex runtime
images, installs the published chart on a disposable real k3s worker with local-path
storage, and runs `prove.sh`. This requires the workflow's normal image build and
cluster tooling; it is not a local mock-backend test.

The wrapper images redirect the native model endpoints to an isolated recording service.
Both agents still run their shipped entrypoints, managed hooks, PTY transport and pinned
interactive binaries. The service also serves two real Git repositories. Credentials are
synthetic API keys and synthetic native subscription files, never real provider accounts.
Subscription transport acceptance does not establish that a real account is entitled to
any particular model.

The maintained driver requests Claude → Codex → Claude for both credential modes through
the owner HTTP API. It checks fresh native identities, exact first native handover,
subsequent queued messages, one visible conversation, target-only credential projection,
retired callback rejection, preserved approval mode, native state archives, and byte
identity of dirty/untracked repository files and Git metadata on the same claim UID.
A Kubernetes watch must observe source deletion before target creation. Central session
deletion must remove the claim. Fixtures are removed on exit; uncertain session cleanup
is reported rather than silently deleting the underlying volume by hand.

Model responses, Git contents and credentials are synthetic. The backend, database,
object storage, Kubernetes API, kubelet, PVC mounts and native CLIs are real. Generated
logs, transcripts, archives and workflow output are evidence artifacts, never repository
content. Record the actual workflow outcome and limits in the PR; this script's presence
alone is not a passing proof. Crash/recovery fault injection remains the next delivery step.

Native subscription inference requires HTTPS workspace routing discovery in the pinned Codex
CLI. The proof generates a private CA and a separately signed server leaf only in its scratch
image context. Wrapper images trust that CA; TLS verification remains enabled. The fixture
answers native account routing for the synthetic account and decodes native zstd requests.
Its HTTP model transport is exercised through the CLI's ordinary WebSocket fallback.
A plain HTTP fixture fails native workspace-origin validation; a CA certificate used directly
as the server leaf fails native TLS validation. Neither is a product credential failure.

The backend accepts a terminal WebSocket before connecting its upstream pod. The driver
therefore waits for actual upstream bytes before accepting the connection and sending resize;
a successful upgrade alone can be followed by an upstream startup failure. Native admission
refuses a missing screen size. Bounded explicit owner retries cover a transient busy safe
point, but do not bypass guards or replay a stopped source/uncertain target.

Credential evidence combines resolved container environment, native auth files, absence of
opposite-provider mounts/key files, and the recording model's accepted credential mode.
The non-root container can deny `/proc/1/environ`; the proof respects that boundary and
never adds privilege or disables isolation to inspect credentials.

Fresh homes may contain `.claude/skills`: the common skill library intentionally creates that
shared root and links `.agents/skills` to it even for Codex. Fresh-native-state assertions
therefore reject opposite-provider credential/history paths and a unique marker placed in
each source native state directory, rather than treating the shared skill root as a resume.

`PROOF_KUBECTL=/absolute/path/to/kubectl node tests/ProviderSwitchProof/watch-format.mjs`
checks the actual client formatter against a small discovery/list/watch fixture. Kubectl's
tab writer expands JSONPath tabs into spaces; consumers must split whitespace, and establish
the watch by observing the source before starting replacement. This formatter check is not
a kubelet/deletion-order proof; the workflow verifies actual source/target lifecycle.
