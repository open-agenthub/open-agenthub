const { chromium } = require(process.env.PLAYWRIGHT_PATH || '/opt/session-agent/node_modules/playwright-core')
const { execFileSync } = require('node:child_process')
const assert = require('node:assert/strict')
;(async () => {
  const browser = await chromium.connectOverCDP(process.env.CDP_ENDPOINT)
  const context = await browser.newContext({viewport:{width:1440,height:1000}})
  const page = await context.newPage(); const errors = []; page.on('pageerror',e=>errors.push(e.message))
  await page.goto(process.env.PROOF_URL || 'http://10.42.1.4:5173')
  await page.getByRole('button',{name:/Give an agent a task/}).first().waitFor()
  async function api(path,method='GET',body,owner='proof-owner') {
    return page.evaluate(async({path,method,body,owner})=>{
      const r=await fetch('/api/'+path,{method,headers:{'Content-Type':'application/json','X-Proof-Owner':owner},body:body?JSON.stringify(body):undefined})
      const text=await r.text();let data;try{data=JSON.parse(text)}catch{data=text}
      return {status:r.status,body:data}
    },{path,method,body,owner})
  }
  const suffix=Date.now()
  const oldSecret=(await api('secrets','POST',{name:'OLD_PROJECT_PROOF',delivery:'env',value:'synthetic-old-secret',description:'proof'})).body
  const newSecret=(await api('secrets','POST',{name:'NEW_PROJECT_PROOF',delivery:'env',value:'synthetic-new-secret',description:'proof'})).body
  assert.ok(oldSecret.id); assert.ok(newSecret.id)
  const source=(await api('projects','POST',{name:'Source '+suffix,color:null,secretIds:[oldSecret.id]})).body
  const target=(await api('projects','POST',{name:'Target '+suffix,color:null,secretIds:[newSecret.id]})).body
  const foreign=(await api('projects','POST',{name:'Foreign '+suffix,color:null},'proof-other')).body
  const created=await api('sessions','POST',{title:'Active move '+suffix,mode:'Interactive',agent:'Claude',authMode:'Subscription',projectId:source.id})
  assert.equal(created.status,200,JSON.stringify(created.body)); const id=created.body.id
  const pod=()=>JSON.parse(execFileSync(process.env.KUBECTL,['--kubeconfig',process.env.PROOF_KUBECONFIG,'-n','agenthub-sessions','get','pod','session-'+id,'-o','json'],{encoding:'utf8'}))
  const envNames=()=>pod().spec.containers.find(c=>c.name==='agent').env.map(e=>e.name)
  assert.ok(envNames().includes('OLD_PROJECT_PROOF'))
  assert.equal((await api('session-organization/'+id+'/project','POST',{projectId:foreign.id,confirm:true})).status,404)
  assert.equal((await api('session-organization/'+id+'/project','POST',{projectId:target.id,confirm:true})).status,409)
  assert.equal((await api('sessions/'+id,'PATCH',{projectId:target.id})).status,409)
  assert.equal((await api('sessions/'+id)).body.projectId,source.id)
  await page.reload(); const row=page.locator('[data-session-row="'+id+'"]'); await row.waitFor()
  await row.locator('[aria-label="Organize session"]').focus(); await page.keyboard.press('Enter')
  await page.getByLabel('Move to project').selectOption(target.id)
  assert.match(await page.locator('[data-project-move-impact]').textContent(),/1 removed, 1 added/)
  await page.getByRole('button',{name:'Pause and move',exact:true}).click()
  await page.getByRole('dialog').waitFor({state:'hidden'})
  const moved=(await api('sessions/'+id)).body
  assert.equal(moved.projectId,target.id); assert.equal(moved.phase,'Paused')
  const missing=execFileSync(process.env.KUBECTL,['--kubeconfig',process.env.PROOF_KUBECONFIG,'-n','agenthub-sessions','get','pod','session-'+id,'--ignore-not-found','-o','name'],{encoding:'utf8'})
  assert.equal(missing.trim(),'')
  assert.equal((await api('sessions/'+id+'/resume','POST')).status,200)
  assert.ok(envNames().includes('NEW_PROJECT_PROOF'));assert.ok(!envNames().includes('OLD_PROJECT_PROOF'))
  assert.deepEqual(errors,[])
  console.log('PASS real Chromium + real API/pod: foreign project 404, active move/PATCH 409, secret diff, confirmed pause+move deletes old pod, explicit resume admits new-project env without old-project env')
  await context.close();await browser.close()
})().catch(e=>{console.error(e);process.exit(1)})
