# Telegram & Signal Chat Integration (Community) + Slack Fixes — Design

Date: 2026-07-19
Status: approved

## Goal

Bring the Slack-style session chat experience (session mirroring, replies as
session input, interactive permission prompts) to **Telegram** and **Signal**
as **community features** (AGPL core, no enterprise license required). Along
the way, fix known weaknesses of the existing Slack integration (message
truncation, permission timeouts) and add a "working…" indicator, a status
query, and browser desktop notifications.

## Decisions (with user)

- **Multi-session model**: Telegram uses **forum topics** (one topic per
  session in a forum supergroup — real thread feel). Telegram DMs and Signal
  (no thread concept) use **reply/quote routing + active session** fallback.
- **Signal transport**: `signal-cli-rest-api` as an optional sidecar
  deployment in the Helm chart (`json-rpc` mode); the admin registers a phone
  number once directly on the container.
- Everything lives in the AGPL core under `backend/Chat/` — deliberate
  contrast to the Slack EE integration. No `IEnterpriseLicense` checks.

## Architecture

### Shared core (`backend/Chat/`)

Two small moves from `ee/backend/Slack/` into the core (the community side
needs them; Slack then references the core):

- `AgentTerminal` → `backend/Services/` (send input to a session pod)
- `PermissionAction` → `backend/Permissions/` (encoding `perm:<decision>:<id>`)

New shared pieces:

- **`ChatBindingStore`** (Postgres):
  - `chat_session_bindings`: `(platform, session_id)` → owner, chat_id,
    thread_id (nullable; Telegram forum topic id), status message ref
    (nullable; for the working indicator), active flag.
  - `chat_messages`: `(platform, chat_id, message_ref)` → session_id —
    outgoing message mapping for reply/quote routing (Telegram `message_id`,
    Signal timestamp).
- **`ChatFormatting`**: pure functions — session header (`🤖 #a3f2 ·
  fix-login`), quote rendering, and `Split(text, maxLen)` (see Slack fixes).
- **Session tags**: first 4 hex chars of the session id; on inbound routing,
  accept any unambiguous prefix (collision → require more chars).

### Telegram (`backend/Chat/Telegram/`)

- `TelegramOptions` (`Chat:Telegram`): `Enabled`, `BotToken`.
- `TelegramClient`: Bot API via HttpClient — `sendMessage`,
  `editMessageText`, `deleteMessage`, `createForumTopic`,
  `answerCallbackQuery`, `getUpdates` (long polling; no public endpoint,
  analogous to Slack Socket Mode).
- `TelegramNotifier : INotifier`: on `question`, resolve the owner's linked
  chat. Forum supergroup → `createForumTopic("<title> #<tag>")`, post header +
  question into the topic. DM → reply-routing mode: message with session tag
  header, no topic. `finished`/`failed` posted like Slack.
- `TelegramPermissionNotifier : IPermissionNotifier`: inline keyboard
  (Allow / Allow always / Deny) with `callback_data` in the shared
  `PermissionAction` format; on click, edit the message (buttons removed,
  decision + user shown) — same UX as Slack.
- `TelegramUpdateService` (BackgroundService, long polling):
  - `callback_query` → `PermissionStore.ResolveAsync`; answer with a toast if
    already decided/expired.
  - Message in a forum topic (`message_thread_id`) → session via binding.
  - DM with reply → session via `chat_messages`; without reply → the chat's
    active session.
  - Commands: `/link <code>`, `/sessions`, `/use <tag>`, `/status` (+
    `!status`, see below).
- **Linking**: bots cannot initiate chats. The user generates a short-lived
  link code in the AgentHub settings and sends `/link <code>` to the bot — in
  a DM **or** in a forum group (then the group becomes the target). Chat id is
  stored in the user directory.

### Signal (`backend/Chat/Signal/`)

- `SignalOptions` (`Chat:Signal`): `Enabled`, `ApiUrl`, `Number`.
- `SignalClient`: `POST /v2/send` (with quote fields), remote delete;
  receive via WebSocket `/v1/receive/{number}`.
- `SignalNotifier`: no topics → always session tag header + question in one
  message; outgoing timestamps recorded in `chat_messages`.
- `SignalReceiveService` (BackgroundService, WebSocket):
  - Message **with quote** → session of the quoted message (the reliable
    path; mentioned in the header text).
  - Without quote → active session (the one that asked most recently).
  - Reaction 👍/👎 on a permission message → allow/deny; quote-reply
    `always` → allowAlways. Bot confirms the decision with a short message
    (Signal cannot edit).
  - Text commands: `!sessions`, `!use <tag>`, `!status`.
