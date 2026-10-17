import { z } from 'zod';

// Helper containers of this session (docs/session-containers.md). Registered only when the hub
// set AGENTHUB_CONTAINERS_ENABLED=1, so an instance with the feature off shows no tools the
// backend would refuse anyway.

const name = z.string().min(1).max(40).regex(/^[a-z]([-a-z0-9]*[a-z0-9])?$/,
  'lowercase letters, digits and "-", starting with a letter');

export const runSchema = z.object({
  name,
  image: z.string().min(1).max(300),
  command: z.array(z.string().max(4096)).max(64).optional(),
  args: z.array(z.string().max(4096)).max(64).optional(),
  env: z.record(z.string().max(128), z.string().max(32_768)).optional(),
  ports: z.array(z.number().int().min(1).max(65535)).max(8).optional(),
  cpu: z.string().max(32).optional(),
  memory: z.string().max(32).optional(),
  volumes: z.array(z.object({
    mountPath: z.string().min(2).max(201),
    sizeLimit: z.string().max(32).optional()
  })).max(4).optional(),
  writableRootFilesystem: z.boolean().optional()
});

export function registerContainerTools(register, client) {
  register('container_run', {
    description: 'Start a helper container for this session — a database, a queue, a service under test. '
      + 'It runs in this session\'s own namespace with this session\'s privileges and no more: as uid 1000 '
      + '(root only if this session runs as root), no capabilities, a read-only root filesystem with a '
      + 'writable /tmp, no Kubernetes access. Images that must run as root, or as a fixed other user, fail '
      + 'in a non-root session; pick a variant that runs as any user. Mount writable data directories as '
      + '`volumes` (emptyDir, lost when the container stops) or set writableRootFilesystem. Every declared '
      + 'port is reachable from this session at `host` (name.namespace.svc) and from this session\'s other '
      + 'containers; nothing else can connect, and the container reaches only what this session reaches. '
      + 'It starts in phase Pending: poll container_list until ready is true, and read container_logs if '
      + 'it does not get there. All containers are deleted when the session is paused or deleted.',
    inputSchema: runSchema
  }, guarded(async body => client.containerRun(body)));

  register('container_list', {
    description: 'List this session\'s helper containers: name, image, phase, ready, restarts, reason '
      + '(e.g. ImagePullBackOff, CrashLoopBackOff), host and ports.',
    inputSchema: z.object({})
  }, guarded(async () => client.containerList()));

  register('container_logs', {
    description: 'The last lines of a helper container\'s output (tail, default 200, at most 2000; about '
      + '48 KiB). previous: true reads the run before the last restart — where a crash loop leaves its error.',
    inputSchema: z.object({
      name,
      tail: z.number().int().min(1).max(2000).optional(),
      previous: z.boolean().optional()
    })
  }, guarded(async ({ name: container, tail, previous }) => client.containerLogs(container, { tail, previous })));

  register('container_stop', {
    description: 'Stop and delete a helper container and its service. Its volumes are lost.',
    inputSchema: z.object({ name })
  }, guarded(async ({ name: container }) => client.containerStop(container)));
}

/**
 * The hub's reason for a refusal ("registry 'x' is not allowed", "exceeded quota", "violates
 * PodSecurity ...") is what lets the agent correct its request, so it goes back with the error
 * code instead of being reduced to sessions_backend_http_400 like the other tools' errors.
 */
function guarded(handler) {
  return async input => {
    try {
      return { content: [{ type: 'text', text: JSON.stringify(await handler(input)) }] };
    } catch (error) {
      const code = /sessions_backend_http_\d{3}/.exec(error?.message ?? '')?.[0] ?? 'containers_operation_failed';
      const result = { error: code, ...(error?.detail ? { message: error.detail } : {}) };
      return { content: [{ type: 'text', text: JSON.stringify(result) }], isError: true };
    }
  };
}
