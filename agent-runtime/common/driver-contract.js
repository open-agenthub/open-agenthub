'use strict';

const path = require('node:path');

const REQUIRED = ['name', 'stateDir', 'authFilename', 'buildCommand', 'isMissingResume', 'prepare'];

function validateDriver(driver) {
  if (!driver || typeof driver !== 'object') throw new Error('Agent driver must export an object');
  for (const key of REQUIRED) {
    if (!driver[key]) throw new Error('Agent driver missing ' + key);
  }
  for (const key of ['buildCommand', 'isMissingResume', 'prepare']) {
    if (typeof driver[key] !== 'function') throw new Error('Agent driver ' + key + ' must be a function');
  }
  return driver;
}

function loadDriver(driverPath) {
  if (!driverPath) throw new Error('AGENTHUB_DRIVER is required');
  const resolved = path.isAbsolute(driverPath) ? driverPath : path.resolve(driverPath);
  return validateDriver(require(resolved));
}

module.exports = { loadDriver, validateDriver };
