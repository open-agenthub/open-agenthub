# Sessions that delete themselves

A session can be told to go away on its own: after a fixed time **since it started**, or after a
period **without anyone using it**. The setting lives on the session (web app, `PATCH
/api/sessions/{id}`, `PATCH /api/remote/sessions/{id}`, and the `session_create` tool of all three
MCP servers), the remaining time is visible wherever the session is listed, and a background sweep
in the backend performs the deletion. This file records the decisions and the alternatives each one
rejected.

## The problem

Sessions accumulate. A session created by an API caller for a one-off task, a child spawned by
another agent, an interactive session somebody opened and forgot — each keeps its database row, its
secrets, its MCP registration and, while it runs, a pod. Nothing cleaned them up except a person
clicking delete, and the people who create the most sessions (automations, orchestrating agents)
are exactly the ones that never come back to do that.

## What a session knows

Three columns on `sessions`, appended at the end of the select list like every column before them
(the store maps by ordinal, and a column inserted in the middle would silently shift every field
behind it):

| Column | Meaning |
|---|---|
| `auto_delete_after_seconds INT` | null = never. 300 s minimum, 365 days maximum. |
| `auto_delete_from TEXT` | `start` or `lastActivity`. The point the countdown is measured from. |
| `last_activity_at TIMESTAMPTZ` | When a person or a caller last did something with the session. Null for rows older than the column, which read as `created_at`. |

`SessionInfo` reports all three plus the derived `expiresAt`, so a client never has to repeat the
arithmetic and the two MCP allowlists only had to name the fields.

### Why `updated_at` could not be "last activity"

`updated_at` already existed and looked like the obvious choice. It is bumped by every write to the
row, and the session pod writes the row every 30 seconds: the scrollback upload lands in
`SetScrollbackAsync`, the transcript upload in `SetTranscriptAsync`. A running session therefore
never goes idle by that clock, and "delete after 2 hours without use" would have meant "delete only
once the pod is gone". A separate timestamp with a separate, explicit set of writers was the only way
to make "use" mean what a person expects.

### What counts as use

`ISessionStore.TouchActivityAsync(id)` sets `last_activity_at` and **nothing else** — in particular
not `updated_at`, so the touch cannot be mistaken for an edit by anything that watches that column.
It is called from:

- session creation (the row is inserted with the timestamp) and resume;
- a terminal or chat attach from the web app, and — throttled to once a minute per socket — every
  frame the browser sends into the session afterwards, so a person typing for an hour keeps the
  session alive without one write per keystroke;
- replies relayed from Telegram, Signal and Slack (`AgentTerminal.SendInputAsync` callers);
- `POST /api/remote/sessions/{id}/messages`;
- the pod's own status transitions (`UpdateStatusAsync` sets the timestamp in the same statement),
  so a session that just finished its task is not deleted a minute later by a short idle window.

Deliberately **not** counted: the scrollback and transcript uploads. They are the pod talking to the
backend, not anyone talking to the session, and counting them would recreate the `updated_at`
problem under a new name.

`UpsertAsync` keeps the stored `last_activity_at` on conflict (`COALESCE(existing, new)`). The edit
dialog reads a record, changes a field and writes the whole record back; between the read and the
write a touch may have landed, and an upsert that copied the record's stale timestamp would undo it.
The same reasoning already protected `credential_id` from the pod's writeback.

## The sweep

`SessionExpirySweepService` runs every 60 seconds, like `SessionFileSweepService`, with a
`SweepOnceAsync` that tests call directly. Each pass asks the store for due sessions across **all
owners** — the first query on `sessions` that is not scoped to one owner, which is why it is a
dedicated method (`ListExpiredAsync`) with its own `WHERE` rather than a filter on `ListAsync`:

```sql
WHERE auto_delete_after_seconds IS NOT NULL
  AND (CASE auto_delete_from WHEN 'start' THEN created_at
       ELSE COALESCE(last_activity_at, created_at) END)
      + auto_delete_after_seconds * interval '1 second' < @now
```

For each hit the service:

1. takes `pg_try_advisory_lock` on the session id (a different key space than the browser lock,
   which uses the blocking `pg_advisory_lock` on the same id text);
2. re-reads the row and re-checks the deadline — a touch or an edit may have arrived between the
   listing and the lock;
3. calls `ISessionService.DeleteSessionAsync(owner, id)`, the same path the delete button uses, so
   files, browser, network policies, pod, CronJob, secrets, MCP registrations and the row all go
   together;
4. raises the notifier event `session-expired`, which the chat integrations post into an existing
   thread the way they post `finished`.

A failure on one session is logged and the loop moves on; the row is still there, so the next pass
retries. The lock is what makes several backend replicas safe: both would list the same session, only
one deletes it, the other skips and finds nothing on re-read.

### The alternative: let Kubernetes do it

`ttlSecondsAfterFinished` on Jobs and `activeDeadlineSeconds` on pods both exist, and a
`CronJob` could sweep. All three were rejected for the same reason: Kubernetes knows about the pod,
not about the session. It cannot see `last_activity_at`, so "since last use" is impossible; it
cannot delete the database row, the S3-independent secrets or the MCP registrations, so a session
whose pod was reaped would still appear in the list as a stale ghost — the exact state the feature is
meant to end. The deletion has to run where the record lives.

## Validation

- `autoDeleteAfterSeconds`: 300 to 31 536 000. Below five minutes the 60-second sweep and the
  throttled touch make the deadline meaningless; above a year it is "never" written as a number.
- `autoDeleteFrom`: `start` or `lastActivity`, case-insensitive; omitted means `lastActivity` —
  except on a **scheduled** session, where it means `start`, because a CronJob is never attached to
  and would otherwise be deleted on its first idle interval. Asking explicitly for `lastActivity` on
  a scheduled session is a `400`, not a silent correction, so an API caller learns the rule.
- On `PATCH`: `null` leaves a field alone, `autoDeleteAfterSeconds: 0` switches the feature off and
  clears the basis with it. The fields are not runtime fields: they change nothing in the pod, so a
  scheduled session may change them without recreating its CronJob — the same reasoning that lets
  `autoApprove` through the scheduled-session guard.
- Duplicating a session copies the **setting** but not the timestamps: the copy's countdown starts
  from its own creation. The duplicate dialog shows the card prefilled and always sends the fields,
  so a setting cleared there is dropped rather than copied back in.

## Durations on the MCP tools

The REST API takes seconds, because that is what the row stores and a number needs no parser. An
MCP caller is a language model composing a tool call, and "12h" is what it will write; a wrong
unit conversion there is a session deleted twelve *minutes* later. So the tools take
`autoDeleteAfter` as text — `90m`, `12h`, `3d`, or `300s` — and each server converts it before the
HTTP call: `SessionExpiry.ParseDuration` on the remote MCP server, `expiry.mjs` in the stdio and
in-pod servers. The three parsers accept exactly the same grammar and are tested against the same
table of inputs; an unrecognised string is reported as an error rather than ignored, for the same
reason malformed `repos` JSON is.

## What the web app shows

A card "Auto-delete" in the New, Edit and Duplicate dialogs: a switch, a number with a unit (hours or
days) and the basis. The edit dialog also shows the computed "expires in 2 d 3 h". The session header
and both session lists carry a badge with the remaining time, switching to the warning colour under
one hour. A deleted session disappears through the list poll that already runs; no extra push was
needed.
