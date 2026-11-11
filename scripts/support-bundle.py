#!/usr/bin/env python3
"""Collect a local, deliberately narrow support projection. Never uploads."""
import argparse
import collections
import datetime
import json
import os
import re
import subprocess
import sys
import urllib.request
import urllib.parse

COMPONENTS = ('agenthub-backend', 'agenthub-frontend', 'postgres', 'garage',
              'agenthub-artifact-renderer', 'agenthub-signal-cli')
PHASES = ('Pending', 'Running', 'Succeeded', 'Failed', 'Unknown')
REASONS = ('Scheduled', 'Pulled', 'Pulling', 'Created', 'Started', 'Killing',
           'BackOff', 'Failed', 'FailedScheduling', 'Unhealthy', 'FailedMount',
           'FailedAttachVolume', 'Evicted', 'OOMKilled', 'Completed',
           'CrashLoopBackOff', 'ImagePullBackOff', 'ErrImagePull', 'ContainerCreating')
# Only types with no customer text are copied from Helm values. Unknown keys are omitted.
BOOL_VALUES = ('ingress.enabled', 'postgres.enabled', 'objectStorage.enabled', 'oidc.requireHttpsMetadata',
               'agent.workspaceVolume.persistent', 'agent.allowCustomImage',
               'agent.allowRunAsRoot', 'mcp.enabled', 'spawnMcpEnabled',
               'networkPolicy.enabled', 'sharing.userSuggestionsEnabled')
INT_VALUES = ('backend.replicas', 'frontend.replicas', 'backend.database.maxPoolSize',
              'backend.database.lockPoolSize', 'maxRunningSessionsPerOwner')
# Mirrors SchemaScript.DiagnosticNames; schema-names.py prevents drift.
SCHEMA_NAMES = frozenset((
    'AllowedAgentsStore',
    'ApiTokenStore',
    'ChatBindingStore',
    'ChatLinkCodeStore',
    'EphemeralApiMcpStore',
    'LibraryShareStore',
    'LicenseStore',
    'McpClientStore',
    'McpServerStore',
    'McpSigningKeyStore',
    'OwnerHandoverStore',
    'PermissionStore',
    'PortGrantStore',
    'PostgresBrowserLeaseStore',
    'PostgresExecutionUsageSnapshots',
    'PostgresKeyRepository',
    'PostgresModelCatalogStore',
    'PostgresProjectStore',
    'PostgresProviderSwitchStore',
    'PostgresSecretRequestStore',
    'PostgresSessionExecutionStore',
    'PostgresSessionFileRegistry',
    'PostgresSessionMessageStore',
    'PostgresSessionOrderStore',
    'PostgresSessionStore',
    'PostgresSessionTransferTicketStore',
    'PostgresSessionWorkspaceStore',
    'PostgresUsageStore',
    'PostgresWebhookTriggerStore',
    'SessionShareStore',
    'SingleUserOwnerStore',
    'SkillEmbeddingStore',
    'SkillStore',
    'SlackThreadStore',
    'UsageLimitStore',
    'UserDirectory',
    'UserGroupStore',
    'UserRoleStore',
))
VERSION = re.compile(r'(?:v?\d+\.\d+\.\d+(?:-[0-9]+)?|[a-f0-9]{7,64})\Z')


def at(value, path):
    for key in path.split('.'):
        value = value.get(key) if isinstance(value, dict) else None
    return value


def version(value):
    return value if isinstance(value, str) and VERSION.fullmatch(value) else 'omitted'


def image_tag(value):
    # Repositories may embed customer hosts, credentials or arbitrary labels.
    return version(value.rsplit(':', 1)[-1]) if isinstance(value, str) else 'omitted'


def values_projection(raw):
    result = {}
    for key in BOOL_VALUES:
        value = at(raw, key)
        if type(value) is bool:
            result[key] = value
    for key in INT_VALUES:
        value = at(raw, key)
        if type(value) is int and 0 <= value <= 1000000:
            result[key] = value
    return result


