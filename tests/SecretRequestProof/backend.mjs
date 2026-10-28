// Starts the real backend (dev auth) against the rig's Postgres and kube-apiserver. No object
// storage: nothing on the secret request path reads or writes the state archive.
// Run: node backend.mjs <AgentHub.Api.dll> ['{"Extra__Setting":"value"}']
import { spawn } from 'node:child_process'
import fs from 'node:fs'
const [dll, extra = '{}'] = process.argv.slice(2)
const env = {
  ...process.env,
  ASPNETCORE_URLS: 'http://127.0.0.1:8080', ASPNETCORE_ENVIRONMENT: 'Production',
  ConnectionStrings__Postgres: `Host=127.0.0.1;Port=54329;Username=postgres;Database=${process.env.PGDB || 'agenthub330'}`,
  AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: 'http://127.0.0.1:8080',
  AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof', AgentHub__CodexAgentImage: 'registry.example.com/agent-codex:proof',
  AgentHub__AgentImage: 'registry.example.com/agent:proof',
  // The proof pod's own session agent listens on 7681 on every interface; a stand-in pod on
  // 127.0.0.1 must not reach it.
  AgentHub__AgentPort: '7699',
  Browser__Enabled: 'false',
  // API_KEY_FILE switches to API-key mode, so that a callback token sent to the owner API is
  // turned away (insecure dev mode lets every request act as the owner).
  ...(process.env.API_KEY_FILE ? { Auth__ApiKey: fs.readFileSync(process.env.API_KEY_FILE, 'utf8').trim() } : { Auth__InsecureDevMode: 'true' }),
  ...JSON.parse(extra)
}
const child = spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', 'inherit', 'inherit'] })
child.on('exit', code => process.exit(code ?? 1))
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s))
