# Companion pane painting under the conversation (#420)

A Files preview of a markdown document with a table came out wider than the companion column
and painted under the conversation. At 1280 px with the split at 25 %, the companion ended at
x=506 but the preview box ran to x=824. The conversation column had no background of its own,
so the preview's background and text showed through behind messages and tool rows.

Cause: `.file-main`, `.files-companion` and `.companion-side` are grids that set only
`grid-template-rows`. Their single column is therefore an implicit `auto` track, and Chromium
sized it to the preview's min-content width (the table's), although the preview has
`min-width: 0`. The grid item stayed 13 px wide; its column was 426 px. v1.12.1 overflows the
same way (preview to x=795), so this is older than #411 and #375; #411 only moved the table's
minimum from 387 to 416 px.

Change: every one of these grids gets `grid-template-columns: minmax(0, 1fr)`. The companion
column and its body clip (`overflow: hidden`). The conversation column paints `var(--bg)`.
The preview document has `min-width: 0`, so a wide table scrolls inside its own `.md-table`
wrapper rather than widening the document. The companion body is a size container: the Files
pane uses its compact 110 px file list and the preview trims its padding when the column is
narrow, not only when the window is. Without that, a 25 % split at 1280 px left the preview
about 100 px and broke headings letter by letter.

Rejected alternatives: only giving the conversation an opaque background hides the symptom while
the preview stays cut off at the seam with no way to scroll to it. Only clipping the column
leaves the same, unreachable, cut-off content. Raising z-index on the conversation does the
same and adds a stacking context that the composer's menus would then have to escape.

Run against this checkout's Vite frontend (`cd frontend && npm ci && npx vite --port 5173`):

```sh
PLAYWRIGHT_MODULE=/path/to/playwright-core \
CHROMIUM_PATH=/path/to/chrome-headless-shell \
UI_URL=http://localhost:5173 OUTPUT_DIR=/path/to/output \
node tests/CompanionPaneOverflowProof/check.cjs
```

API and WebSocket transport are intercepted in the browser; layout, Files pane and the markdown
renderer are the real frontend. It opens Files on a markdown file with a prose table, a 14-column
table, long prose, a long link and inline code, at 1280, 1440, 1920 and 900 px with the split at
25 %, 50 % and 75 %, plus the 390 px single-pane layout. It asserts:

- no pane box is wider than the companion column;
- `elementsFromPoint` on a grid over the conversation column and down the seam never contains
  companion content. `elementFromPoint` alone is not enough: the conversation scroller wins the
  top hit even where it is transparent, so it reported no hit while the preview showed through;
- the conversation column has an opaque background;
- the preview does not scroll sideways, and the wide table scrolls inside its own wrapper.

`PHASE=before` records without asserting. Screenshots and JSON go to OUTPUT_DIR (a fresh
temporary directory when unset); never commit them.

Not covered: with a 900 px window and the split at 25 %, the column is 163 px, so the preview
is still only about 40 px wide. The Browser pane is measured separately in the PR; this script
only opens Files. In a narrow conversation column the composer can still widen the page; that
happens on v1.12.1 as well and is outside the companion column.
