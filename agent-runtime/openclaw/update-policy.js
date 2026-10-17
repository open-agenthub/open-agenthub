'use strict';

const fs = require('node:fs');

function disableUpdates(configPath) {
  const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  // State restore can bring back an enabled updater. Reapply image ownership each start so
  // neither gateway update hints nor its automatic installer interrupt a managed session.
  config.update = { ...config.update, checkOnStart: false,
    auto: { ...config.update?.auto, enabled: false } };
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n', { mode: 0o600 });
}

if (require.main === module) disableUpdates(process.argv[2]);
module.exports = { disableUpdates };
