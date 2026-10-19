// Starts the real backend (dev auth) with the rig's Postgres, S3 and kube-apiserver, reading the
// local synthetic S3 key from a file so it never appears in a command line or tool output.
import { spawn } from 'node:child_process'
import fs from 'node:fs'
// Run: node backend.mjs <AgentHub.Api.dll> ['{"AgentHub__MemoryLimitCeiling":"6Gi"}']
const [dll, extra = '{}'] = process.argv.slice(2)
const RIG = process.env.RIG || process.env.HOME + '/rig'
const key = fs.readFileSync(RIG + '/s3/key.txt', 'utf8')
const id = key.match(/Key ID:\s*(\S+)/)[1], secret = key.match(/Secret key:\s*(\S+)/)[1]
const env = {
  ...process.env,
  ASPNETCORE_URLS: 'http://127.0.0.1:8080', ASPNETCORE_ENVIRONMENT: 'Production',
  ConnectionStrings__Postgres: 'Host=127.0.0.1;Port=54329;Username=postgres;Database=agenthub215',
  AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: 'http://127.0.0.1:8080',
  AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof', AgentHub__CodexAgentImage: 'registry.example.com/agent-codex:proof',
  AgentHub__AgentImage: 'registry.example.com/agent:proof', AgentHub__PauseGracePeriodSeconds: '5',
  AgentHub__CpuLimitCeiling: '8', AgentHub__MemoryLimitCeiling: '16Gi',
  Browser__Enabled: 'false',
  S3__ServiceUrl: 'http://127.0.0.1:3910', S3__Region: 'local', S3__Bucket: 'agenthub-proof', S3__AccessKey: id, S3__SecretKey: secret,
  ...JSON.parse(extra)
}
const child = spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', 'inherit', 'inherit'] })
child.on('exit', code => process.exit(code ?? 1))
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s))
