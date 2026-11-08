#!/usr/bin/env python3
"""Fail closed if a supplied canary appears anywhere in a collected bundle."""
import argparse
import json
from pathlib import Path


def check(bundle, canaries):
    raw = Path(bundle).read_bytes()
    for value in canaries:
        if value and value.encode() in raw:
            raise AssertionError('sensitive canary leaked into bundle')
    data = json.loads(raw)
    assert data['formatVersion'] == 1
    assert data['application']['schemaScripts'], 'real migrations must be included'
    assert data['application']['license']['edition'] in ('community', 'enterprise')
    assert data['pods'], 'real component status must be included'
    return data


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('bundle')
    parser.add_argument('canary_file', help='local JSON array; never commit actual canaries')
    args = parser.parse_args()
    check(args.bundle, json.loads(Path(args.canary_file).read_text()))
    print('PASS: no supplied secret or user-content canary in the complete bundle')