def run(args, lines=False):
    # Child error output can contain credentials, URLs or server response bodies.
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            timeout=90, check=False, text=True)
    if result.returncode:
        raise RuntimeError('collection command failed; inspect your cluster locally')
    return result.stdout.splitlines() if lines else json.loads(result.stdout)


def pod_projection(raw):
    counts = collections.Counter()
    for item in raw.get('items', []):
        app = at(item, 'metadata.labels.app')
        if app not in COMPONENTS:
            app = 'other'
        status = item.get('status', {})
        phase = status.get('phase')
        phase = phase if phase in PHASES else 'Unknown'
        statuses = status.get('containerStatuses', [])
        restarts = sum(x.get('restartCount', 0) for x in statuses if type(x.get('restartCount')) is int)
        ready = sum(x.get('ready') is True for x in statuses)
        counts[(app, phase, restarts, ready)] += 1
    return [dict(component=k[0], phase=k[1], restarts=k[2], readyContainers=k[3], count=n)
            for k, n in sorted(counts.items())]


def events_projection(raw, since):
    counts = collections.Counter()
    for item in raw.get('items', []):
        # Messages and object names can include user data; only fixed reason categories leave.
        reason = item.get('reason')
        stamp = item.get('lastTimestamp') or item.get('eventTime') or at(item, 'metadata.creationTimestamp')
        if reason in REASONS and isinstance(stamp, str) and stamp >= since:
            counts[reason] += 1
    return dict(sorted(counts.items()))


# Log export is a second allow-list, not arbitrary text with sensitive words removed.
# Even short secrets and prompts are unsafe, so unknown messages and continuation lines drop.
LOG_LINES = ('Application started. Press Ctrl+C to shut down.',
             'Application is shutting down.', 'Hosting environment: Production',
             'Content root path: /app')
TOKEN_LIKE = re.compile(r'(?i)(bearer|token|secret|password|api.?key|eyJ|[A-Za-z0-9_+/=-]{24,})')


