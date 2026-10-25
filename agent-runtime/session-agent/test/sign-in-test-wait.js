'use strict';

// xterm writes settle on its own timer, so one setImmediate is not an export barrier.
async function waitForExport(ready) {
  const end = Date.now() + 5000;
  while (!ready()) {
    if (Date.now() >= end) throw new Error('terminal export did not settle');
    await new Promise(resolve => setTimeout(resolve, 1));
  }
}
module.exports = { waitForExport };
