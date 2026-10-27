'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFile } = require('node:child_process');

function dependencies(root, fsImpl = fs) {
  const archive = path.join(root, 'state.tgz');
  const map = value => value === '/tmp/state.tgz' ? archive : value;
  // The shipped server assumes one process per pod; fixtures share the live pod's /tmp.
  // Relocate its archive arguments and cleanup I/O while keeping the real commands and bytes.
  const fileMethods = new Set(['unlinkSync', 'readFileSync', 'writeFileSync', 'existsSync', 'statSync', 'lstatSync', 'openSync']);
  const isolatedFs = new Proxy(fsImpl, {
    get(target, property) {
      if (fileMethods.has(property)) return (file, ...args) => target[property](map(file), ...args);
      return target[property];
    }
  });
  return {
    fs: isolatedFs,
    execFile(file, args, ...rest) { return execFile(file, args.map(map), ...rest); }
  };
}
module.exports = { dependencies };
