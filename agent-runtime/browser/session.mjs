import { connectBrowser } from './connection.mjs';
import { SnapshotRefs, accessibilitySnapshot } from './snapshot.mjs';

// How long a failed action waits for the browser to answer a plain Browser.getVersion before it
// treats the connection as gone. A browser pod that disappears without closing its sockets
// leaves the WebSocket open with nobody behind it, and Playwright still reports it connected.
const PROBE_TIMEOUT_MS = 5_000;

export class BrowserSession {
  constructor(backend, engine, connect = connectBrowser, probeTimeoutMs = PROBE_TIMEOUT_MS) {
    this.backend = backend;
    this.probeTimeoutMs = probeTimeoutMs;
    this.engine = engine;
    this.connect = connect;
    this.browser = null;
    this.endpoint = null;
    this.context = null;
    this.page = null;
    this.connecting = null;
    this.refs = new SnapshotRefs();
  }

  // Concurrent tool calls share one connection attempt; two would leave the loser's connection
  // open and its page unwatched.
  start() {
    this.connecting ??= this.#start().finally(() => { this.connecting = null; });
    return this.connecting;
  }

  async #start() {
    const connection = await this.backend.start();
    // A replaced browser pod comes back on a new address. The old connection can still look
    // connected, so the endpoint decides, not isConnected().
    if (this.browser?.isConnected() && this.endpoint === connection.cdpEndpoint) return connection;
    this.drop();
    const browser = await this.connect(this.engine, connection.cdpEndpoint);
    // Only the default context is the window the viewer sees. A context of our own would open a
    // second window over it (#236).
    const context = browser.contexts()[0];
    if (!context) {
      browser.close().catch(() => undefined);
      throw new Error('browser_context_unavailable');
    }
    browser.on('disconnected', () => { if (this.browser === browser) this.drop(); });
    this.browser = browser;
    this.endpoint = connection.cdpEndpoint;
    this.context = context;
    this.page = context.pages().at(-1) ?? await context.newPage();
    this.#watch(this.page);
    return connection;
  }

  // Forgets the connection so the next call connects again. Closing a CDP connection only
  // disconnects; Chromium keeps running. Not awaited: on a dead socket it may never settle.
  drop() {
    const browser = this.browser;
    this.browser = null;
    this.endpoint = null;
    this.context = null;
    this.page = null;
    this.refs.advanceRevision();
    browser?.close().catch(() => undefined);
  }

  // After a failed action: is the browser still answering? If not, the connection is dropped and
  // the caller learns that the browser, not its action, was the problem.
  async recover() {
    const browser = this.browser;
    if (!browser) return false;
    let timer;
    try {
      await Promise.race([
        (async () => {
          const session = await browser.newBrowserCDPSession();
          try { await session.send('Browser.getVersion'); }
          finally { await session.detach().catch(() => undefined); }
        })(),
        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('probe timed out')), this.probeTimeoutMs); })
      ]);
      return false;
    } catch {
      if (this.browser === browser) this.drop();
      return true;
    } finally {
      clearTimeout(timer);
    }
  }

  status() { return this.backend.status(); }

  async stop() {
    this.drop();
    return this.backend.stop();
  }

  async navigate(url) {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('invalid_browser_url');
    const page = await this.#activePage();
    await page.goto(parsed.toString(), { waitUntil: 'domcontentloaded', timeout: 30_000 });
    return { url: page.url(), title: await page.title() };
  }

  async snapshot() {
    return accessibilitySnapshot(await this.#activePage(), this.refs);
  }

  async click(ref) {
    const locator = this.refs.resolve(ref);
    const page = await this.#activePage();
    await locator.click({ timeout: 10_000 });
    return { clicked: ref, url: page.url() };
  }

  async type(ref, value, submit = false) {
    const locator = this.refs.resolve(ref);
    await this.#activePage();
    await locator.fill(value, { timeout: 10_000 });
    if (submit) await locator.press('Enter');
    return { typed: ref, submitted: submit };
  }

  async screenshot(format = 'png', fullPage = false) {
    const data = await (await this.#activePage()).screenshot({
      type: format, fullPage, timeout: 20_000
    });
    return { data: data.toString('base64'), mimeType: `image/${format === 'jpeg' ? 'jpeg' : 'png'}` };
  }

  async tabs(action, index, url) {
    await this.#activePage();
    if (action === 'select') {
      const pages = this.context.pages();
      if (!Number.isInteger(index) || !pages[index]) throw new Error('invalid_tab_index');
      this.page = pages[index];
      await this.page.bringToFront();
      this.refs.advanceRevision();
    } else if (action === 'new') {
      this.page = await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
      if (url) await this.navigate(url);
    } else if (action === 'close') {
      const pages = this.context.pages();
      if (!Number.isInteger(index) || !pages[index]) throw new Error('invalid_tab_index');
      await pages[index].close();
      this.page = this.context.pages().at(-1) ?? await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
    }
    const pages = this.context.pages();
    return Promise.all(pages.map(async (page, tabIndex) => ({
      index: tabIndex, active: page === this.page, title: await page.title(), url: page.url()
    })));
  }

  async #activePage() {
    if (!this.browser?.isConnected()) await this.start();
    if (this.page?.isClosed()) {
      this.page = this.context.pages().at(-1) ?? await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
    }
    // Chromium stops animation frames in a background tab, and Playwright waits on them to see
    // an element hold still, so every click there ran into its timeout (#237). A popup, a
    // target=_blank link or the viewer switching tabs puts the agent's page there. In front, it
    // is also the tab the viewer sees the agent working in.
    await this.page.bringToFront();
    return this.page;
  }

  #watch(page) {
    if (page.__agenthubWatched) return;
    Object.defineProperty(page, '__agenthubWatched', { value: true });
    page.on('framenavigated', frame => {
      if (frame === page.mainFrame()) this.refs.advanceRevision();
    });
  }
}
