'use strict';

// The shell commands of the periodic state archive (docs/state-archive.md), kept apart from the
// server so a test can run them against a real directory and the entrypoint's restore command.
//
// Nothing from the environment is part of a script's text. The home directory, the upload url
// and the archive path reach it as the positional parameters $1, $2 and $3: a quote or `$(` in
// one of them would otherwise be executed. What is spliced into the text is the driver's own
// state directory and excludes, which driver-contract.js restricts to safe relative paths.

// Upper bound for each archive/upload step of a persistence run.
const PERSIST_STEP_SECONDS = 120;

function stateExcludes(driver) {
  const excludes = [driver.stateDir + '/' + driver.authFilename];
  if (Array.isArray(driver.stateExcludes)) {
    for (const entry of driver.stateExcludes) excludes.push(entry);
  }
  // Atomic credential updates stage a sibling file before rename. Copies of excluded
  // files must stay excluded too, including wildcard paths for nested agent stores.
  const temporary = excludes.map(entry => {
    const file = entry.replace(/\/+$/, '');
    const slash = file.lastIndexOf('/');
    return file.slice(0, slash + 1) + '.' + file.slice(slash + 1) + '.*.tmp';
  });
  return [...new Set([...excludes, ...temporary])];
}

// A digest over the name, type, size, mtime and ctime of everything the archive would contain,
// without opening a single file. The timestamps are printed at the filesystem's full
// resolution: tar's own verbose listing rounds to the second, and a file rewritten to the same
// size within the second of the previous run — an SQLite database does exactly that — would
// then look unchanged for good. find anchors a pattern that tar matches anywhere in the path,
// so find can only ever see more files than tar archives: an extra archive, never a missed one.
// The exit status is part of the digest so an unreadable subtree cannot pass for a quiet one.
// Runs with the home directory as its working directory, which keeps that environment-derived
// path out of the shell's arguments altogether.
function signatureScript(driver) {
  const prune = stateExcludes(driver).map(entry => '-path "' + entry + '"').join(' -o ');
  return '{ timeout ' + PERSIST_STEP_SECONDS + ' find "' + driver.stateDir +
    '" \\( ' + prune + " \\) -prune -o -printf '%T@ %C@ %s %y %p\\n'; echo \"exit $?\"; } 2>&1 | " +
    'LC_ALL=C sort | sha256sum';
}

// tar exits 1 when a file changed while it was read — the live transcript of a running
// agent always does — and the archive is still complete, so only >1 counts as a failure;
// treating 1 as one meant an active session never uploaded its state at all. It writes
// to a side file so an aborted run never replaces the last good archive, runs niced
// and bounded so a large state directory cannot starve the agent and its hooks.
// gzip runs at level 1 through -I rather than a pipe, which would hide tar's exit status:
// about half the CPU of the default level for a tenth more bytes, and still plain gzip, so
// `tar xzf` in an older runtime's entrypoint restores it and this one restores an older archive.
// Runs in the home directory, like signatureScript; $1: where the archive is written. The
// upload is a separate step (uploadArgs): the url is refreshed from the hub while the pod runs
// (#111), and a value that arrives over the network is never handed to a shell.
function archiveScript(driver) {
  const excludeArgs = stateExcludes(driver).map(entry => '--exclude="' + entry + '"').join(' ');
  return 'nice -n 10 timeout ' + PERSIST_STEP_SECONDS +
    ' tar -I \'gzip -1\' -cf "$1.part" ' + excludeArgs + ' "' +
    driver.stateDir + '" 2>/dev/null; ' +
    'if [ $? -le 1 ]; then mv -f "$1.part" "$1"; else rm -f "$1.part"; exit 1; fi';
}

// curl's argument list for one presigned PUT, run without a shell.
function uploadArgs(file, url, insecure = false) {
  return ['-fsS', ...(insecure ? ['-k'] : []), '--max-time', String(PERSIST_STEP_SECONDS), '-T', file, url];
}

module.exports = { PERSIST_STEP_SECONDS, stateExcludes, signatureScript, archiveScript, uploadArgs };
