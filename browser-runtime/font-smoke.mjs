// Inspect actual fallback: canvas widths can confuse a missing-glyph box with a symbol.
import { chromium } from 'playwright-core';
import assert from 'node:assert/strict';

const browser = await chromium.connectOverCDP(process.env.AGENTHUB_BROWSER_CDP_URL || 'http://127.0.0.1:9223');
let page;
try {
  page = await browser.contexts()[0].newPage();
  await page.setContent('<style>span { font-family: Symbola }</style><span id="hourglass">⌛</span><span id="duplicate">⧉</span><span id="emoji">😀</span>');
  await page.evaluate(() => document.fonts.ready);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('DOM.enable');
  await cdp.send('CSS.enable');
  const { root } = await cdp.send('DOM.getDocument');
  for (const id of ['hourglass', 'duplicate', 'emoji']) {
    const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector: '#' + id });
    const { fonts } = await cdp.send('CSS.getPlatformFontsForNode', { nodeId });
    assert(fonts.some(font => font.familyName === 'Symbola' && font.glyphCount > 0), id + ': symbol font did not render the glyph');
    console.log(id + ': symbol font rendered the glyph');
  }
  await cdp.detach();
} finally {
  if (page) await page.close();
  await browser.close();
}
