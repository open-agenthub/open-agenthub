const assert = require('node:assert/strict');
(async () => {
 const search = query => fetch('http://127.0.0.1:8080/api/ee/sessions/alice-session/shares/recipients?query='+encodeURIComponent(query),{headers:{Cookie:'proof-user=alice'}});
 assert.deepEqual(await (await search('bo')).json(),[]);
 console.log('PASS disabled suggestions do not return prefix matches');
 for (const identifier of ['bob','bob@example.com']) {
  const users = await (await search(identifier)).json();
  assert.deepEqual(users,[{username:'bob',displayName:'Bob'}]);
 }
 console.log('PASS exact username and email still yield only display name and username');
 let throttled = false;
 for (let i=0;i<31;i++) {
  const result = await search('bo');
  if (result.status === 429) throttled = true;
 }
 assert.ok(throttled);
 console.log('PASS real HTTP rate limiter returns 429');
})().catch(e=>{console.error(e);process.exit(1)});
