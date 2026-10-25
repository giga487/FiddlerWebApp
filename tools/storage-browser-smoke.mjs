import fs from 'node:fs/promises';
const cdp=process.env.UOAS_CDP??'http://127.0.0.1:9239';
const frontend=process.env.UOAS_FRONTEND??'http://localhost:5178';
const backend=process.env.UOAS_BACKEND??'http://localhost:5188';
const output=process.env.UOAS_SCREENSHOT_DIR??'.';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const started=await(await fetch(backend+'/api/sessions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:'Storage browser verification'})})).json();
const headers={'Content-Type':'application/json','X-UOAS-Session':started.token};
async function api(path,method='GET',body){const response=await fetch(backend+path,{method,headers,body:body?JSON.stringify(body):undefined});if(!response.ok)throw new Error(await response.text());return response.status===204?null:response.json();}
const original=await api('/api/configuration');
const target=await(await fetch(`${cdp}/json/new?${encodeURIComponent(frontend+'/configuration')}`,{method:'PUT'})).json();
const socket=new WebSocket(target.webSocketDebuggerUrl);await new Promise(resolve=>socket.addEventListener('open',resolve,{once:true}));
let id=0;const pending=new Map();
socket.addEventListener('message',event=>{const value=JSON.parse(event.data);if(pending.has(value.id)){pending.get(value.id)(value);pending.delete(value.id);}});
const command=(method,params={})=>new Promise(resolve=>{const key=++id;pending.set(key,resolve);socket.send(JSON.stringify({id:key,method,params}));});
const evaluate=async expression=>{const value=await command('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(value.result?.exceptionDetails)throw new Error(JSON.stringify(value.result.exceptionDetails));return value.result?.result?.value;};
const wait=async(expression,label)=>{const deadline=Date.now()+30000;while(Date.now()<deadline){if(await evaluate(expression))return;await sleep(100);}throw new Error(`${label}: ${await evaluate('document.body.innerText')}`);};
const press=async label=>{await sleep(100);await wait(`Array.from(document.querySelectorAll('button')).some(b=>b.textContent.trim()===${JSON.stringify(label)}&&!b.matches(':disabled'))`,'Enabled '+label);await evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()===${JSON.stringify(label)}).click()`);};
try{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  await wait('!!document.querySelector(".welcome form")','WASM startup');
  await evaluate(`(()=>{const input=document.querySelector('.welcome input');input.value='Alice';input.dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('.welcome form').requestSubmit();})()`);
  await wait('document.querySelector(".storage-breadcrumb")?.textContent.trim()==="/"','Root folder');
  await press('ServerData/');
  await wait('document.querySelector(".storage-breadcrumb")?.textContent.trim()==="/ServerData"','Nested folder');
  if(!await evaluate('document.querySelector(".storage-browser").textContent.includes("regions.json")'))throw new Error('Region file missing');
  await press('Parent folder');await wait('document.querySelector(".storage-breadcrumb")?.textContent.trim()==="/"','Parent navigation');
  await press('Exports/');await wait('document.querySelector(".storage-breadcrumb")?.textContent.trim()==="/Exports"','Export folder');
  await press('Use folder for assets');
  await evaluate(`(()=>{const input=document.querySelector('.settings-source input');const source=input.closest('.settings-source');const name=Array.from(source.querySelectorAll('label')).find(l=>l.textContent==='Name').querySelector('input');name.value='Browser verification';name.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await press('Save configuration');await wait('document.querySelector(".notice.success")?.textContent.includes("saved")','Persistent configuration save');
  await wait('Array.from(document.querySelectorAll(".workspacebar option")).some(o=>o.textContent==="Browser verification")','Libraries refreshed after save');
  const saved=await api('/api/configuration');
  if(!saved.libraries.some(item=>item.name==='Browser verification'&&item.directory==='Exports'))throw new Error('A root-relative library was not persisted');
  await press('Reload configuration');await wait('document.querySelector(".tool-page")?.getAttribute("aria-busy")==="false"','Configuration reload');
  if(!await evaluate('Array.from(document.querySelectorAll(".settings-source input")).some(input=>input.value==="Browser verification")'))throw new Error('Saved source disappeared after reload');
  await fs.mkdir(output,{recursive:true});const screenshot=await command('Page.captureScreenshot',{format:'png'});await fs.writeFile(`${output}/uoas-configuration.png`,Buffer.from(screenshot.result.data,'base64'));
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:false});await sleep(300);
  if(!await evaluate('document.documentElement.scrollWidth<=innerWidth'))throw new Error('Configuration mobile overflow');
  await press('End session');
  console.log('PASS: storage root browser, nested navigation, relative source selection, configuration save/reload, library refresh and mobile layout.');
}finally{
  const latest=await api('/api/configuration');await api('/api/configuration','PUT',{...original,revision:latest.revision});await api('/api/session','DELETE');
  await Promise.race([command('Browser.close'),sleep(1000)]);socket.close();
}
