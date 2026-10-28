// Starts the REAL backend in one of the authentication modes of #324, with the rig's Postgres and
// kube-apiserver (README.md). The API key is read from a file so it never appears on a command
// line or in tool output. Exported for proof.mjs; also runnable on its own for browser.mjs:
//   node backend.mjs <AgentHub.Api.dll> <none|short|lowEntropy|apiKey|insecure|insecureTypo> [log file]
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

const RIG = process.env.RIG || process.env.HOME + '/rig'
export const KEY_FILE = process.env.PROOF_KEY_FILE || RIG + '/proof-key.txt'
export const HTTP = 'http://127.0.0.1:8080'
export const HTTPS = 'https://127.0.0.1:8443'

export function readKey() { return fs.readFileSync(KEY_FILE, 'utf8').trim() }

function authEnv(mode) {
  switch (mode) {
    case 'none': return {}
    case 'short': return { Auth__ApiKey: 'changeme' }
    case 'lowEntropy': return { Auth__ApiKey: 'changemechangemechangemechangeme' }
    case 'insecureTypo': return { Auth__InsecureDevMode: 'yes' }
    case 'insecure': return { Auth__InsecureDevMode: 'true' }
    case 'apiKey': return { Auth__ApiKey: readKey() }
    default: throw new Error(`unknown mode ${mode}`)
  }
}

export function startBackend(dll, mode, logFile, extraEnv = {}) {
  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    // https as well, because the remote MCP's OAuth server refuses plain http (it is part of what
    // has to keep working under the API key).
    Kestrel__Endpoints__Http__Url: HTTP,
    Kestrel__Endpoints__Https__Url: HTTPS,
    Kestrel__Endpoints__Https__Certificate__Path: RIG + '/tls/cert.pem',
    Kestrel__Endpoints__Https__Certificate__KeyPath: RIG + '/tls/key.pem',
    Mcp__PublicUrl: HTTPS,
    ConnectionStrings__Postgres: 'Host=127.0.0.1;Port=54329;Username=postgres;Database=proof324',
    AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: HTTP,
    // Not 7681: that is this session pod's own agent, which a loopback pod IP would reach.
    AgentHub__AgentPort: '7699',
    AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof',
    AgentHub__AgentImage: 'registry.example.com/agent:proof',
    Browser__Enabled: 'false',
    DOTNET_GCHeapHardLimit: '0x30000000',
    ...authEnv(mode),
    ...extraEnv
  }
  // Inherited from a shell that ran another mode; each run decides its own.
  for (const k of ['Auth__ApiKey', 'Auth__InsecureDevMode', 'Oidc__Authority'])
    if (!(k in authEnv(mode)) && !(k in extraEnv)) delete env[k]
  const out = logFile ? fs.openSync(logFile, 'w') : 'inherit'
  return spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', out, out] })
}

export async function waitHealthy(child, timeoutMs = 90000) {
  const end = Date.now() + timeoutMs
  while (Date.now() < end) {
    if (child.exitCode !== null) return false
    try { if ((await fetch(HTTP + '/healthz')).ok) return true } catch { }
    await new Promise(r => setTimeout(r, 500))
  }
  return false
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [dll, mode, log] = process.argv.slice(2)
  const child = startBackend(dll, mode, log)
  child.on('exit', code => process.exit(code ?? 1))
  for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s))
}
