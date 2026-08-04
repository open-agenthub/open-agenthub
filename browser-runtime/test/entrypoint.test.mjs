import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { access, chmod, mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimeDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('entrypoint waits for Xvfb and enables xrandr resize for both VNC servers', async t => {
  try {
    await access('/bin/sh');
  } catch {
    t.skip('/bin/sh is required');
    return;
  }

  const directory = await mkdtemp(path.join(tmpdir(), 'agenthub-browser-entrypoint-'));
  const binDirectory = path.join(directory, 'bin');
  const socketDirectory = path.join(directory, '.X11-unix');
  const eventsFile = path.join(directory, 'events.log');
  let child;

  try {
    await mkdir(binDirectory);
    await writeExecutable(path.join(binDirectory, 'Xvfb'), `#!/bin/sh
if [ ! -d "$AGENTHUB_BROWSER_X11_SOCKET_DIR" ]; then
  echo "Xvfb-missing-socket-directory" >> "$AGENTHUB_TEST_EVENTS"
  exit 41
fi
sleep 0.25
: > "$AGENTHUB_BROWSER_X11_SOCKET_DIR/X99"
echo "Xvfb-ready" >> "$AGENTHUB_TEST_EVENTS"
trap 'exit 0' TERM INT
while :; do sleep 1; done
`);

    const dependentStub = `#!/bin/sh
name="$(basename "$0")"
if [ ! -e "$AGENTHUB_BROWSER_X11_SOCKET_DIR/X99" ]; then
  echo "$name-early" >> "$AGENTHUB_TEST_EVENTS"
  exit 42
fi
echo "$name" >> "$AGENTHUB_TEST_EVENTS"
if [ "$name" = "x11vnc" ]; then echo "x11vnc-args:$*" >> "$AGENTHUB_TEST_EVENTS"; fi
trap 'exit 0' TERM INT
while :; do sleep 1; done
`;
    for (const name of ['chromium', 'x11vnc', 'websockify', 'socat', 'node']) {
      await writeExecutable(path.join(binDirectory, name), dependentStub);
    }

    child = spawn('/bin/sh', [path.join(runtimeDirectory, 'entrypoint.sh')], {
      env: {
        ...process.env,
        PATH: `${binDirectory}:${process.env.PATH}`,
        AGENTHUB_BROWSER_X11_SOCKET_DIR: socketDirectory,
        AGENTHUB_BROWSER_X11_READY_ATTEMPTS: '50',
        // The real /data only exists inside the image, and creating it needs root.
        AGENTHUB_BROWSER_DATA_DIR: path.join(directory, 'data'),
        AGENTHUB_TEST_EVENTS: eventsFile,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    await waitFor(async () => {
      const events = await readEvents(eventsFile);
      return events.some(event => event === 'node' || event === 'node-early') || child.exitCode !== null;
    }, 5_000);

    // A script that already died (say, because a directory could not be created) has
    // fired its exit event long before we could listen for it — waiting for another one
    // would just hang until the timeout and report the wrong problem.
    assert.equal(child.exitCode, null, `entrypoint exited early with status ${child.exitCode}`);

    child.kill('SIGTERM');
    // Generous on purpose. This asserts that shutdown happens and in what order, not how
    // fast: the entrypoint polls on one-second sleeps and deliberately gives the
    // supervisor time to drain, so a tight budget just makes the test flaky under load.
    await Promise.race([
      new Promise(resolve => child.once('exit', resolve)),
      new Promise((_, reject) => setTimeout(() => reject(new Error('entrypoint did not stop')), 30_000)),
    ]);

    const events = await readEvents(eventsFile);
    assert.ok(events.includes('Xvfb-ready'), `Xvfb never became ready: ${events.join(', ')}`);
    assert.ok(!events.some(event => event.endsWith('-early')), `display consumer started early: ${events.join(', ')}`);

    const vncArguments = events.filter(event => event.startsWith('x11vnc-args:'));
    assert.equal(vncArguments.length, 2, `Expected writable and view-only x11vnc processes: ${events.join(', ')}`);
    assert.ok(vncArguments.every(event => event.includes('-xrandr resize')),
      `x11vnc processes do not track XRandR resizes: ${vncArguments.join(', ')}`);

    const readyIndex = events.indexOf('Xvfb-ready');
    for (const name of ['chromium', 'x11vnc', 'websockify', 'socat', 'node']) {
      const index = events.indexOf(name);
      assert.ok(index > readyIndex, `${name} started before Xvfb: ${events.join(', ')}`);
    }
  } finally {
    if (child?.exitCode === null) child.kill('SIGKILL');
    await rm(directory, { recursive: true, force: true });
  }
});

async function writeExecutable(filename, contents) {
  await writeFile(filename, contents);
  await chmod(filename, 0o755);
}

async function readEvents(filename) {
  try {
    return (await readFile(filename, 'utf8')).trim().split(/\r?\n/).filter(Boolean);
  } catch (error) {
    if (error.code === 'ENOENT') return [];
    throw error;
  }
}

async function waitFor(predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error('timed out waiting for entrypoint events');
}
