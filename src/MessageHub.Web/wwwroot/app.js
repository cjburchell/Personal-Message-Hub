'use strict';
const $ = id => document.getElementById(id);
let csrf = null, view = 'ask', aiAvailable = false, busy = false, lockTimer = null;
let session = new AbortController();
const node = (tag, text, cls) => { const el = document.createElement(tag); if (text !== undefined) el.textContent = text; if (cls) el.className = cls; return el; };
const date = value => new Date(value).toLocaleString([], {dateStyle:'medium',timeStyle:'short'});
function lock(message = '') {
  clearTimeout(lockTimer);
  session.abort(); session = new AbortController(); csrf = null;
  $('app').hidden = true; $('login').hidden = false;
  $('password').value = ''; $('login-error').textContent = message;
  $('conversation').replaceChildren(); $('list-content').replaceChildren(); $('detail-body').textContent = ''; $('detail-meta').textContent = '';
  $('detail-title').textContent = 'Archived evidence'; $('question').value = '';
  $('detail').close(); busy = false;
}
async function request(path, body) {
  const response = await fetch('/api/' + path, {method:body === undefined ? 'GET':'POST', credentials:'same-origin', cache:'no-store',
    headers:body === undefined ? {} : {'Content-Type':'application/json','X-PMH-CSRF':csrf || ''},
    body:body === undefined ? undefined : JSON.stringify(body), signal:AbortSignal.any([session.signal,AbortSignal.timeout(135000)])});
  if (response.status === 401 && path !== 'login') lock('Your session ended. Sign in again.');
  if (!response.ok) throw new Error(response.status === 429 ? 'Too many requests. Please wait a minute and try again.' : response.status === 503 ? 'The local archive is busy or unavailable. Please try again.' : 'The request could not complete. Check your input and try again.');
  return response.status === 204 || response.headers.get('content-length') === '0' ? null : response.headers.get('content-type')?.includes('json') ? await response.json() : null;
}
async function refreshCsrf() { csrf = (await request('csrf')).token; }
function welcome() {
  const box = node('div', undefined, 'welcome');
  box.append(node('span','\u2197','welcome-symbol'),node('h2','Pick up the thread.'),node('p','Ask about dates, requests, or a conversation you want to revisit.'));
  const suggestions = node('div',undefined,'suggestions');
  for (const question of ['What deadlines are coming up?','Which requests may need a reply?','What was said about availability?']) {
    const button = node('button',question + ' \u2197'); button.type = 'button'; button.addEventListener('click',()=>{ $('question').value=question; $('question').focus(); }); suggestions.append(button);
  }
  box.append(suggestions,node('p','Each question searches the selected window independently. Previous chat turns are not sent to AI.','fine'));
  $('conversation').replaceChildren(box);
}
async function unlock() {
  session.abort(); session = new AbortController();
  const info = await request('session'); aiAvailable = info.aiAvailable;
  clearTimeout(lockTimer); lockTimer = setTimeout(() => lock('Your session ended. Sign in again.'), Math.max(0, new Date(info.expires).getTime() - Date.now()));
  await refreshCsrf();
  $('use-ai').disabled = !aiAvailable; $('use-ai').checked = false;
  $('ai-hint').textContent = aiAvailable ? 'Optional. Uses your configured local model.' : 'Local AI is not configured. Evidence search is available.';
  $('login').hidden = true; $('app').hidden = false; $('login-error').textContent = ''; $('password').value='';
  $('ask-button').disabled=false; $('clear').disabled=false;
  welcome(); await setView('ask');
}
$('login-form').addEventListener('submit', async event => {
  event.preventDefault(); const button=event.currentTarget.querySelector('button'); button.disabled=true; $('login-error').textContent='';
  try { await refreshCsrf(); await request('login',{password:$('password').value}); await unlock(); }
  catch (error) { $('login-error').textContent = error.message === 'The request could not complete. Check your input and try again.' ? 'Could not unlock the archive. Check your local password and try again.' : error.message; }
  finally { $('password').value=''; button.disabled=false; }
});
$('logout').addEventListener('click',async()=>{ try { await request('logout',{}); lock(); } catch { $('app-error').textContent='Could not lock the session. Please retry or close the browser session.'; } });
async function setView(next) {
  view=next; $('app-error').textContent='';
  document.querySelectorAll('[data-view]').forEach(button=>button.classList.toggle('selected',button.dataset.view===view));
  const labels={ask:['Ask your archive','Start with a question. Follow the evidence.'],search:['Search messages','Find the words, then open the original context.'],reports:['Weekly reports','A saved view of what needed your attention.'],sources:['Source coverage','Understand what is in your archive, and what might be missing.']};
  $('view-title').textContent=labels[view][0]; $('view-subtitle').textContent=labels[view][1]; $('breadcrumb').textContent='Your archive / '+labels[view][0];
  const query = view==='ask'||view==='search'; $('question-view').hidden=!query; $('list-view').hidden=query; $('clear').hidden=!query;
  $('use-ai').disabled=view==='search'||!aiAvailable; $('ask-button').textContent=view==='search'?'Search messages \u2192':'Ask question \u2191';
  $('question').placeholder=view==='search'?'Search for a name, topic, phrase, or request…':'Ask about a conversation, request, or deadline…';
  if(query) return;
  $('list-content').replaceChildren(node('p','Loading…','fine'));
  try {
    const items=await request(view==='reports'?'reports':'sources'); if(view!==next) return;
    const list=$('list-content'); list.replaceChildren();
    if(!items.length) list.append(node('div',view==='reports'?'No saved reports yet. Generate a report with the local CLI or weekly worker.':'No source sync has been recorded. This does not mean your inboxes are empty.','list-card'));
    for(const item of items) {
      const card=node('article',undefined,'list-card');
      if(view==='reports') {
        card.append(node('h2','Message report'),node('p',date(item.start)+' — '+date(item.end)),node('p',item.id,'fine'));
        const button=node('button','Open saved report','secondary'); button.addEventListener('click',()=>showReport(item.id)); card.append(button);
      } else {
        card.append(node('h2',item.source));
        if(item.lastExportImport) {
          const snapshot=item.lastExportImport;
          card.append(node('p','Export snapshot imported: '+date(snapshot.importedAt)),node('p',`${snapshot.summary.messageCount} message rows processed. Newest accepted message: ${snapshot.summary.latestMessage?date(snapshot.summary.latestMessage):'none'}.`),node('p','Snapshot coverage is unknown. Import time does not establish current provider freshness.','fine'));
        } else card.append(node('p',item.lastSuccessfulSync?'Last successful run: '+date(item.lastSuccessfulSync):'Never successfully synchronized'));
        if(item.error) card.append(node('p',item.error,'error'));
      }
      list.append(card);
    }
    if(view==='sources') list.append(node('p','Timestamps do not prove complete historical coverage. LinkedIn/Messenger imports are snapshots; missing or imported data does not establish an empty inbox.','fine'));
  } catch(error) { $('app-error').textContent=error.message; }
}
document.querySelectorAll('[data-view]').forEach(button=>button.addEventListener('click',()=>setView(button.dataset.view)));
document.querySelectorAll('[data-question]').forEach(button=>button.addEventListener('click',()=>{ $('question').value=button.dataset.question; $('question').focus(); }));
$('clear').addEventListener('click',()=>{ if(!busy) welcome(); });
function citation(id, evidence) {
  const index=evidence.findIndex(item=>item.id===id); const button=node('button','Message '+(index+1),'citation'); button.title=id;
  button.addEventListener('click',()=>showMessage(id)); return button;
}
function answerCard(result) {
  const card=node('article',undefined,'answer-card');
  card.append(node('div',result.analysisStatus,'answer-status'),node('p',`${result.counts.messageCount} archived messages · ${result.counts.incomingCount} incoming · ${result.counts.outgoingCount} outgoing | ${date(result.start)} — ${date(result.end)}`,'count-line'));
  for(const statement of result.statements) {
    card.append(node('p',statement.text,'answer-text')); const refs=node('div'); for(const id of statement.evidenceIds) refs.append(citation(id,result.evidence)); card.append(refs);
  }
  if(!result.statements.length) card.append(node('p',result.evidence.length?'Here is the matching evidence. Open a message to read its archived context.':'No matching archived excerpts were found. Try a specific name or topic, or widen the search window.','answer-text'));
  const grid=node('div',undefined,'evidence-grid');
  result.evidence.forEach((item,index)=>{
    const button=node('button',undefined,'evidence');
    button.append(node('span',`MESSAGE ${index+1} · ${date(item.sentAt)} · ${item.isOutgoing?'Sent':'Received'}`,'fine'),node('strong',item.subject||'(No subject)'),node('span',item.sender,'fine'),node('p',item.excerpt),node('span',item.truncated?'Excerpt shortened · open message':'Open archived message','fine'));
    button.addEventListener('click',()=>showMessage(item.id)); grid.append(button);
  }); card.append(grid);
  const warnings=node('details',undefined,'warnings'); warnings.append(node('summary','Coverage and interpretation limits'));
  result.warnings.forEach(warning=>warnings.append(node('p',warning))); card.append(warnings);
  if(result.inputTokens||result.outputTokens||result.cached) card.append(node('p',result.cached?'Reused local analysis. No generation tokens used.':`Local model: ${result.inputTokens} input / ${result.outputTokens} output tokens.`,'fine'));
  return card;
}
$('question-form').addEventListener('submit',async event=>{
  event.preventDefault(); if(busy)return; const question=$('question').value.trim(); if(!question)return;
  busy=true; $('ask-button').disabled=true; $('clear').disabled=true; $('app-error').textContent='';
  const conversation=$('conversation'); if(conversation.querySelector('.welcome'))conversation.replaceChildren();
  const turn=node('div',undefined,'turn'); const status=node('p','Reading your local archive…','fine'); turn.append(node('div',question,'question-bubble'),status); conversation.append(turn); conversation.scrollTop=conversation.scrollHeight;
  const activeSession=session;
  try {
    const result=view==='search'?await request('search?'+new URLSearchParams({query:question,days:$('days').value})):await request('ask',{question,days:Number($('days').value),useAi:$('use-ai').checked});
    if(activeSession!==session)return; status.replaceWith(answerCard(result)); $('question').value='';
  } catch(error) { if(activeSession===session){status.textContent=error.message; status.className='error';} }
  finally { if(activeSession===session){busy=false; $('ask-button').disabled=false; $('clear').disabled=false; conversation.scrollTop=conversation.scrollHeight;} }
});
async function showMessage(id) {
  try { const message=await request('message?'+new URLSearchParams({id})); $('detail-title').textContent=message.subject||'Archived message'; $('detail-meta').textContent=`${message.sender} · ${date(message.sentAt)} · ${message.id}${message.truncated?' · Content shortened':''}`; $('detail-body').textContent=message.body; $('detail').showModal(); }
  catch(error){$('app-error').textContent=error.message;}
}
async function showReport(id) {
  try {const report=await request('report?'+new URLSearchParams({id})); $('detail-title').textContent='Saved message report'; $('detail-meta').textContent=`${report.id}${report.truncated?' · Content shortened':''}`; $('detail-body').textContent=report.markdown; $('detail').showModal();}
  catch(error){$('app-error').textContent=error.message;}
}
$('close-detail').addEventListener('click',()=>$('detail').close());
(async()=>{try{await unlock();}catch{$('login-error').textContent='';}})();