- **Linking**: user enters their number in settings → backend sends a
  6-digit code via Signal → user confirms it in the UI (prevents pointing
  agent output at arbitrary third-party numbers).

### Permission chain & wiring

- `InternalController` takes `IEnumerable<IPermissionNotifier>`; order
  **Slack → Telegram → Signal**, first success (`PostAsync == true`) wins;
  otherwise fall back to the web prompt as today. `ResolveAsync` is already
  idempotent.
- `INotifier` fan-out unchanged: Telegram/Signal notifiers are registered in
  addition; each skips when the user has no target there.
- `UserDirectory`/`AppUser`: new fields `TelegramChatId`, `TelegramEnabled`,
  `SignalNumber`, `SignalVerified`, `SignalEnabled` (+ migration).
- `/api/config` reports which platforms are enabled; `SettingsDialog.vue`
  gets a Telegram section (show link code) and a Signal section (number +
  verification code), analogous to the Slack section.

### Helm & operations

- `values.yaml`: `chat.telegram.botToken` (secret), `chat.signal.enabled` →
  deploys `signal-cli-rest-api` (Deployment + Service + PVC for registration
  data) and sets `Chat:Signal:ApiUrl`.
- Both default **off**; no company/environment values in versioned files.

## Slack fixes & cross-platform features

### A. Long answers: split instead of truncate

Truncation happens in two places today: `notify-hook.sh` caps at 1500 chars,
`SlackNotifier.Quote()` again at 2500.

- `notify-hook.sh`: raise cap to 12000.
- Shared `ChatFormatting.Split(text, maxLen)` — breaks at line boundaries
  (hard split only when a single line exceeds the limit). Slack ~3800,
  Telegram 4000 (hard limit 4096), Signal ~4000.
- Notifiers post chunks sequentially into the thread/topic: first chunk with
  header, follow-ups as continuation quotes (`… (2/3)`).

### B. Permission timeout fix

Cause: `pretooluse-hook.sh` polls ~4 minutes (120 × 2 s), then falls back to
`ask` — but the Slack buttons stay and appear dead afterwards.

- Poll duration configurable (`AGENTHUB_PERMISSION_POLL_SECONDS`, default
  **30 min**); raise the hook timeout in the agent `settings.json`
  accordingly.
- When the hook gives up it reports it:
  `POST /internal/sessions/{id}/permission/{reqId}/expire` → backend marks
  the request `expired` and **edits the chat message**: "⏰ Expired — please
  answer in the web terminal", buttons removed.
- Clicking an already decided/expired request → short feedback ("already
  decided") instead of silence.

### C. "Claude is working…" indicator + status query

Slack bots have no real typing indicator, therefore:

- After a chat reply is delivered to the session, the bot posts
  **"⏳ Claude is working…"** into the thread/topic. A lightweight updater
  animates the message via `chat.update`/`editMessageText` every ~5 s
  (⏳→⌛, walking dots), capped at 30 min. On the session's next event
  (question/finished/failed) the message is **deleted** — the animation
  disappears once the answer arrives. Signal cannot edit → static message +
  remote delete there.
- **Status query `!status`** (uniform on all three platforms; Telegram also
  `/status` in the bot menu): replies with phase (Running/Waiting/Finished),
  whether a question or permission is pending, runtime, and the web link.
  Intercepted before forwarding to the session — exact keyword only, so
  normal replies don't collide.

### D. Desktop notifications (frontend)

- On the existing 5 s refresh, the frontend detects transitions:
  `questionPending` false→true or status → finished/failed → **web
  notification** ("Session fix-login is waiting for you"), only when the tab
  is not focused. Clicking the notification focuses the tab and opens the
  session.
- Opt-in via a settings toggle (triggers `Notification.requestPermission()`).
  Transition detection as a pure function.

## Testing

- xUnit (under `tests/`): routing decision logic as pure functions (analogous
  to `SlackTargetTests`) — Telegram update parsing → routing target, Signal
  envelope parsing (quote/reaction/text), tag resolution incl. prefix
  collisions, `ChatFormatting` (header, quote, split); `PermissionAction`
  tests (namespace move only); permission chain (first success wins);
  expire flow.
- vitest (frontend): new settings sections; notification transition
  detection.

## Out of scope (YAGNI)

Signal group support, media/file sending, Telegram webhook mode, multiple
bots/numbers per instance, terminal scrollback in chat (same reasoning as
Slack), per-user permission-notifier preference ordering.
