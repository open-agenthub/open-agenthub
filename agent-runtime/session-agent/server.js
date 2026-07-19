'use strict';

const fs = require('node:fs');
const path = require('node:path');

const installedServer = path.join(__dirname, 'common', 'server.js');
const sourceServer = path.join(__dirname, '..', 'common', 'server.js');
const { startFromEnvironment } = require(fs.existsSync(installedServer) ? installedServer : sourceServer);

startFromEnvironment();
