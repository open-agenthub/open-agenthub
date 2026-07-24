export class SnapshotRefs {
  #revision = 1;
  #counter = 0;
  #entries = new Map();

  get revision() { return this.#revision; }

  remember(locator) {
    const ref = `e${this.#revision}-${++this.#counter}`;
    this.#entries.set(ref, { revision: this.#revision, locator });
    return ref;
  }

  resolve(ref) {
    const entry = this.#entries.get(ref);
    if (!entry || entry.revision !== this.#revision)
      throw new Error('stale_browser_reference');
    return entry.locator;
  }

  advanceRevision() {
    this.#revision += 1;
    this.#counter = 0;
    this.#entries.clear();
    return this.#revision;
  }
}

export async function accessibilitySnapshot(page, refs) {
  const title = await page.title();
  const url = page.url();
  const candidates = page.locator('a,button,input,textarea,select,[role],[contenteditable="true"]');
  const count = Math.min(await candidates.count(), 100);
  const elements = [];
  for (let index = 0; index < count; index += 1) {
    const locator = candidates.nth(index);
    if (!await locator.isVisible().catch(() => false)) continue;
    const ref = refs.remember(locator);
    const role = await locator.getAttribute('role') || await locator.evaluate(element =>
      element.tagName.toLowerCase());
    const name = (await locator.getAttribute('aria-label')) ||
      (await locator.getAttribute('placeholder')) ||
      (await locator.innerText().catch(() => '')) ||
      (await locator.getAttribute('name')) || '';
    elements.push({ ref, role, name: name.trim().slice(0, 300) });
  }
  const text = (await page.locator('body').innerText({ timeout: 10_000 }).catch(() => ''))
    .replace(/\n{3,}/g, '\n\n').slice(0, 12_000);
  return { revision: refs.revision, title, url, elements, text };
}