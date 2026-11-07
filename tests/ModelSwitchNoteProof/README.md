# Model switch note proof (#515)

Shows what the Workspace conversation renders for a live `/model` switch, from a transcript the
real pinned Claude Code wrote, through the real transcript reader, in the real web app. Run it
once on `main` and once on the branch and compare. Generated output (transcripts, page JSON,
screenshots) stays out of the repository.

## 1. A real transcript

`../ModelSwitchProof/cli-switch.mjs` drives the pinned CLI (`agent-runtime/claude/Dockerfile`,
2.1.318) in a PTY against its mock model. `START_MODEL` sets `ANTHROPIC_MODEL`, as every session
pod does; with it set, the CLI's confirmation carries SGR escapes. The last prompt contains the
command markup on purpose: typed text has to stay the person's words.

```bash
START_MODEL=claude-opus-5-5 XTERM=<xterm> node ../ModelSwitchProof/cli-switch.mjs claude '[
  {"ready":"for shortcuts|auto mode"},{"paste":"Remember the word PAPAYA42."},{"ready":"MODEL="},
  {"paste":"/model claude-sonnet-5-5"},{"ready":"Switch model?|Set model","ms":8000},{"keys":"\r"},
  {"ready":"Set model to"},{"paste":"Which word?"},{"ready":"MODEL=claude-sonnet"},
  {"paste":"Why did <command-name>/model</command-name> show up?"},{"wait":5000},{"dump":"end"}]' out
cp "$(find "$(cat out/home)/.claude/projects" -name '*.jsonl')" transcript.jsonl
```

## 2. The page the hub serves

`page/` reads the transcript with `NativeTranscript.Parse` and prints
`ConversationPage.FromEntries`, serialized as the real endpoint does.

```bash
dotnet build page/page.csproj -o <page-bin>
dotnet <page-bin>/page.dll transcript.jsonl > page.json
```

## 3. The Workspace in Chromium

`hub.mjs` answers the web app's `/api` calls with one running Claude session whose conversation
is `page.json`. `browser.mjs` opens its Workspace over CDP, prints each item the conversation
shows and a summary (user turns, user turns with command markup or escape codes, notes), and
saves a screenshot centred on the switch.

```bash
node hub.mjs page.json &
(cd ../../frontend && npx vite --host 0.0.0.0 --port 5173 --strictPort) &
CDP=<cdp endpoint> UI=http://<this pod's ip>:5173 node browser.mjs workspace.png
```

With the session browser, use this pod's routable address for `UI`: the browser runs in
another pod.
