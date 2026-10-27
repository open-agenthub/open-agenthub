'use strict';
// Return a native tool call, then observe the real CLI's tool result. The model endpoint
// is local and uses a fake API key; log only fixed assertions, never request bodies.
const http=require('node:http');
const question='Which proof should run next when a terminal redraw wraps the question over several rows and the answer must preserve a line break through the actual workspace, session transport and native CLI?';
let answered=false;
const server=http.createServer((req,res)=>{
 if(req.url==='/proof'){if(req.method==='POST')answered=false;res.setHeader('Content-Type','application/json');res.end(JSON.stringify({question,answered}));return;}
 let body='';req.on('data',data=>{body+=data;if(body.length>1000000)req.destroy();});req.on('end',()=>{
  let input;try{input=JSON.parse(body);}catch{res.writeHead(400);res.end();return;}
  if(req.url.includes('count_tokens')){res.setHeader('Content-Type','application/json');res.end('{"input_tokens":10}');return;}
  const results=(input.messages||[]).flatMap(m=>Array.isArray(m.content)?m.content:[]).filter(b=>b.type==='tool_result');
  if(results.some(result=>JSON.stringify(result.content).includes('Runtime first\\nthen browser')))answered=true;
  if(!input.stream){res.setHeader('Content-Type','application/json');res.end(JSON.stringify({id:'msg_proof',type:'message',role:'assistant',model:'proof',content:[{type:'text',text:'ok'}],stop_reason:'end_turn',usage:{input_tokens:1,output_tokens:1}}));return;}
  res.writeHead(200,{'Content-Type':'text/event-stream'});
  const event=(type,data)=>res.write('event: '+type+'\ndata: '+JSON.stringify({type,...data})+'\n\n');
  event('message_start',{message:{id:'msg_proof',type:'message',role:'assistant',model:'proof',content:[],stop_reason:null,usage:{input_tokens:1,output_tokens:1}}});
  const ask=!results.length&&(input.tools||[]).some(tool=>tool.name==='AskUserQuestion');
  const content=ask?{type:'tool_use',id:'toolu_proof',name:'AskUserQuestion',input:{}}:{type:'text',text:''};
  event('content_block_start',{index:0,content_block:content});
  event('content_block_delta',{index:0,delta:ask?{type:'input_json_delta',partial_json:JSON.stringify({questions:[{question,header:'Proof',options:[{label:'Runtime',description:'Run runtime checks'},{label:'Browser',description:'Run browser checks'}],multiSelect:false}]})}:{type:'text_delta',text:'Answer recorded'}});
  event('content_block_stop',{index:0});event('message_delta',{delta:{stop_reason:ask?'tool_use':'end_turn'},usage:{output_tokens:1}});event('message_stop',{});res.end();
 });
});
server.listen(17100,'127.0.0.1',()=>console.log('Question model ready'));
