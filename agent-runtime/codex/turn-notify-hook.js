#!/usr/bin/env node
'use strict';

// Same arrangement as skill-reminder-hook.js: Codex's managed hooks all have to live under
// managed_dir, and project-requirements.js rewrites exactly this directory's path when the
// runtime root is the alternative one. So requirements.toml names a command here, and the
// shared implementation (the one Cursor runs directly) is reached from it.

const path = require('node:path');
const { pathToFileURL } = require('node:url');

const shared = pathToFileURL(path.join(__dirname, '..', 'common', 'turn-notify-hook.mjs')).href;

import(shared)
  .then(module => module.cli())
  // A hook that fails must never be the reason a turn breaks.
  .catch(() => {});