def logs_projection(lines):
    kept = []
    for line in lines:
        message = line.strip()
        if not TOKEN_LIKE.search(message) and message in LOG_LINES:
            kept.append(message)
    return kept


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def admin_projection(url, credential):
    parsed = urllib.parse.urlsplit(url)
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError('use an origin URL without credentials, query or fragment')
    if parsed.scheme != 'https' and not (parsed.scheme == 'http' and parsed.hostname in ('127.0.0.1', 'localhost', '::1')):
        raise ValueError('admin URL requires HTTPS or a loopback port-forward')
    request = urllib.request.Request(url.rstrip('/') + '/api/admin/diagnostics',
                                     headers={'Authorization': 'Bearer ' + credential})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=30) as response:
        raw = json.load(response)
    # The endpoint itself projects safe fields; never copy unknown response fields.
    result = {'license': {}, 'oidc': {}, 'schemaScripts': []}
    for key in ('valid', 'present'):
        value = at(raw, 'license.' + key)
        if type(value) is bool:
            result['license'][key] = value
    for key in ('seats', 'usedSeats'):
        value = at(raw, 'license.' + key)
        if type(value) is int and 0 <= value <= 1000000000:
            result['license'][key] = value
    edition = at(raw, 'license.edition')
    if edition in ('community', 'enterprise'):
        result['license']['edition'] = edition
    expiry = at(raw, 'license.expiresAt')
    if expiry is None or (isinstance(expiry, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT[0-9:.]+(?:Z|[+-]\d\d:\d\d)', expiry)):
        result['license']['expiresAt'] = expiry
    for key in ('authorityPresent', 'clientIdPresent', 'audiencePresent'):
        value = at(raw, 'oidc.' + key)
        if type(value) is bool:
            result['oidc'][key] = value
    # Names are compiled into the server allow-list, not taken from arbitrary SQL text.
    if raw.get('schemaScripts') is None:
        result['schemaScripts'] = None
        result['schemaReason'] = 'unavailable'
    for row in raw.get('schemaScripts') or []:
        name, date = row.get('name'), row.get('appliedAt')
        if name in SCHEMA_NAMES and isinstance(date, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT[0-9:.]+(?:Z|[+-]\d\d:\d\d)', date):
            result['schemaScripts'].append({'name': name, 'appliedAt': date})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--namespace', required=True)
    parser.add_argument('--release', default='agenthub')
    parser.add_argument('--admin-url', required=True, help='HTTPS origin or local backend port-forward')
    parser.add_argument('--output', required=True, help='new readable JSON file; never overwritten')
    parser.add_argument('--include-backend-logs', action='store_true')
    parser.add_argument('--log-lines', type=int, default=200)
    parser.add_argument('--event-minutes', type=int, default=60)
    args = parser.parse_args()
    if not 1 <= args.log_lines <= 10000 or not 1 <= args.event_minutes <= 1440:
        parser.error('log-lines must be 1..10000; event-minutes must be 1..1440')
    if not re.fullmatch(r'[a-z0-9][a-z0-9.-]{0,252}', args.namespace) or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,52}', args.release):
        parser.error('invalid namespace or release')
    credential = sys.stdin.readline().strip()
    if not credential:
        parser.error('supply an admin API key or OIDC access token on standard input')
    kube = ['kubectl', '--request-timeout=60s', '-n', args.namespace]
    values = run(['helm', 'get', 'values', args.release, '-n', args.namespace, '--all', '-o', 'json'])
    releases = run(['helm', 'list', '-n', args.namespace, '-o', 'json'])
    release = next(x for x in releases if x.get('name') == args.release)
    chart = release.get('chart', '').removeprefix('open-agenthub-')
    now = datetime.datetime.now(datetime.timezone.utc)
    since = (now - datetime.timedelta(minutes=args.event_minutes)).isoformat().replace('+00:00', 'Z')
    config = run(kube + ['get', 'configmap', 'agenthub-config', '-o', 'json']).get('data', {})
    deployment = run(kube + ['get', 'deployment', 'agenthub-backend', '-o', 'json'])
    containers = at(deployment, 'spec.template.spec.containers') or []
    bundle = dict(formatVersion=1, generatedAt=now.isoformat(),
                  notice='Local file only. Review before sharing. No automatic upload.',
                  chartVersion=version(chart), appVersion=version(release.get('app_version')),
                  backendImageTags=[image_tag(c.get('image')) for c in containers if c.get('name') == 'backend'],
                  runtimeImageTags={name: image_tag(config.get('AgentHub__' + name + 'AgentImage'))
                                    for name in ('Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode')},
                  helmValues=values_projection(values),
                  pods=pod_projection(run(kube + ['get', 'pods', '-o', 'json'])),
                  recentEventReasonCounts=events_projection(run(kube + ['get', 'events', '-o', 'json']), since),
                  eventWindowMinutes=args.event_minutes,
                  application=admin_projection(args.admin_url, credential))
    if args.include_backend_logs:
        warning = 'Logs can contain personal data and user-chosen names. Agree on sending logs with the provider beforehand (DPA). Review this file before sharing.'
        print(warning, file=sys.stderr)
        bundle['logsWarning'] = warning
        lines = run(kube + ['logs', 'deployment/agenthub-backend', '-c', 'backend',
                            '--tail=' + str(args.log_lines), '--all-pods=true', '--prefix=false'], lines=True)
        bundle['backendLogs'] = logs_projection(lines)
        bundle['backendLogLinesDropped'] = len(lines) - len(bundle['backendLogs'])
    # Exclusive creation and owner-only permissions prevent accidental replacement or disclosure.
    fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8') as output:
        json.dump(bundle, output, indent=2)
        output.write('\n')
    print('Support bundle saved locally. Review the JSON before sharing.', file=sys.stderr)


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Never print exception bodies: command/API errors can echo secret source material.
        print('Collection failed; no diagnostics were uploaded. Check arguments, permissions and local connectivity.', file=sys.stderr)
        sys.exit(1)
