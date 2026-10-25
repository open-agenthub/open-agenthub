# Recent conversation windows

The Workspace requests `offset=-1` for its first conversation page. The backend resolves that
sentinel against the current length and returns at most 500 native entries (100,000 characters
for terminal history), with absolute offsets. Existing clients that omit the offset retain
forward pagination from zero. Live polling continues at `nextOffset`; loading earlier pages
changes only the beginning of the displayed window.

Eagerly draining all pages was rejected: it makes opening a finished conversation perform work
proportional to its entire history, including markdown rendering and message components. A
browser-only slice was also rejected because it still transfers every page. Earlier history
remains available through an explicit button, including for finished sessions and shared views.
Prepending preserves the scroll position so the entry being read does not move off screen.

The window bounds the initial response and rendered entries, not the hub's transcript cache or
the number of bytes the runtime must initially supply. Native parsing and image enrichment keep
their existing full-history semantics.

## Reproduction and proof

`proofs/251/Host` references the real backend and serves the actual conversation paginator and
live reader. Its session store and loopback pod identity are fixtures. The context-clear route
invokes the actual callback controller and forwarding service. `cli-follow.cjs` runs the pinned
native CLI through the real common server, production hooks and sessions MCP, against a local
recording model. No external model account or cluster is required.

Start the host with `dotnet run --project proofs/251/Host`. With the runtime dependencies
available at `/opt/session-agent/node_modules` and the proof dependencies (`@xterm/headless`,
`playwright-core` and the pinned CLI) installed under a scratch directory, run:

```sh
NODE_PATH=/opt/session-agent/node_modules PROOF_DEPS=/tmp/proof251 node proofs/251/cli-follow.cjs
PROOF_DEPS=/tmp/proof251 node proofs/251/browser.cjs
```

The browser script uses real Chromium and the production Workspace components, with local API
fixtures for unrelated panels. Its conversation responses come from the real paginator over
20,000 synthetic entries. System libraries and fonts must be available to Chromium. The script
reports initial rendering time, verifies 500 initial messages and exactly one conversation
request, then verifies that Load earlier prepends 500 entries without discarding the latest.

The local baseline proof also passed the rc.5 runtime's context-clear sequence and three later
turns in Chromium, using the current backend reader; reloading retained those turns. The reader's
release-to-main change centralizes pod verification and HTTP timeouts, while its identity and byte
cursor algorithm is unchanged. These runs have not reproduced the reported freeze. They do not
establish that an affected deployed session is healthy, or verify a cluster rollout.
