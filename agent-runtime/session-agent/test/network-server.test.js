'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('agenthub_network registers port_request and port_list tools', () => {
  const server = fs.readFileSync(
    path.join(__dirname, '..', '..', 'network', 'server.mjs'),
    'utf8'
  );
  assert.match(server, /name:\s*'agenthub_network'/);
  for (const name of ['port_request', 'port_list']) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  // The request tool validates direction/port/protocol and requires a reason.
  assert.match(server, /z\.enum\(\['egress', 'browser_to_agent'\]\)/);
  assert.match(server, /port:\s*z\.number\(\)\.int\(\)\.min\(1\)\.max\(65535\)/);
  assert.match(server, /reason:\s*z\.string\(\)\.min\(1\)/);
  // It waits for the decision and expires the request when nobody answers.
  assert.match(server, /waitForDecision/);
  assert.match(server, /client\.expire/);
});
