// Only sanitized screens become evidence; authorization material stays in memory.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const pty = require('/opt/session-agent/node_modules/node-pty');
const { Terminal } = require('/opt/session-agent/node_modules/@xterm/headless');
const [name, executable, argsJson = '[]', stepsJson = '[]'] = process.argv.slice(2);
const home = fs.mkdtempSync(path.join(os.tmpdir(), 'signin-proof-'));
const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|XDG_|.*TOKEN|.*KEY|.*SECRET)/.test(k)));
Object.assign(env, { HOME: home, XDG_CONFIG_HOME: home + '/.config', XDG_DATA_HOME: home + '/.local/share', XDG_STATE_HOME: home + '/.local/state', TERM: 'xterm-256color', COLORTERM: 'truecolor', DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', AGENT_CLI_CREDENTIAL_STORE: 'file' });
fs.writeFileSync(home + '/.claude.json', JSON.stringify({hasCompletedOnboarding:true, theme:'dark', projects:{[home]:{hasTrustDialogAccepted:true, hasCompletedProjectOnboarding:true}}}));
const terminal = new Terminal({cols:120,rows:40,allowProposedApi:true});
const child = pty.spawn(executable, JSON.parse(argsJson), {cwd:home,env,cols:120,rows:40,name:'xterm-256color'});
child.onData(data => terminal.write(data));
terminal.onData(data => child.write(data));
let exit;
child.onExit(e => {exit=e.exitCode});
const wait = ms => new Promise(resolve => setTimeout(resolve,ms));
function screen() {
 const b=terminal.buffer.active;
 return Array.from({length:terminal.rows},(_,y)=>b.getLine(b.baseY+y)?.translateToString(true)||'').filter(Boolean).join('\n');
}
function sanitize(text) {
 let continuation = false;
 text = text.split('\n').map(line => {
  if (/https?:\/\//.test(line)) { continuation = true; return line.replace(/https?:\/\/.*$/, '[sign-in URL removed]'); }
  if (continuation && /^\S+$/.test(line)) return '[wrapped sign-in URL removed]';
  continuation = false;
  return line;
 }).join('\n');
 return text
 .replace(/\b[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+\b/g,'[device code removed]')
 .replace(/\b[A-Za-z0-9_%+\/=.#~-]{24,}\b/g,'[value removed]');
}
(async()=>{
 for(const step of JSON.parse(stepsJson)) {
  if(step.wait) await wait(step.wait);
  if(step.keys) child.write(step.keys);
  if(step.mockCode) {
   const rows=screen().split('\n');let address='';
   for(let i=0;i<rows.length;i++) {const match=/https:\/\/\S+/.exec(rows[i]);if(!match)continue;address=match[0];while(i+1<rows.length && /^\S+$/.test(rows[i+1]))address+=rows[++i];}
   const state=new URL(address).searchParams.get('state');
   if(!state)throw new Error('No authorization state on the mock-login screen');
   child.write('\x1b[200~example-code#'+state+'\x1b[201~');await wait(150);child.write('\r');
  }
  if(step.text) {child.write('\x1b[200~'+step.text+'\x1b[201~');await wait(150);child.write('\r');}
  if(step.dump) {await wait(200);console.log(JSON.stringify({agent:name,stage:step.dump,exit,screen:sanitize(screen())}));}
 }
 try{child.kill();}catch{}
 await wait(300);
 // CLI diagnostics can contain authorization data, so isolated homes are never archived.
 fs.rmSync(home,{recursive:true,force:true});
 process.exit(0);
})().catch(()=>{try{child.kill();}catch{};fs.rmSync(home,{recursive:true,force:true});console.error('Measurement failed; authorization details omitted');process.exit(1)});
