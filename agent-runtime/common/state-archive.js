'use strict';

// The shell commands of the periodic state archive (docs/state-archive.md), kept apart from the
// server so a test can run them against a real directory and the entrypoint's restore command.
//
// Each is returned as the argument list for /bin/sh. The home directory and the upload url come
// from the environment and reach the script as positional parameters, never as part of its
// text: a quote or `$(` in either would otherwise be executed. What is spliced into the text
// is the driver's own state directory and excludes, which driver-contract.js restricts to safe
// relative paths.

// Upper bound for each archive/upload step of a persistence run.
const PERSIST_STEP_SECONDS = 120;

function stateExcludes(driver) {
  const excludes = [driver.stateDir + '/' + driver.authFilename];
  if (Array.isArray(driver.stateExcludes)) {
    for (const entry of driver.stateExcludes) excludes.push(entry);
  }
  return excludes;
}

// A digest over the name, type, size, mtime and ctime of everything the archive would contain,
// without opening a single file. The timestamps are printed at the filesystem's full
// resolution: tar's own verbose listing rounds to the second, and a file rewritten to the same
// size within the second of the previous run — an SQLite database does exactly that — would
// then look unchanged for good. find anchors a pattern that tar matches anywhere in the path,
// so find can only ever see more files than tar archives: an extra archive, never a missed one.
// The exit status is part of the digest so an unreadable subtree cannot pass for a quiet one.
function signatureCommand({ home, driver }) {
  const prune = stateExcludes(driver).map(entry => '-path "' + entry + '"').join(' -o ');
  return ['-c', 'cd "$1" && { timeout ' + PERSIST_STEP_SECONDS + ' find "' + driver.stateDir +
    '" \\( ' + prune + " \\) -prune -o -printf '%T@ %C@ %s %y %p\\n'; echo \"exit $?\"; } 2>&1 | " +
    'LC_ALL=C sort | sha256sum', 'sh', home];
}

// tar exits 1 when a file changed while it was read — the live transcript of a running
// agent always does — and the archive is still complete, so only >1 counts as a failure;
// treating 1 as one meant an active session never uploaded its state at all. It writes
// to a side file so an aborted run never replaces the last good archive, runs niced
// and bounded so a large state directory cannot starve the agent and its hooks.
// gzip runs at level 1 through -I rather than a pipe, which would hide tar's exit status:
// about half the CPU of the default level for a tenth more bytes, and still plain gzip, so
// `tar xzf` in an older runtime's entrypoint restores it and this one restores an older archive.
function archiveCommand({ home, driver, statePut, curlOption = '', archivePath = '/tmp/state.tgz' }) {
  const excludeArgs = stateExcludes(driver).map(entry => '--exclude="' + entry + '"').join(' ');
  return ['-c', 'nice -n 10 timeout ' + PERSIST_STEP_SECONDS +
    " tar -I 'gzip -1' -cf " + archivePath + '.part -C "$1" ' + excludeArgs + ' "' +
    driver.stateDir + '" 2>/dev/null; ' +
    'if [ $? -le 1 ]; then mv -f ' + archivePath + '.part ' + archivePath +
    ' && curl -fsS --max-time ' + PERSIST_STEP_SECONDS + ' ' + curlOption + '-T ' + archivePath +
    ' "$2"; else rm -f ' + archivePath + '.part; exit 1; fi', 'sh', home, statePut];
}

module.exports = { PERSIST_STEP_SECONDS, stateExcludes, signatureCommand, archiveCommand };
