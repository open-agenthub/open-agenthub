// A backend crash can retire a keep-alive socket after the final native assertions. Cleanup
// reconciles a lost reply before retrying the same resource deletion; it never sends native input.
export async function deleteProofSession(origin, id, headers, { fetchImpl = fetch, wait = ms => new Promise(resolve => setTimeout(resolve, ms)) } = {}) {
 const url = origin + '/api/sessions/' + encodeURIComponent(id);
 const requestHeaders = { ...headers, Connection: 'close' };
 let remove = true;
 for(let attempt=0;attempt<20;attempt++){
  if(remove){
   try{
    const response=await fetchImpl(url,{method:'DELETE',headers:requestHeaders,signal:AbortSignal.timeout(30000)});
    await response.arrayBuffer();
    if(response.status===204||response.status===404)return;
    if(response.status!==409&&response.status<500)throw new Error('Proof session cleanup refused: '+response.status);
   }catch(error){if(!['TypeError','TimeoutError','AbortError'].includes(error.name))throw error;}
  }
  remove=false;
  try{
   const state=await fetchImpl(url,{headers:requestHeaders,signal:AbortSignal.timeout(10000)});
   await state.arrayBuffer();
   if(state.status===404)return;
   if(state.status===200)remove=true;
   else if(state.status<500)throw new Error('Proof cleanup reconciliation refused: '+state.status);
  }catch(error){if(!['TypeError','TimeoutError','AbortError'].includes(error.name))throw error;}
  await wait(250);
 }
 throw new Error('Proof session cleanup remains unconfirmed. Inspect the disposable cluster.');
}
