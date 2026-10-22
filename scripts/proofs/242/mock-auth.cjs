// A local fake identity proves the CLI's success branch without authorizing a real account.
// Requests, headers, URLs, authorization codes, and tokens are deliberately never logged.
const http=require('node:http'),https=require('node:https'),tls=require('node:tls');
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {execFileSync}=require('node:child_process');
const dir=process.env.SIGNIN_TLS_DIR||path.join(os.tmpdir(),'signin-242-tls');
fs.mkdirSync(dir,{recursive:true});
execFileSync('openssl',['req','-x509','-newkey','rsa:2048','-nodes','-keyout',dir+'/ca.key','-out',dir+'/ca.pem','-days','1','-subj','/CN=Sign-in proof CA'],{stdio:'ignore'});
const contexts=new Map();
function context(host){
 if(contexts.has(host))return contexts.get(host);
 const leaf=path.join(dir,'leaf-'+contexts.size);
 execFileSync('openssl',['req','-newkey','rsa:2048','-nodes','-keyout',leaf+'.key','-out',leaf+'.csr','-subj','/CN=Sign-in proof'],{stdio:'ignore'});
 fs.writeFileSync(leaf+'.ext','subjectAltName=DNS:'+host+'\n');
 execFileSync('openssl',['x509','-req','-in',leaf+'.csr','-CA',dir+'/ca.pem','-CAkey',dir+'/ca.key','-CAcreateserial','-out',leaf+'.pem','-days','1','-extfile',leaf+'.ext'],{stdio:'ignore'});
 const value=tls.createSecureContext({key:fs.readFileSync(leaf+'.key'),cert:fs.readFileSync(leaf+'.pem')});contexts.set(host,value);return value;
}
const counts={token:0,profile:0,other:0};
const server=https.createServer({SNICallback:(host,done)=>{try{done(null,context(host))}catch(e){done(e)}}},async(req,res)=>{
 const pathname=new URL(req.url,'https://example.com').pathname;
 let body='';for await(const chunk of req)body+=chunk;
 let result={};
 if(pathname==='/v1/oauth/token'){
  counts.token++;
  result={token_type:'Bearer',access_token:'example-proof-access-token',refresh_token:'example-proof-refresh-token',expires_in:28800,scope:'user:inference user:profile',account:{uuid:'example-account',email_address:'person@example.com'},organization:{uuid:'example-organization'}};
 } else if(pathname.startsWith('/api/oauth/profile')){
  counts.profile++;
  result={account:{uuid:'example-account',email_address:'person@example.com',display_name:'Example Person'},organization:{uuid:'example-organization',organization_type:'claude_max',rate_limit_tier:'default_claude_max_20x',billing_type:'stripe_subscription'}};
 } else counts.other++;
 res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(result));
});
const proxy=http.createServer((req,res)=>{res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(counts))});
proxy.on('connect',(req,socket,head)=>{socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');if(head.length)socket.unshift(head);server.emit('connection',socket)});
proxy.listen(18443,'127.0.0.1',()=>console.log('Local fake-identity proxy ready'));
