#!/usr/bin/env node
'use strict';

// Same arrangement as turn-notify-hook.js: Codex's managed hooks have to live under managed_dir,
// so requirements.toml names this file and the shared implementation is reached from it.

const path = require('node:path');
const { pathToFileURL } = require('node:url');

const shared = pathToFileURL(path.join(__dirname, '..', 'common', 'fleet-turn-hook.mjs')).href;

import(shared)
  .then(module => module.cli())
  // A hook that fails must never be the reason a turn breaks.
  .catch(() => {});
