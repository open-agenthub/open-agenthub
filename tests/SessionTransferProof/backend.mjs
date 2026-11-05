// Starts the real backend (dev auth) with the rig's Postgres, object storage and kube-apiserver.
// The storage key is read from a file so it never appears in a command line or tool output.
// Run: node backend.mjs <AgentHub.Api.dll>
import { spawn } from 'node:child_process'
import fs from 'node:fs'
const [dll] = process.argv.slice(2)
const RIG = process.env.RIG || process.env.HOME + '/rig'
const key = fs.readFileSync(RIG + '/s3/key.txt', 'utf8')
const id = key.match(/Key ID:\s*(\S+)/)[1], secret = key.match(/Secret key:\s*(\S+)/)[1]
const env = {
  ...process.env,
  ASPNETCORE_URLS: 'http://127.0.0.1:8080', ASPNETCORE_ENVIRONMENT: 'Production',
  Auth__InsecureDevMode: 'true',
  ConnectionStrings__Postgres: process.env.PG || 'Host=127.0.0.1;Port=54329;Username=postgres;Database=agenthub453',
  // The links the hub hands out are built from this origin, never from the request's Host. It is
  // access-proxy.mjs, the ingress stand-in whose access log the proof searches for secrets.
  FrontendOrigin: 'http://127.0.0.1:8081',
  AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: 'http://127.0.0.1:8080',
  AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof', AgentHub__CodexAgentImage: 'registry.example.com/agent-codex:proof',
  AgentHub__AgentImage: 'registry.example.com/agent:proof', AgentHub__PauseGracePeriodSeconds: '5',
  // Not 7681: that is the proof pod's own session agent (see the real-backend rig skill).
  AgentHub__AgentPort: '7699',
  Browser__Enabled: 'false',
  S3__ServiceUrl: 'http://127.0.0.1:3910', S3__Region: 'local', S3__Bucket: 'agenthub-proof', S3__AccessKey: id, S3__SecretKey: secret
}
const child = spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', 'inherit', 'inherit'] })
child.on('exit', code => process.exit(code ?? 1))
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s))
