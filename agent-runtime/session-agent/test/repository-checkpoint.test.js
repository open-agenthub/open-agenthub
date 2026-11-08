'use strict';

const test = require('node:test');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const execute = promisify(execFile);
const root = path.resolve(__dirname, '../../..');

// Real Git and HTTP keep publication/credential checks from passing against an agreeing mock.
// The packaged runtime is Linux-only; its required temporary volume is /tmp.
test('repository checkpoint uses real authenticated/read-only Git and archive-safe scratch', { skip: process.platform === 'win32' }, async () => {
  await execute(process.execPath, [path.join(root, 'tests/ProviderRepositoryProof/inspect.mjs')],
    { cwd: root, timeout: 30000, maxBuffer: 65536 });
});

test('proof cleanup reconciles actual HTTP connection loss around deletion', async () => {
  await execute(process.execPath, [path.join(root, 'tests/ProviderSwitchProof/delete-proof.mjs')],
    { cwd: root, timeout: 30000, maxBuffer: 65536 });
});
