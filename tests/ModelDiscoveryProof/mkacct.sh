#!/bin/bash
# Fake ChatGPT login for account $2 in CODEX_HOME $1, pointed at the mock on 18181.
d=$1; acct=$2; rm -rf $d; mkdir -p $d; node -e '
const b=o=>Buffer.from(JSON.stringify(o)).toString("base64url");const a=process.argv[2];
const claims={email:a+"@example.com",exp:Math.floor(Date.now()/1000)+86400,"https://api.openai.com/auth":{chatgpt_plan_type:"plus",chatgpt_account_id:a,chatgpt_user_id:"u-"+a}};
const jwt=b({alg:"none"})+"."+b(claims)+".sig";
require("fs").writeFileSync(process.argv[1]+"/auth.json",JSON.stringify({OPENAI_API_KEY:null,tokens:{id_token:jwt,access_token:jwt,refresh_token:"rt-SECRET",account_id:a},last_refresh:new Date().toISOString()}));' $d $acct
printf 'chatgpt_base_url = "http://127.0.0.1:18181/backend-api/"\nopenai_base_url = "http://127.0.0.1:18181/backend-api/codex"\n' > $d/config.toml
