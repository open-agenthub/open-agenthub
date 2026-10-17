# Session startup diagnostics

SessionInfo carries `failureReason` through REST, remote MCP, personal MCP and the in-pod
child/peer MCP. The list and session workspace display it as text. The backend maps init-container
termination, agent termination, image waiting states, scheduling conditions and admission/quota
refusals. A nonzero init exit overrides Pending with Failed: the pod cannot reach the agent.
Image pull and scheduling warnings remain Pending because Kubernetes may recover without a resume.

The clone script captures stderr in memory, sanitizes it before logging or writing its termination
message, and preserves Git's exit code. Its repository index identifies the failed repository even
when five clones share one init container. After a failed clone, ls-remote --symref supplies the
default branch when accessible. It never silently substitutes that branch: doing so could run an
agent against code the caller did not select. Edit the session repositories instead.

A targeted store update preserves the sanitized reason after pod collection without overwriting
concurrent title/policy edits. Resume clears it; a healthy live pod clears transient warnings.
Polling uses the same mapper for list/get and parent peer reads. We chose termination messages over
fetching logs on every dashboard poll: log access needs a kubelet, costs extra requests, and risks
copying unrelated output. Older pods without the new structured clone message still show their
sanitized Kubernetes diagnostic, but cannot identify a particular clone without that evidence.

Redaction precedes truncation at both the script output and backend persistence/display boundaries.
URL userinfo, authentication headers, cookies, named secrets and common token formats are removed.
Unknown failures remain bounded plain text. Tests cover secret-bearing inputs separately from
mapping, and execute the factory's real shell script against a local bare repository.

## Reproduce the API object proof

Run `dotnet run --project tests/SessionStartupProof -- /path/to/kubeconfig` against a scratch
API server. It creates an isolated namespace, writes four pod statuses through the status
subresource, reads them back and asserts the mapper step/terminal flag and credential removal.
It needs no kubelet. The verified output ends with:

```text
REAL API clone-failure: phase=Pending, step=clone, terminal=True, reason=clone: Branch main does not exist in https://example.com/repo5. The default branch is instagram. Edit the session's repositories.
PASS: four statuses written/read through a real kube-apiserver; no kubelet used.
```

The real clone-script regression executes the factory's emitted shell, rather than a copy, against
a local bare repository. It asserts exit 128 and Git's exact missing-branch error. A separate
script regression injects credential-bearing output and asserts that neither stdout nor the
termination file contains those values. PostgreSQL tests exercise the actual migration, column
projection and targeted update, including resume clearing the old failure.
