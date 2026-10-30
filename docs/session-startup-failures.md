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

The agent container writes its own termination message as well (#244). Without one, Kubernetes
has only the container's reason, and an autonomous run whose CLI exited 1 on an expired login was
listed as "agent start: Error". The person reading that cannot tell a login problem from a crash.
`common/server.js` writes the driver name, the exit code and the CLI's last three output lines when
the agent exits non-zero. They are bounded to 600 characters, and they are what a person in the
terminal would have read.

The session list is readable by more people than the terminal, so those lines go through three
filters:

- The session's secret values are masked.
- Every line that held a sign-in value the filter captured is dropped.
- A scrub by shape removes URL queries, tokens, device and paste codes, and `NAME=value`
  environment lines.

The shape scrub is not optional. A real Codex device login that failed on its token request drew
the code and exited before the sign-in filter could capture it, and the first version of this
message showed that code (#287). Quoting the filtered scrollback instead was rejected: it omits
everything from a sign-in screen on, which hid the reason the login failed.
`entrypoint-common.sh` traps ERR for a
step that fails before the session agent exists, and names the failed command as written, before
expansion, so no secret value is in it. Fetching pod logs instead was rejected for the reasons
above.

Redaction precedes truncation at both the script output and backend persistence/display boundaries.
URL userinfo, authentication headers, cookies, named secrets and common token formats are removed.
Unknown failures remain bounded plain text. Tests cover secret-bearing inputs separately from
mapping, and execute the factory's real shell script against a local bare repository.

## Retrying the clone on network errors (#354)

The first session started after a quiet period sometimes failed with `Could not resolve host:
github.com`, while other pods resolved the same name in the same minute and a respawn a minute
later worked. The pod most likely lands on a freshly started node whose DNS path is not ready
yet. One lookup failure ended the session, because the clone ran exactly once.

The clone step now retries, but only output that a network fault produces: an unresolvable host
or proxy, a refused, reset or timed-out connection, a dropped transfer, and a 502/503/504. The
delays are 2, 4, 8, 16, 30, 30 and 30 seconds: eight attempts over about two minutes, which
covers the minute-long window the incidents showed. A 300-second deadline stops the retry when
each attempt itself waits out a TCP connect timeout. A 404, a 401/403, a refused SSH key and a
missing branch match none of these patterns and still fail on the first attempt: retrying them
only delays the message that tells the user what to fix.

When the retries run out, the termination message carries `failure: network`, the attempt count
and the elapsed seconds, and the session list reads "Network error: could not reach repository
… after 8 attempts over 127 s". Without the marker the reason would read like any other clone
failure and send the user to edit a repository or credential that was correct. The default-branch
lookup is skipped in that case, as it would only repeat the network failure.

Rejected alternatives:

- Retrying every failure. A wrong URL or token would then cost two minutes before the user sees
  why, on every start.
- Waiting for DNS in a separate init step before the clone. It would need a host to probe, which
  differs per repository and proxy setup, and would not cover a connection that drops mid-clone.
- Letting Kubernetes restart the init container. Session pods use `restartPolicy: Never`, and
  that policy covers every container: changing it would also restart a failed agent, retry
  permanent clone errors without end, and leave no record of why the clone was retried.

`tests/CloneRetryProof` runs the factory's exact script in the real clone image against a
Service that does not exist yet, so the cluster DNS fails for real.

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
