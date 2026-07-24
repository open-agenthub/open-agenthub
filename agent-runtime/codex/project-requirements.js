'use strict';

const fs = require('node:fs');

const SOURCE_RUNTIME = '/opt/session-agent/codex';
const SUPPORTED_RUNTIMES = new Set([
  SOURCE_RUNTIME,
  '/opt/agenthub/session-agent/codex'
]);

function projectRequirements(source, destination, managedRuntime) {
  if (!SUPPORTED_RUNTIMES.has(managedRuntime)) {
    throw new Error(`Unsupported Codex managed runtime: ${managedRuntime}`);
  }

  const requirements = fs.readFileSync(source, 'utf8');
  if (!requirements.includes(SOURCE_RUNTIME)) {
    throw new Error('Codex system requirements do not contain the managed runtime marker.');
  }

  const projected = requirements.replaceAll(SOURCE_RUNTIME, managedRuntime);
  fs.writeFileSync(destination, projected, { encoding: 'utf8', mode: 0o444 });
  fs.chmodSync(destination, 0o444);
}

if (require.main === module) {
  const [source, destination, managedRuntime] = process.argv.slice(2);
  if (!source || !destination || !managedRuntime) {
    process.stderr.write('Usage: project-requirements <source> <destination> <managed-runtime>\n');
    process.exitCode = 2;
  } else {
    projectRequirements(source, destination, managedRuntime);
  }
}

module.exports = { projectRequirements };
