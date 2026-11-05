# Who may drive a session from its Slack thread (#436)

`Program.cs` starts the production `SlackSocketModeService` as the hosted service it is in the
backend, wired with the real `SlackClient`, `SlackSenderGate`, `SessionAccessService`,
`AgentPodRequests` and the real Postgres session, user, share, Slack thread and permission
stores. Slack itself is a local stand-in:

- `apps.connections.open` hands out a `ws://` URL served by the proof, and envelopes arrive over
  that socket the way Socket Mode delivers them; the proof records every ack;
- `users.info` answers each Slack user's email (`U_STRANGER`'s matches no AgentHub account);
- every other Web API call (`chat.postMessage`, `chat.postEphemeral`, `chat.update`,
  `chat.delete`) is recorded with its body;
- `SlackClient`'s requests to `slack.com` are rewritten to the stand-in; any other host fails.

A stand-in session pod on 127.0.0.1 accepts the terminal WebSocket (`tty`) and records what is
typed. The pod identity check is answered "live".

Seed data: alice owns `s-channel` (thread in channel `C0PROOF`) and `s-dm` (thread in her DM);
`s-channel` is shared with bob as Collaborator and vic as Viewer; mallory is a linked user
with no share. Two permission requests are pending on `s-channel`.

The run sends thread replies, `!status` and every permission button (allow, allowAlways, deny)
from each of them, then checks: only alice's and bob's replies reach the pod; no refused text
appears in any Slack call; each refused sender gets one ephemeral notice; refused clicks leave
the request pending, add no always-allow rule and do not rewrite the prompt; alice's allow and
bob's deny are recorded.

```bash
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres" \
  dotnet run --project tests/SlackSenderProof
```

Exit code 0 and `PROOF PASSED` when every expectation holds. The run uses its own schema and
drops it afterwards.
