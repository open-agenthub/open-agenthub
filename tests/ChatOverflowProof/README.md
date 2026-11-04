# Conversation horizontal overflow (#406)

The real frontend rendered long links and inline code wider than their paragraphs. At 390 px,
the conversation's scrollWidth reached 5914 px for a 380 px clientWidth; the document still
fit the viewport. A document-only assertion would therefore miss the scrollbar inside chat.

Prose now uses overflow-wrap: anywhere. Fenced code and table wrappers retain local horizontal
scrolling and normal word boundaries. The verbatim-text rule in ConversationTimeline targets
only direct pre children so it cannot override markdown code formatting.

Rejected alternatives: hiding page or conversation overflow clips text and leaves the sizing
fault; breaking every table cell or code line removes their useful local horizontal scrolling.

Run against this checkout's Vite frontend (`cd frontend && npm ci && npm run dev`):

```sh
PLAYWRIGHT_MODULE=/path/to/playwright-core \
CHROMIUM_PATH=/path/to/chrome-headless-shell \
UI_URL=http://localhost:5173 OUTPUT_DIR=/tmp/chat-overflow-proof \
node tests/ChatOverflowProof/check.cjs
```

The script serves synthetic API/WebSocket data through browser interception; no backend,
provider credentials or live session is needed. All layout, markdown rendering, shared-view
routing and composer components come from the real frontend. Use a local development server.
It checks workspace and shared-session views at 1280, 1440, 1920 and 390 px, expands tool
output, asserts document and conversation scrollWidth <= clientWidth, and verifies both
messages' code/table wrappers remain horizontally scrollable. Browser page errors fail it.
`PHASE=before` records measurements without enforcing the corrected layout, for use with
unchanged source. Screenshots and JSON go only to OUTPUT_DIR; never commit them.
