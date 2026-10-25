# Session starting proof (#241)

A just-created session showed "Could not load the conversation · Retry" in the Workspace. The
conversation endpoints answered 404 for a session that existed but had written no transcript
yet, and the Workspace could not tell that apart from an unknown session. `proof.mjs` checks
the fix end to end for a Claude and a Codex session. The backend, kube-apiserver, Postgres, object
storage and Chromium are real. The kubelet and the session agent are stand-ins inside the script:
it holds the pod Pending, marks it Running, then uploads the first transcript line the way
`common/server.js` does (`curl -T` to `AGENTHUB_TRANSCRIPT_PUT_URL`, then `PUT <callback>/transcript`).

It checks:
- `GET /api/sessions/{id}/conversation` for a just-created session: 200 with an empty page. Main
  answers 404 (run it against a main build with `api-only` to see the difference).
- An unknown session and an invalid share token stay 404.
- The Workspace shows "Session starting" with its stage, animated and with no error, through the
  Pending phase and while the agent is not reachable yet. The first entry replaces it.
- A paused session with a saved transcript, opened while Postgres is down, still shows
  "Could not load the conversation" with Retry, and Retry loads it once the database is back.

Setup: the rig of `../SessionResourcesProof/README.md` (steps 1–3, with `kubelet.mjs` **not**
running, since this script plays the kubelet). Start the backend with an unused agent port:

```bash
node ../SessionResourcesProof/backend.mjs <AgentHub.Api.dll> '{"AgentHub__AgentPort":"7699"}'
cd ../../frontend && npx vite --port 5173 --host 127.0.0.1
PG_CTL=<postgres>/bin/pg_ctl PGDATA=<postgres data dir> node proof.mjs <chrome-headless-shell> <outdir> <admin-kubeconfig>
```

The port matters. Every 127.x address is the proof pod's own loopback, and on the default 7681 the
hub attaches to the session agent that runs the proof and types terminal replies into it. With
nothing listening, the hub accepts the browser's terminal socket and then fails to reach the pod,
which is what a pod whose agent is still starting looks like.

`playwright-core` is resolved from this directory (`ln -s ../../browser-runtime/node_modules node_modules`).

What it cannot show: a real image pull or container start (the Pending phase is held by the script),
and the moment a real agent first answers the terminal. That last step, the invitation replacing
"Starting the agent…", is covered by `frontend/src/components/transcript-view.test.js`.
