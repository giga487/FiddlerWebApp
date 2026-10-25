import fs from 'node:fs/promises';
const cdp=process.env.UOAS_CDP??'http://127.0.0.1:9239';
const frontend=process.env.UOAS_FRONTEND??'http://localhost:5178';
const output=process.env.UOAS_SCREENSHOT_DIR??'.';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const failures=[];
async function select(client,name){await client.wait('document.querySelector(".tool-page")?.getAttribute("aria-busy")==="false"','Region operation finished');await client.evaluate(`Array.from(document.querySelectorAll('td button')).find(b=>b.textContent.trim()===${JSON.stringify(name)}).click()`);}
async function page(){
  const target=await(await fetch(`${cdp}/json/new?${encodeURIComponent(frontend+'/regions')}`,{method:'PUT'})).json();
  const socket=new WebSocket(target.webSocketDebuggerUrl); await new Promise(resolve=>socket.addEventListener('open',resolve,{once:true}));
  let id=0; const pending=new Map();
  socket.addEventListener('message',event=>{const value=JSON.parse(event.data);if(pending.has(value.id)){pending.get(value.id)(value);pending.delete(value.id);}if(value.method==='Runtime.exceptionThrown')failures.push(value.params.exceptionDetails.text);});
  const command=(method,params={})=>new Promise(resolve=>{const key=++id;pending.set(key,resolve);socket.send(JSON.stringify({id:key,method,params}));});
  const evaluate=async expression=>{const result=await command('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(result.result?.exceptionDetails)throw new Error(JSON.stringify(result.result.exceptionDetails));return result.result?.result?.value;};
  const wait=async(expression,label)=>{const deadline=Date.now()+30000;while(Date.now()<deadline){if(await evaluate(expression))return;await sleep(100);}throw new Error(`${label}: ${await evaluate('document.body.innerText')}`);};
  const press=async label=>{await sleep(100);await wait(`Array.from(document.querySelectorAll('button')).some(b=>b.textContent.trim()===${JSON.stringify(label)}&&!b.matches(':disabled'))`,'Enabled '+label);await evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()===${JSON.stringify(label)}).click()`);};
  await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  return{socket,command,evaluate,wait,press};
}
async function start(client,name){
  await client.wait('!!document.querySelector(".welcome form")','WASM startup');
  await client.evaluate(`(()=>{const input=document.querySelector('.welcome input');input.value=${JSON.stringify(name)};input.dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('.welcome form').requestSubmit();})()`);
  await client.wait('document.querySelectorAll(".tool-fields select option").length>1','Region sources');
  await client.press('Open regions');
  await client.wait('document.querySelectorAll("tbody tr").length>=2','Catalog loaded');
  await select(client,'Harbor');
  await client.wait('!!document.querySelector(".region-stage canvas")','Region canvas');
}
const alice=await page(),bob=await page();
try{
  await start(alice,'Alice');await alice.press('Start editing region');
  await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Alice")','Alice owns region');
  await start(bob,'Bob');
  await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Alice")','Bob sees Alice');
  if(!await bob.evaluate('Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Save region draft").disabled'))throw new Error('Foreign region save enabled');
  await alice.evaluate(`(()=>{const input=Array.from(document.querySelectorAll('label')).find(l=>l.textContent==='Priority').querySelector('input');input.value='91';input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await alice.press('Save region draft');await alice.wait('document.querySelector(".tool-page").textContent.includes("1 drafts")','Region draft');
  await alice.press('Publish regions');await alice.wait('document.querySelector(".notice.success")?.textContent.includes("Published region revision")','JSON publication');
  await bob.press('Refresh published regions');await bob.wait('Array.from(document.querySelectorAll("td")).some(td=>td.textContent.includes("priority 91"))','Shared revision visible');
  await select(alice,'Cellar');
  await alice.wait('document.querySelector(".tool-output h2")?.textContent.includes("Cellar")','Visibility region');
  await alice.press('Start editing region');
  await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Alice")','Child region lock');
  if(!await alice.evaluate('document.querySelector(".tool-page").textContent.includes("3D visibility")'))throw new Error('Visibility fields missing');
  await fs.mkdir(output,{recursive:true});
  const screenshot=await alice.command('Page.captureScreenshot',{format:'png'});await fs.writeFile(`${output}/uoas-regions.png`,Buffer.from(screenshot.result.data,'base64'));
  await alice.press('Release region');
  await select(bob,'Cellar');
  await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Available")','Child region released');
  await bob.press('Start editing region');await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Bob")','Bob owns child region');
  await bob.press('End session');await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Available")','Ending session releases region');
  await alice.command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:false});await sleep(300);
  if(!await alice.evaluate('document.documentElement.scrollWidth<=innerWidth'))throw new Error('Region page mobile overflow');
  await alice.press('End session');
  if(failures.length)throw new Error(failures.join('\n'));
  console.log('PASS: regions WASM, Skia volumes, Alice/Bob ID locks, foreign save disabled, JSON publish/refresh, visibility fields, session cleanup and mobile layout.');
}finally{await Promise.race([alice.command('Browser.close'),sleep(1000)]);alice.socket.close();bob.socket.close();}
