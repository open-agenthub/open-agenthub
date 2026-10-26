// A local fake identity proves the CLI's success branch without authorizing a real account.
// Requests, headers, URLs, authorization codes, and tokens are deliberately never logged.
const http=require('node:http'),https=require('node:https'),tls=require('node:tls');
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {execFileSync}=require('node:child_process');
const dir=process.env.SIGNIN_TLS_DIR||fs.mkdtempSync(path.join(os.tmpdir(),'signin-242-tls-'));
fs.mkdirSync(dir,{recursive:true,mode:0o700});
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
const counts={token:0,profile:0,other:0,expired:0};
let failAuth=false;
const server=https.createServer({SNICallback:(host,done)=>{try{done(null,context(host))}catch(e){done(e)}}},async(req,res)=>{
 const pathname=new URL(req.url,'https://example.com').pathname;
 let body='';for await(const chunk of req)body+=chunk;
 let result={};
 const jwt=Buffer.from(JSON.stringify({alg:'RS256',typ:'JWT'})).toString('base64url')+'.'+Buffer.from(JSON.stringify({sub:'example-account',email:'person@example.com',exp:Math.floor(Date.now()/1000)+3600})).toString('base64url')+'.'+Buffer.from('example-signature').toString('base64url');
 if(pathname.endsWith('/deviceauth/usercode'))result={device_auth_id:'example-device',user_code:'ABCD-12345',interval:'1'};
 else if(pathname.endsWith('/deviceauth/token') && process.env.SIGNIN_DEVICE_TOKEN_STATUS){res.writeHead(Number(process.env.SIGNIN_DEVICE_TOKEN_STATUS),{'Content-Type':'application/json'});return res.end('{"error":"proof failure"}');}
 else if(pathname.endsWith('/deviceauth/token'))result={authorization_code:'example-code',code_verifier:'example-verifier',code_challenge:'example-challenge'};
 else if(pathname==='/auth/poll' && (process.env.SIGNIN_AUTH_FAIL==='1'||failAuth)){res.writeHead(403,{'Content-Type':'application/json'});return res.end('{}');}
 else if(pathname==='/auth/poll')result={accessToken:jwt,refreshToken:'example-proof-refresh-token'};
 else if(pathname==='/oauth/token')result={id_token:jwt,access_token:'example-proof-access-token',refresh_token:'example-proof-refresh-token',token_type:'Bearer',expires_in:3600};
 else if(pathname==='/v1/oauth/token'){
  counts.token++;
  let payload={};try{payload=JSON.parse(body);}catch{payload=Object.fromEntries(new URLSearchParams(body));}
  if(payload.grant_type==='refresh_token'){counts.expired++;res.writeHead(400,{'Content-Type':'application/json'});return res.end(JSON.stringify({error:'invalid_grant',error_description:'Example refresh token expired'}));}
  result={token_type:'Bearer',access_token:'example-proof-access-token',refresh_token:'example-proof-refresh-token',expires_in:28800,scope:'user:inference user:profile',account:{uuid:'example-account',email_address:'person@example.com'},organization:{uuid:'example-organization'}};
 } else if(pathname.startsWith('/api/oauth/profile')){
  counts.profile++;
  result={account:{uuid:'example-account',email_address:'person@example.com',display_name:'Example Person'},organization:{uuid:'example-organization',organization_type:'claude_max',rate_limit_tier:'default_claude_max_20x',billing_type:'subscription'}};
 } else if(pathname==='/v1/messages'){res.writeHead(401,{'Content-Type':'application/json'});return res.end(JSON.stringify({type:'error',error:{type:'authentication_error',message:'OAuth token has expired.'}}));}
 else counts.other++;
 res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(result));
});
const proxy=http.createServer((req,res)=>{if(req.url==='/__proof/fail')failAuth=true;if(req.url==='/__proof/success')failAuth=false;res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(counts))});
proxy.on('connect',(req,socket,head)=>{socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');if(head.length)socket.unshift(head);server.emit('connection',socket)});
proxy.listen(Number(process.env.SIGNIN_PROXY_PORT||18443),'127.0.0.1',()=>console.log('Local fake-identity proxy ready; CA file: '+dir+'/ca.pem'));
