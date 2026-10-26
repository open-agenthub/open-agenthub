// Starts a backend build (the branch's or main's) with dev auth against the rig: real Postgres, the
// real kube-apiserver (~/.kube/config) and the mock GitLab as the connected provider. The OAuth
// client secret is read from a file so it never appears in a command line or in output.
// Run: node backend.mjs <AgentHub.Api.dll> <rig> <database> ['{"Extra__Setting":"value"}']
import { spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

const [dll, rig, database, extra = '{}'] = process.argv.slice(2)
const env = {
  ...process.env,
  ASPNETCORE_URLS: 'http://127.0.0.1:8080', ASPNETCORE_ENVIRONMENT: 'Production',
  ConnectionStrings__Postgres: `Host=127.0.0.1;Port=54329;Username=postgres;Database=${database}`,
  AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: 'http://127.0.0.1:8080',
  AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof', AgentHub__CodexAgentImage: 'registry.example.com/agent-codex:proof',
  AgentHub__AgentImage: 'registry.example.com/agent:proof',
  // Not 7681: that is the port of the session agent running this proof (every 127.x is this pod).
  AgentHub__AgentPort: '7699',
  Browser__Enabled: 'false',
  S3__ServiceUrl: 'http://127.0.0.1:3999', S3__Region: 'local', S3__Bucket: 'agenthub-proof', S3__AccessKey: 'proof', S3__SecretKey: 'proof-not-a-secret',
  FrontendOrigin: 'http://127.0.0.1:8080',
  Git__StateKey: crypto.randomBytes(16).toString('hex'),
  Git__Providers__0__Id: 'gitlab', Git__Providers__0__Type: 'gitlab', Git__Providers__0__DisplayName: 'GitLab',
  Git__Providers__0__BaseUrl: 'https://127.0.0.10',
  Git__Providers__0__ClientId: 'proof-client',
  Git__Providers__0__ClientSecret: fs.readFileSync(path.join(rig, 'mock/client-secret.txt'), 'utf8').trim(),
  // .NET on Linux builds its trust store from OpenSSL's, which honours SSL_CERT_FILE.
  SSL_CERT_FILE: path.join(rig, 'mock/certs/bundle.crt'),
  DOTNET_GCHeapHardLimit: '0x30000000',
  ...JSON.parse(extra)
}
const child = spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', 'inherit', 'inherit'] })
child.on('exit', code => process.exit(code ?? 1))
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s))
