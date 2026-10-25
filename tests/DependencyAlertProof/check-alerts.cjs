// Check the actual merge-preview resolutions against live ranges: a clean audit alone
// can miss advisories that have not yet reached the package manager's database.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const semver = require(path.join(root, 'frontend/node_modules/semver'));
const alerts = JSON.parse(execFileSync('gh', ['api', 'repos/open-agenthub/open-agenthub/dependabot/alerts', '--paginate'], { encoding: 'utf8' })).filter(a => a.state === 'open');
let remaining = 0;
for (const a of alerts) {
  const manifest = a.dependency.manifest_path;
  const name = a.dependency.package.name;
  const source = process.env.ALERT_GIT_REF
    ? execFileSync('git', ['show', `${process.env.ALERT_GIT_REF}:${manifest}`], { cwd: root, encoding: 'utf8' })
    : fs.readFileSync(path.join(root, manifest), 'utf8');
  let versions;
  if (manifest.endsWith('.json')) {
    const lock = JSON.parse(source);
    versions = Object.entries(lock.packages).filter(([p]) => p.endsWith('node_modules/' + name)).map(([, p]) => p.version);
  } else {
    versions = [source.match(new RegExp('Include="' + name.replaceAll('.', '\\.') + '" Version="([^"]+)"'))?.[1]].filter(Boolean);
    if (!versions.length) throw new Error(`No direct reference found for ${name}`);
  }
  const vulnerable = versions.filter(v => semver.satisfies(v, a.security_vulnerability.vulnerable_version_range.replaceAll(',', '')));
  remaining += vulnerable.length;
  console.log(JSON.stringify({ alert: a.number, manifest, package: name, versions, vulnerable }));
}
console.log(JSON.stringify({ openAlerts: alerts.length, remainingVulnerableResolutions: remaining }));
if (remaining) process.exitCode = 1;
