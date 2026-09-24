"use strict";
const $ = id => document.getElementById(id);
let token = "", role = "", tab = "overview", cursor = null, nextCursor = null, history = [], editingKey = null;
const titles = {overview:["Overview","Requests, tokens and estimated spend."],usage:["Usage","Request history and accounting details."],keys:["API keys","Access policies, expiration and spending allowances."],operators:["Operators","People who can manage this gateway."],audit:["Audit history","Management actions and rejected requests."],models:["Models & routing","Public aliases and their available provider targets."]};
let toastTimer;
function notify(message, error = false) {
  $("message").textContent = message; $("message").classList.toggle("error",error); $("message").hidden=false;
  clearTimeout(toastTimer); toastTimer=setTimeout(()=>$("message").hidden=true,7000);
}
async function request(path, options={}) {
  const headers = new Headers(options.headers);
  if(token) headers.set("Authorization","Bearer "+token);
  if(options.body) headers.set("Content-Type","application/json");
  const response = await fetch(path,{...options,headers,cache:"no-store"});
  if(!response.ok) {
    let message="The request failed.";
    try { const body=await response.json(); message=body.error?.message || body.detail || body.title || message; } catch {}
    if(response.status===401 && !path.endsWith("/login")) signOut();
    throw new Error(message);
  }
  return response;
}
async function json(path, options={}) {
  const response=await request(path,options);
  return response.status===204 ? null : response.json();
}
function guard(action) { return async event => { event?.preventDefault(); try { await action(event); } catch(error) { notify(error.message,true); } }; }
const number = value => Number(value||0).toLocaleString(undefined,{maximumFractionDigits:0});
const money = value => "$"+Number(value||0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:6});
const date = value => value ? new Date(value).toISOString().replace("T"," ").slice(0,19)+" UTC" : "—";
function filters() {
  const data=new FormData($("filters")), query=new URLSearchParams();
  for(const name of ["owner","from","to"]) if(data.get(name)) query.set(name,name==="owner"?data.get(name):data.get(name)+"T00:00:00Z");
  return query;
}
function signOut() {
  token="";role="";$("console").hidden=true;$("signin").hidden=false;
  $("secret-value").value="";document.querySelectorAll("dialog[open]").forEach(dialog=>dialog.close());
}
async function signedIn() {
  const me=await json("/admin/auth/me"); role=me.role;
  clearTimeout(toastTimer);$("message").hidden=true;
  $("username").textContent=me.username;$("role").textContent=me.role;
  $("signin").hidden=true;$("console").hidden=false;
  document.querySelectorAll("nav .admin-only").forEach(element=>element.hidden=role!=="administrator");
  await openTab("overview");
}
$("login-form").addEventListener("submit",guard(async()=>{
  const form=$("login-form"),data=new FormData(form);
  const result=await json("/admin/auth/login",{method:"POST",body:JSON.stringify({username:data.get("username"),password:data.get("password")})});
  token=result.token;form.reset();await signedIn();
}));
$("bootstrap-form").addEventListener("submit",guard(async()=>{
  token=new FormData($("bootstrap-form")).get("key").trim();
  try {await signedIn();$("bootstrap-form").reset();} catch(error) {token="";throw error;}
}));
$("logout").addEventListener("click",guard(async()=>{try {await json("/admin/auth/logout",{method:"POST"});}finally {signOut();}}));
document.querySelectorAll("[data-tab]").forEach(button=>button.addEventListener("click",guard(()=>openTab(button.dataset.tab))));
async function openTab(name) {
  tab=name;cursor=null;nextCursor=null;history=[];
  document.querySelectorAll("[data-tab]").forEach(button=>button.classList.toggle("active",button.dataset.tab===name));
  $("page-title").textContent=titles[name][0];$("page-description").textContent=titles[name][1];
  $("filters").hidden=!["overview","usage","keys"].includes(name);$("export").hidden=name==="keys";
  for(const field of ["from","to"])$("filters").elements[field].closest("label").hidden=name==="keys";
  $("overview").hidden=name!=="overview";$("records").hidden=name==="overview";
  $("new-key").hidden=name!=="keys"||role==="auditor";
  $("new-operator").hidden=name!=="operators"||role!=="administrator";
  $("reload").hidden=name!=="models"||role!=="administrator";
  $("records-title").textContent=titles[name][0];
  await load();
}
async function load() {
  $("refresh").disabled=true;
  try {
    if(tab==="overview") {
      const rows=await json("/admin/usage/summary?"+filters());
      const total=rows.reduce((a,x)=>({requests:a.requests+x.requests,tokens:a.tokens+x.input_tokens+x.output_tokens,cost:a.cost+x.cost,latency:a.latency+x.average_latency_ms*x.requests}),{requests:0,tokens:0,cost:0,latency:0});
      $("stat-requests").textContent=number(total.requests);$("stat-tokens").textContent=number(total.tokens);
      $("stat-cost").textContent=money(total.cost);$("stat-latency").textContent=number(total.requests?total.latency/total.requests:0)+" ms";
      const chart=$("spend-chart");chart.replaceChildren();
      const sorted=[...rows].sort((a,b)=>b.cost-a.cost).slice(0,12),max=Math.max(...sorted.map(x=>x.cost),0.000000001);
      if(!rows.length){const empty=document.createElement("p");empty.className="empty";empty.textContent="Usage will appear after your first request.";chart.append(empty);}
      for(const row of sorted) {const line=document.createElement("div");line.className="chart-row";const label=document.createElement("span");label.textContent=row.model+" / "+row.provider;const meter=document.createElement("meter");meter.max=max;meter.value=row.cost;meter.setAttribute("aria-label",label.textContent+" spend");const value=document.createElement("strong");value.textContent=money(row.cost);line.append(label,meter,value);chart.append(line);}
      return;
    }
    let page,rows,head,render;
    const query=filters();query.set("limit","25");if(cursor)query.set("cursor",cursor);
    if(tab==="usage"){page=await json("/admin/usage/page?"+query);rows=page.data;head=["Time","Model / provider","Status","Tokens","Cost","Latency"];render=x=>[date(x.created_at),x.model+" / "+x.provider,x.status,number(x.total_tokens),money(x.estimated_cost),number(x.latency_ms)+" ms"];}
    if(tab==="keys"){page=await json("/admin/keys/page?"+query);rows=page.data;head=["Owner / prefix","Models","Status / expiry","Spent","Monthly budget","Actions"];render=x=>[x.owner+" · "+x.prefix+"…",x.allowed_models.join(", "),(x.enabled?"Enabled":"Disabled")+" · "+date(x.expires_at),money(x.spent),x.monthly_spending_budget==null?"Unlimited":money(x.monthly_spending_budget),keyActions(x)];}
    if(tab==="audit"){page=await json("/admin/audit?"+query);rows=page.data;head=["Time","Actor","Action","Resource","Status"];render=x=>[date(x.created_at),x.actor,x.action,x.resource,x.status_code||"Started"];}
    if(tab==="operators"){rows=await json("/admin/operators");head=["Username","Role","Status","Created","Actions"];render=x=>[x.username,x.role,x.enabled?"Enabled":"Disabled",date(x.created_at),operatorActions(x)];}
    if(tab==="models"){rows=await json("/admin/models");head=["Alias","Routing","Concurrency","Targets","Capabilities"];render=x=>[x.name,x.routing,x.max_concurrent_requests,x.targets.map(t=>t.provider+" / "+t.model).join("\n"),x.targets.map(t=>t.provider+": "+t.capabilities).join("\n")];}
    drawTable(head,rows.map(render));
    nextCursor=page?.next_cursor||null;$("next").disabled=!nextCursor;$("previous").disabled=!history.length;
    $("record-count").textContent=rows.length?number(rows.length)+" records":"No records match these filters.";
  } finally {$("refresh").disabled=false;}
}
function drawTable(head,rows) {
  const table=$("table");table.replaceChildren();const thead=document.createElement("thead"),tr=document.createElement("tr");
  for(const title of head){const th=document.createElement("th");th.textContent=title;th.scope="col";tr.append(th);}thead.append(tr);table.append(thead);
  const tbody=document.createElement("tbody");
  for(const cells of rows){const tr=document.createElement("tr");for(const value of cells){const td=document.createElement("td");if(value instanceof Node)td.append(value);else td.textContent=value;tr.append(td);}tbody.append(tr);}
  table.append(tbody);
}
function button(label,action){const element=document.createElement("button");element.textContent=label;element.addEventListener("click",guard(action));return element;}
function keyPolicy(key){return {enabled:key.enabled,allowed_models:key.allowed_models,requests_per_minute:key.requests_per_minute,token_limit:key.token_limit,spending_budget:key.spending_budget,expires_at:key.expires_at,monthly_token_limit:key.monthly_token_limit,monthly_spending_budget:key.monthly_spending_budget};}
function keyActions(key) {
  const actions=document.createElement("div");if(role==="auditor")return actions;
  actions.append(button("Edit",()=>editKey(key)),button(key.enabled?"Disable":"Enable",async()=>{
    await json("/admin/keys/"+key.id,{method:"PUT",body:JSON.stringify({...keyPolicy(key),enabled:!key.enabled})});await load();
  }),button("Rotate",async()=>{
    if(!confirm("Rotate this key? The previous credential will work for one hour. Allowances are preserved."))return;
    const result=await json("/admin/keys/"+key.id+"/rotate",{method:"POST",body:JSON.stringify({grace_seconds:3600})});reveal(result.key);await load();
  }));return actions;
}
function operatorActions(account) {
  const actions=document.createElement("div");
  actions.append(button(account.enabled?"Disable":"Enable",async()=>{
    await json("/admin/operators/"+account.id,{method:"PUT",body:JSON.stringify({enabled:!account.enabled,role:account.role})});await load();
  }));return actions;
}
function editKey(key=null) {
  editingKey=key;const form=$("key-form");form.reset();$("key-title").textContent=key?"Edit API key":"Create API key";
  const values=key?{owner:key.owner,models:key.allowed_models.join(", "),rpm:key.requests_per_minute,expires:key.expires_at?new Date(key.expires_at).toISOString().slice(0,16):"",budget:key.spending_budget,monthly_budget:key.monthly_spending_budget,tokens:key.token_limit,monthly_tokens:key.monthly_token_limit}:{};
  for(const [name,value] of Object.entries(values))form.elements[name].value=value??"";
  form.elements.owner.disabled=!!key;form.elements.enabled.checked=key?key.enabled:true;
  form.elements.enabled.closest("label").hidden=!key;
  $("key-dialog").showModal();
}
function reveal(secret){$("secret-value").value=secret;$("secret-dialog").showModal();}
$("key-form").addEventListener("submit",guard(async()=>{
  const form=$("key-form"),data=new FormData(form),numeric=name=>data.get(name)===""?null:Number(data.get(name));
  const body={owner:data.get("owner"),enabled:form.elements.enabled.checked,allowed_models:data.get("models").split(",").map(x=>x.trim()).filter(Boolean),requests_per_minute:numeric("rpm"),expires_at:data.get("expires")?data.get("expires")+":00Z":null,spending_budget:numeric("budget"),monthly_spending_budget:numeric("monthly_budget"),token_limit:numeric("tokens"),monthly_token_limit:numeric("monthly_tokens")};
  const result=await json(editingKey?"/admin/keys/"+editingKey.id:"/admin/keys",{method:editingKey?"PUT":"POST",body:JSON.stringify(body)});
  $("key-dialog").close();if(result.key)reveal(result.key);notify(editingKey?"Key updated.":"Key created.");await load();
}));
$("new-key").addEventListener("click",()=>editKey());
$("new-operator").addEventListener("click",()=>{$("operator-form").reset();$("operator-dialog").showModal();});
$("operator-form").addEventListener("submit",guard(async()=>{
  const form=$("operator-form");await json("/admin/operators",{method:"POST",body:JSON.stringify(Object.fromEntries(new FormData(form)))});
  form.reset();$("operator-dialog").close();notify("Operator created.");await load();
}));
document.querySelectorAll("[data-close]").forEach(button=>button.addEventListener("click",()=>$(button.dataset.close).close()));
$("secret-dialog").addEventListener("close",()=>$("secret-value").value="");
$("copy-secret").addEventListener("click",guard(async()=>{await navigator.clipboard.writeText($("secret-value").value);notify("Key copied.");}));
$("refresh").addEventListener("click",guard(load));
$("filters").addEventListener("submit",guard(async()=>{cursor=null;history=[];await load();}));
$("next").addEventListener("click",guard(async()=>{history.push(cursor);cursor=nextCursor;await load();}));
$("previous").addEventListener("click",guard(async()=>{cursor=history.pop()??null;await load();}));
$("reload").addEventListener("click",guard(async()=>{const result=await json("/admin/config/reload",{method:"POST"});notify("Configuration reloaded. "+result.models+" model aliases loaded.");await load();}));
$("export").addEventListener("click",guard(async()=>{
  const response=await request("/admin/usage/export?"+filters()),url=URL.createObjectURL(await response.blob()),link=document.createElement("a");
  link.href=url;link.download="llmproxy-usage.csv";link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}));
