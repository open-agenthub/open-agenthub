# Model switch proof (#478)

`cli-switch.mjs` runs one pinned agent CLI in a real PTY against a mock model and answers the
question "does a model switch mid-conversation reach the model, with the conversation intact?".
The mock replies `MODEL=<model in the request> CONTEXT=KEPT|NONE` (whether the first turn's marker
word is still in the request), and logs only the URL, model and that flag — never a header.

```bash
export CLIS=<dir with node_modules/.bin/{codex,opencode,openclaw}>   # npm i the pinned versions
export XTERM=<dir with node_modules/@xterm/headless>                 # @xterm/headless@5.5.0
node cli-switch.mjs claude '[{"ready":"for shortcuts|auto mode"},{"paste":"Remember the word PAPAYA42."},
  {"ready":"MODEL="},{"paste":"/model claude-sonnet-5-5"},{"ready":"Switch model\\?","ms":5000},{"keys":"\r"},
  {"ready":"Set model to"},{"paste":"Which word?"},{"ready":"MODEL=claude-sonnet"},{"dump":"turn2"}]' out-claude
```

Environment switches: `START_MODEL` (the model the session starts on), `CUSTOM_ENDPOINT=1` (Codex
with the runtime's custom provider block), `REUSE=<earlier outdir>` plus `EXTRA_ARGS='["resume","--last"]'`
(the restart half: same HOME and work dir, the CLI's resume arguments).
