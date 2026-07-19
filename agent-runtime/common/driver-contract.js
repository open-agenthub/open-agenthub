'use strict';

const path = require('node:path');

const REQUIRED = ['name', 'stateDir', 'authFilename', 'buildCommand', 'isResumeCommand',
  'isMissingResume', 'prepare'];
const SAFE_RELATIVE_NAME = /^(?!\.{1,2}$)[A-Za-z0-9._][A-Za-z0-9._-]*$/;

function validateDriver(driver) {
  if (!driver || typeof driver !== 'object') throw new Error('Agent driver must export an object');
  for (const key of REQUIRED) {
    const missing = key === 'stateDir' || key === 'authFilename' ? driver[key] == null : !driver[key];
    if (missing) {
      throw new Error('Agent driver missing ' + key);
    }
  }
  for (const key of ['buildCommand', 'isResumeCommand', 'isMissingResume', 'prepare']) {
    if (typeof driver[key] !== 'function') throw new Error('Agent driver ' + key + ' must be a function');
  }
  for (const key of ['stateDir', 'authFilename']) {
    if (typeof driver[key] !== 'string' || !SAFE_RELATIVE_NAME.test(driver[key])) {
      throw new Error('Agent driver ' + key + ' must be a safe single relative name');
    }
  }
  return driver;
}

function loadDriver(driverPath) {
  if (!driverPath) throw new Error('AGENTHUB_DRIVER is required');
  const resolved = path.isAbsolute(driverPath) ? driverPath : path.resolve(driverPath);
  return validateDriver(require(resolved));
}

module.exports = { loadDriver, validateDriver };
