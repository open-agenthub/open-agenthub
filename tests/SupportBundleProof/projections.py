#!/usr/bin/env python3
"""Adversarial checks complement the real cluster proof; no external packages."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from check import check

spec = importlib.util.spec_from_file_location('collector', Path(__file__).parents[2] / 'scripts/support-bundle.py')
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class ProjectionProof(unittest.TestCase):
    def test_unknown_content_and_wrong_types_drop(self):
        canary = 'private-prompt-with-short-secret'
        self.assertEqual(collector.values_projection({'backend': {'replicas': canary}, 'newField': canary}), {})
        self.assertEqual(collector.values_projection({'backend': {'replicas': 2}, 'newField': canary}), {'backend.replicas': 2})
        self.assertEqual(collector.logs_projection([canary, '    ' + canary, 'token=' + canary,
                         'Application started. Press Ctrl+C to shut down. ' + canary]), [])
        self.assertEqual(collector.logs_projection(list(collector.LOG_LINES)), list(collector.LOG_LINES))
        self.assertEqual(collector.events_projection({'items': [{'reason': canary, 'message': canary}]}, ''), {})
        self.assertEqual(collector.image_tag('https://user:password@example.com/runtime:arbitrary-secret'), 'omitted')

    def test_canary_checker_rejects_a_new_leak(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'leaking.json'
            path.write_text(json.dumps({'newField': 'new-unknown-secret-canary'}))
            with self.assertRaisesRegex(AssertionError, 'canary leaked'):
                check(path, ['new-unknown-secret-canary'])


unittest.main()
