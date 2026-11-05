#!/usr/bin/env python3
"""Check the pinned source inventory without installing or executing upstream dependencies."""
import argparse
import json
import re
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('upstream', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
doc = root / 'docs/design/t3code-adoption.md'
text = doc.read_text()
sha = '25b53299b75d4d8d1d0667ad47e9cfed3749776e'
actual = subprocess.check_output(['git', '-C', str(args.upstream), 'rev-parse', 'HEAD'], text=True).strip()
assert actual == sha, (actual, sha)
registry = (args.upstream / 'apps/server/src/provider/builtInDrivers.ts').read_text()
array = re.search(r'export const BUILT_IN_DRIVERS:.*?= \[(.*?)\];', registry, re.S).group(1)
drivers = re.findall(r'\b(\w+Driver)\b', array)
assert len(drivers) == 11, drivers
assert len(set(drivers)) == 11
imports = dict((symbol, source) for symbol, source in re.findall(
    r'import\s*\{\s*(\w+Driver),.*?from\s*"([^"]+)"', registry, re.S))
assert all(driver in imports for driver in drivers)
# Ordinal links preserve identification without duplicating provider branding in prose.
for ordinal in range(1, 12):
    assert f'| {ordinal}' in text
prefix = f'https://github.com/pingdotgg/t3code/tree/{sha}/'
upstream_paths = re.findall(re.escape(prefix) + r'([^\s)]+)', text)
for path in upstream_paths:
    assert (args.upstream / path).exists(), path
local_paths = re.findall(r'\]\((\.\.?/[^)#]+)', text)
for path in local_paths:
    assert (doc.parent / path).resolve().exists(), path
license_text = (args.upstream / 'LICENSE').read_text()
assert license_text.startswith('MIT License')
notices = json.loads((args.upstream / 'third-party-licenses.config.json').read_text())
assert notices['customNotices'] and notices['packageOverrides']
custom_licenses = sorted({entry['license'] for entry in notices['customNotices']})
assert 'OFL-1.1' in custom_licenses and 'CC0-1.0' in custom_licenses
helper = (args.upstream / 'scripts/licenses/libatomic.LICENSE').read_text()
assert 'GPL-3.0-or-later WITH GCC-exception-3.1' in helper
print(json.dumps({'upstream': actual, 'drivers': len(drivers),
                  'upstream_references': len(upstream_paths),
                  'local_references': len(local_paths),
                  'custom_notices': len(notices['customNotices']),
                  'package_overrides': len(notices['packageOverrides']),
                  'custom_license_expressions': custom_licenses,
                  'scope': 'source evidence only; no provider execution'}, indent=2))
