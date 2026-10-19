# Pending owner messages and native delivery

The Workspace follows a sent composer message until a new native user entry contains its exact
text. PTY line endings are normalized to LF; indentation, repeated spaces and the rest of the
text stay significant. Substring matching was rejected because a longer message quoting the
same words could consume a pending send that the agent had not taken.

## Claude Code 2.1.287

A message sent during a real Bash call is first recorded as a `queue-operation` enqueue. At the
next tool result it becomes an `attachment` whose structured fields are:

- `type: queued_command`
- `commandMode: prompt`
- `humanTurn: true`
- `origin.kind: human`
- `prompt`: the owner's original text

The CLI renders this to the model as a system reminder beginning “The user sent a new message
while you were working”. The native reader uses the structured prompt and emits one user entry.
It ignores the rendered wrapper, sidechains, plugin/command attachments and queue operations.
In particular, `operation: remove` with `reason: absorbed_mid_turn` confirms scheduling but is
not the delivery source; treating an enqueue or every removal as taken could lose a cancelled
message. Reading only ordinary user records left this delivered message pending forever (#210).

## Codex 0.160.0

During a real `exec_command` sleep, the next tool output is followed by a regular
`response_item` message with `role: user` and an `input_text` part containing the exact
multiline steering message. Its native turn ID is the same as the initial prompt's, so it is
part of the running turn. The existing response-item reader already handles this equivalent;
no wrapper guessing or extra event-message parser is needed. Event records remain ignored to
avoid acknowledging the same send twice.

## Matching attachments and dismissal

Image sends retain their uploaded file IDs with the local pending row. A matching conversation
entry must carry those same IDs and the exact caption, allowing only the CLI's image framing
before it. This preserves image-send reconciliation without restoring arbitrary substring
matching. File names, timestamps and assistant or tool echoes do not acknowledge owner sends.

The existing ✕ removes a pending owner row from the local per-session Workspace state. It does
not cancel CLI work or delete a server message. This is also the recovery path for an older,
unrecognized native record; dismissing it cannot prove the agent received it.

## Proof boundary

The `workspace-composer-real-cli-e2e` rig runs the branch UI in Chromium, the real common session
server and pinned CLI in a PTY, a recording model endpoint, and the real `NativeTranscript.Append`
and `ConversationPage` through an HTTP host. The owner sends a multiline message while a real
shell sleep is running. Main's parser leaves it pending after model receipt; the fixed parser
shows one user entry and removes the pending row. The same browser verifies local dismissal.
The hub and model are fixtures; this is not a deployed identity-provider or cluster test.
