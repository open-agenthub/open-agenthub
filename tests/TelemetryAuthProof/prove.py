#!/usr/bin/env python3
"""Exercise real CLI OTLP exports against the UsageProof host and its Postgres stores."""
import argparse
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request

p = argparse.ArgumentParser()
p.add_argument('--hub', default='http://127.0.0.1:8080')
p.add_argument('--model', default='http://127.0.0.1:18780')
p.add_argument('--cli', required=True)
p.add_argument('--out', required=True)
a = p.parse_args()
out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)


def request(path, body=None, token=None):
    headers = {'Content-Type': 'application/x-protobuf'}
    if isinstance(body, dict):
        body = json.dumps(body).encode()
        headers['Content-Type'] = 'application/json'
    if token is not None:
        headers['X-Agent-Token'] = token
    req = urllib.request.Request(a.hub + path, data=body, headers=headers)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


for sid, token in [('proof-own', 'proof-own-token'), ('proof-alt', 'proof-alt-token')]:
    assert request('/__seed', dict(id=sid, token=token, owner='alice', title=sid,
                                  agent=0, authMode=2, mode=0))[0] == 200
captured = []


class Capture(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers['Content-Length']))
        # Keep only a boolean about the credential, never log its value.
        captured.append((body, self.headers.get('X-Agent-Token') == 'proof-own-token'))
        status, response = request('/internal/otel/v1/metrics', body,
                                   self.headers.get('X-Agent-Token'))
        self.send_response(status)
        self.send_header('Content-Type', 'application/x-protobuf')
        self.end_headers()
        self.wfile.write(response)

    def log_message(self, *_):
        pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Capture)
threading.Thread(target=server.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory(prefix='telemetry-auth-') as home:
    Path(home, '.claude.json').write_text('{"hasCompletedOnboarding":true}')
    env = dict(PATH=os.environ['PATH'], HOME=home, ANTHROPIC_API_KEY='proof-key',
               ANTHROPIC_BASE_URL=a.model, CLAUDE_CODE_ENABLE_TELEMETRY='1',
               OTEL_METRICS_EXPORTER='otlp', OTEL_TRACES_EXPORTER='none', OTEL_LOGS_EXPORTER='none',
               OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf',
               OTEL_EXPORTER_OTLP_ENDPOINT=f'http://127.0.0.1:{server.server_port}',
               OTEL_EXPORTER_OTLP_HEADERS='X-Agent-Token=proof-own-token',
               OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE='delta',
               OTEL_METRIC_EXPORT_INTERVAL='1000',
               OTEL_RESOURCE_ATTRIBUTES='agenthub.session_id=proof-own,agenthub.owner=alice')
    run = subprocess.run([a.cli, '-p', 'say hi', '--model', 'claude-sonnet-4-5'],
                         env=env, cwd=home, stdin=subprocess.DEVNULL,
                         capture_output=True, timeout=90)
    assert run.returncode == 0, run.stderr.decode()
server.shutdown()
assert captured and all(ok for _, ok in captured), 'CLI must send the configured credential'
status, body = request('/api/usage/sessions/proof-own')
assert status == 200, (status, body)
before = json.loads(body)
assert before['inputTokens'] > 0, before
# Reuse actual protobuf bytes: equal-length IDs preserve every encoded field length.
payload = b''.join(body for body, _ in captured)
assert b'proof-own' in payload
other = payload.replace(b'proof-own', b'proof-alt')
results = {}
for name, batch, token, expected in [
    ('missing', payload, None, 401),
    ('invalid', payload, 'unknown', 401),
    ('different-session', other, 'proof-own-token', 400),
    ('mixed-batch', payload + other, 'proof-own-token', 400),
]:
    status, _ = request('/internal/otel/v1/metrics', batch, token)
    assert status == expected, (name, status)
    results[name] = status
assert request('/api/usage/sessions/proof-alt')[0] == 404
assert json.loads(request('/api/usage/sessions/proof-own')[1]) == before
assert request('/internal/otel/v1/metrics', payload, 'proof-own-token')[0] == 200
updated = json.loads(request('/api/usage/sessions/proof-own')[1])
assert updated['inputTokens'] == before['inputTokens'] * 2
results.update(exports=len(captured), credentialPresent=True, initialInput=before['inputTokens'],
               replayInput=updated['inputTokens'], otherSessionUntouched=True)
(out / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps(results))
