#!/usr/bin/env python3
"""Audit real production schema callers and the collector against the shared registry."""
from pathlib import Path
import ast
import re

root = Path(__file__).resolve().parents[2]
source = (root / 'backend/Persistence/SchemaScript.cs').read_text()
registered = set(re.findall(r'"(\w+)"', source.split('DiagnosticNames =')[1].split('};')[0]))
callers = set()
for directory in ('backend', 'ee/backend'):
    for path in (root / directory).rglob('*.cs'):
        text = path.read_text()
        calls = re.findall(r'SchemaScript\.ApplyAsync\([^,]+,\s*(?:nameof\((\w+)\)|"([^"]+)")', text)
        callers.update(a or b for a, b in calls)
        assert len(calls) == text.count('SchemaScript.ApplyAsync('), 'unrecognised schema caller: ' + str(path)
assert callers <= registered, 'schema names missing from shared registry: ' + repr(callers - registered)
module = ast.parse((root / 'scripts/support-bundle.py').read_text())
assignment = next(n for n in module.body if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'SCHEMA_NAMES' for t in n.targets))
client = set(ast.literal_eval(assignment.value.args[0]))
assert client == registered, 'collector schema projection differs from shared registry'
print('PASS: all', len(callers), 'production schema names registered; client matches shared registry')
