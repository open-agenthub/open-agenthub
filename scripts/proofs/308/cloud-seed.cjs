const http=require('http'),fs=require('fs'),root=process.argv[2];
http.createServer((req,res)=>{
 const name={'/user-data':'user-data','/meta-data':'meta-data','/vendor-data':null}[req.url];
 if(name===undefined){res.writeHead(404).end();return;}
 res.end(name?fs.readFileSync(root+'/'+name):'');
}).listen(18000,'127.0.0.1');
