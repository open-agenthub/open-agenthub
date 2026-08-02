'use strict';

const path = require('node:path');

const REQUIRED = ['name', 'stateDir', 'authFilename', 'attachmentCapabilities', 'buildCommand', 'isResumeCommand',
  'isMissingResume', 'prepare'];
const SAFE_RELATIVE_NAME = /^(?!\.{1,2}$)[A-Za-z0-9._][A-Za-z0-9._-]*$/;
// Optional tar --exclude paths under HOME: relative, no .., may include * globs.
const SAFE_STATE_EXCLUDE = /^(?!\/)(?!.*(?:^|\/)\.\.(?:\/|$))[A-Za-z0-9._*][A-Za-z0-9._*/-]*$/;

// prepare(env) may scrub values from the long-lived parent environment and return
// { childEnv: { ... } } with values merged only into the provider agent PTY. The
// common shell terminal always receives the scrubbed parent environment.

function isSafeStateExclude(value) {
  return typeof value === 'string' && SAFE_STATE_EXCLUDE.test(value) &&
    !value.includes('\\') && !/(^|\/)\.\.?(\/|$)/.test(value);
}

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
  if (driver.stateExcludes !== undefined) {
    if (!Array.isArray(driver.stateExcludes)) {
      throw new Error('Agent driver stateExcludes must be an array of safe relative paths');
    }
    for (const entry of driver.stateExcludes) {
      if (!isSafeStateExclude(entry)) {
        throw new Error('Agent driver stateExcludes entries must be safe relative paths');
      }
    }
  }
  const capabilities = driver.attachmentCapabilities;
  const capabilityNames = ['localImagePaths', 'mcpImages', 'nativeImages'];
  if (!capabilities || typeof capabilities !== 'object' || Array.isArray(capabilities) ||
      Object.keys(capabilities).sort().join(',') !== capabilityNames.join(',')) {
    throw new Error('Agent driver attachmentCapabilities must define exactly nativeImages, localImagePaths, and mcpImages');
  }
  for (const key of capabilityNames) {
    if (typeof capabilities[key] !== 'boolean') {
      throw new Error('Agent driver attachmentCapabilities values must be booleans');
    }
  }
  return driver;
}

function loadDriver(driverPath) {
  if (!driverPath) throw new Error('AGENTHUB_DRIVER is required');
  const resolved = path.isAbsolute(driverPath) ? driverPath : path.resolve(driverPath);
  return validateDriver(require(resolved));
}

module.exports = { loadDriver, validateDriver, isSafeStateExclude };
