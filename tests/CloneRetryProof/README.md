# CloneRetryProof

Proves the `git-clone` init container's retry (#354) in the real clone image, against a real
cluster DNS failure rather than a fake git. Needs a session that can start helper containers
(`container_run`); no kubelet access, no root.

- `Program.cs` builds the init container with the real `AgentPodSpecFactory` and prints its
  image, its `REPOS` value and the exact script with its sha256. Each argument is a repository,
  `url` or `url#branch`.
- `server.py` is the stand-in Git host: a bare repository with branch `main` over Git's dumb
  HTTP protocol, and `/private/…` answering 401.

## Run

```bash
for c in "dns|http://agent:ghp_prooftoken123@gitsrv:8080/repo.git#main" \
         "never|http://agent:ghp_prooftoken123@gitsrv-never:8080/repo.git#main" \
         "notfound|http://gitsrv:8080/nope.git#main" \
         "auth|http://agent:ghp_prooftoken123@gitsrv:8080/private/repo.git#main" \
         "branch|http://gitsrv:8080/repo.git#no-such-branch"; do
  dotnet run --project tests/CloneRetryProof -- "${c#*|}" > "${c%%|*}.json"
done
```

A helper container's argument is capped at 4 KiB, and the session pod does not accept
connections from its helpers, so stage the files in a `scripts` helper (`python:3.12`, port
8000, a few lines of `http.server` accepting PUT) and `curl -T` the script, each case's `REPOS`
and `server.py` into it. Then start one `alpine/git:2.45.2` helper per case, with an emptyDir
on `/workspace` and `HOME=/tmp/home`, running:

```sh
wget -qO /tmp/clone.sh http://scripts:8000/clone.sh; sha256sum /tmp/clone.sh
REPOS=$(wget -qO- http://scripts:8000/$CASE.repos); export REPOS
/bin/sh /tmp/clone.sh; echo "EXIT=$?"; cat /dev/termination-log; sleep 86400
```

The DNS case is the one from the incident: start `clone-dns` first, while no `gitsrv` Service
exists, so the cluster DNS answers NXDOMAIN; then start `gitsrv` (`python:3.12`, port 8080,
running `server.py`). `never` targets a name that never resolves. The permanent cases run once
`gitsrv` is up.

## Result (2026-10-30, script sha256 3333e2e1…)

| Case | Attempts | Exit | Time |
|---|---|---|---|
| dns: Service created after attempt 4 | 5 | 0, repository cloned | 32 s |
| never resolves | 8 | 128, `failure: network` | 122 s |
| 404 | 1 | 128 | 0 s |
| 401 | 1 | 128 | 0 s |
| unknown branch | 1 | 128, `default-branch: main` | 0 s |

No log line or termination message contained `ghp_prooftoken123`.

`--map` feeds a termination message from such a run through `SessionStartupFailure`:

```bash
printf '%s' "$TERMINATION" | dotnet run --project tests/CloneRetryProof -- --map "<url>#main"
```

For the `never` run it printed `clone: Network error: could not reach repository
http://[redacted]@gitsrv-never:8080/repo.git (branch main) after 8 attempts over 122 s. …`.
