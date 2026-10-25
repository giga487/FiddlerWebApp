// Run against the local backend, frontend and a disposable Chromium CDP profile.
import fs from 'node:fs/promises';
import path from 'node:path';
const debugging = process.env.UOAS_CDP ?? 'http://127.0.0.1:9239';
const frontend = process.env.UOAS_FRONTEND ?? 'http://localhost:5178';
const output = process.env.UOAS_SCREENSHOT_DIR ?? '.';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const failures = [];
async function page() {
  const target = await (await fetch(`${debugging}/json/new?${encodeURIComponent(frontend + '/gump')}`, {method: 'PUT'})).json();
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(resolve => socket.addEventListener('open', resolve, {once:true}));
  let id = 0;
  const requests = new Map();
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (requests.has(message.id)) { requests.get(message.id)(message); requests.delete(message.id); }
    if (message.method === 'Runtime.exceptionThrown') failures.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
  });
  const command = (method, params={}) => new Promise(resolve => { const key=++id; requests.set(key, resolve); socket.send(JSON.stringify({id:key,method,params})); });
  const evaluate = async expression => {
    const result = await command('Runtime.evaluate', {expression,returnByValue:true,awaitPromise:true});
    if (result.result?.exceptionDetails) throw new Error(result.result.exceptionDetails.text);
    return result.result?.result.value;
  };
  const wait = async (expression, description, timeout=20000) => {
    const deadline=Date.now()+timeout;
    while (Date.now()<deadline) { if (await evaluate(expression)) return; await sleep(250); }
    throw new Error(`Timed out: ${description}. Body: ${await evaluate('document.body.innerText.slice(0,2000)')}`);
  };
  await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  return {command,evaluate,wait,socket};
}
const click = text => `Array.from(document.querySelectorAll('button')).find(button=>button.textContent.trim()===${JSON.stringify(text)})?.click()`;
async function start(client,name) {
  await client.wait('!!document.querySelector(".welcome form")', 'WASM startup');
  await client.evaluate(`(()=>{const input=document.querySelector('.welcome input');input.value=${JSON.stringify(name)};input.dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('.welcome form').requestSubmit();})()`);
  await client.wait('!!document.querySelector(".workspacebar")','collaboration session');
  await client.evaluate(click('Open library'));
  await client.wait('document.querySelectorAll(".asset-tile").length===24','paged catalog');
  await client.evaluate(`Array.from(document.querySelectorAll('.asset-tile')).find(button=>button.querySelector('strong')?.textContent==='0x000A')?.click()`);
  await client.wait('!!document.querySelector(".editor-panel .lock-status")','selected ID');
}
const alice=await page();
const bob=await page();
try {
  await start(alice,'Alice');
  await alice.evaluate(click('Start editing ID'));
  await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Locked by you (Alice)")','Alice lock');
  await start(bob,'Bob');
  await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Locked by Alice")','Bob sees Alice');
  if (!await bob.evaluate('document.querySelector("input[type=file]").disabled')) throw new Error('Foreign upload was enabled');
  await alice.wait('document.querySelectorAll(".skia-stage canvas").length>0','Skia canvas');
  const before=await alice.evaluate('document.querySelector(".editor-panel .lock-status").textContent');
  await alice.evaluate(click('Zoom +'));
  await sleep(200);
  if (before!==await alice.evaluate('document.querySelector(".editor-panel .lock-status").textContent')) throw new Error('Zoom altered lock state');
  await fs.mkdir(output,{recursive:true});
  for (const [client,name] of [[alice,'alice'],[bob,'bob']]) {
    const screenshot=await client.command('Page.captureScreenshot',{format:'png'});
    await fs.writeFile(`${output}/uoas-${name}.png`,Buffer.from(screenshot.result.data,'base64'));
  }
  await alice.evaluate(click('Release ID'));
  await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Available")','live lock release',10000);
  await bob.evaluate(click('Start editing ID'));
  await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Locked by you (Bob)")','Bob lock');
  await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Locked by Bob")','Alice sees Bob',10000);
  await bob.evaluate(click('End session'));
  await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Available")','session close releases ID',10000);
  async function navigate(path, heading, client=alice) { console.log(`Checking ${path}`);
    await client.evaluate(`(()=>{const a=document.createElement('a');a.href=${JSON.stringify(path)};document.body.append(a);a.click();a.remove();})()`);
    await client.wait(`document.querySelector('h1')?.textContent.includes(${JSON.stringify(heading)})`,heading);
  }
  await navigate('/colors','Fonts, hues');
  await alice.evaluate(click('Find available documents'));
  await alice.wait('document.querySelectorAll(".tool-fields select option").length>0','resource files');
  await alice.evaluate(`(()=>{const s=document.querySelector('.tool-fields select');s.value=Array.from(s.options).find(o=>o.textContent.includes('Hue')).value;s.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await alice.evaluate(click('Open document'));
  await alice.wait('document.querySelectorAll(".asset-grid button").length>0','hue records');
  await alice.evaluate('document.querySelector(".asset-grid button").click()');
  await alice.wait('document.querySelectorAll("input[type=color]").length===35','palette editor');
  await alice.evaluate(click('Start editing ID'));
  await alice.wait('!Array.from(document.querySelectorAll("button")).find(b=>b.textContent=== "Generate gradient")?.disabled','hue ownership');
  await alice.evaluate(click('Generate gradient'));
  await alice.evaluate(`(()=>{const labels=Array.from(document.querySelectorAll('label'));const s=labels.find(l=>l.textContent.startsWith('Resource')).querySelector('select');s.value='Gump';s.dispatchEvent(new Event('change',{bubbles:true}));const input=labels.find(l=>l.textContent==='ID').querySelector('input');input.value='10';input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await alice.evaluate(click('Add sample'));
  await alice.wait('document.querySelectorAll(".sample-grid canvas").length===1','local hue sample canvas');
  await alice.evaluate(click('Save record draft'));
  await alice.wait('document.querySelector(".tool-page")?.textContent.includes("1 drafts")','hue draft saved');
  await alice.evaluate(click('Publish document'));
  await alice.wait('document.querySelector(".notice.success")?.textContent.includes("Published document")','hue publication');
  await navigate('/gump/compare','Compare and copy');
  await alice.evaluate(`(()=>{const s=document.querySelector('.tool-fields select');s.value='default';s.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await alice.evaluate(click('Compare libraries'));
  await alice.wait('document.querySelector(".tool-page")?.textContent.includes("Comparison is a snapshot")','comparison snapshot');
  await alice.evaluate('document.querySelector(".tool-fields input[type=checkbox]").click()');
  await alice.wait('Array.from(document.querySelectorAll("td button")).some(b=>b.textContent.includes("0x000A"))','equal comparison rows');
  await alice.evaluate('Array.from(document.querySelectorAll("td button")).find(b=>b.textContent.includes("0x000A")).click()');
  await alice.wait('document.querySelector("input[type=number]")?.value==="10"','comparison selection');
  await alice.wait('!Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Lock destination ID")?.disabled','comparison preview loaded');
  await alice.evaluate(`(()=>{const input=document.querySelector('input[type=number]');input.value='11';input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await alice.wait('!Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Lock destination ID")?.disabled','destination preview loaded');
  await alice.evaluate(click('Lock destination ID'));
  await alice.wait('!Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Copy reference to draft")?.disabled','destination ownership');
  await alice.evaluate(click('Copy reference to draft'));
  await alice.wait('document.querySelector(".workspace-status")?.textContent.includes("1 drafts")','copied gump draft');
  await navigate('/gump','Graphics');
  await alice.evaluate(click('Publish new archive version'));
  // Publishing is offered by the selected ID inspector.
  await alice.evaluate(`Array.from(document.querySelectorAll('.asset-tile')).find(b=>b.querySelector('strong')?.textContent==='0x000B')?.click()`);
  await alice.wait('!!document.querySelector(".editor-panel .lock-status")','copied ID inspector');
  await alice.evaluate(click('Publish new archive version'));
  await alice.wait('document.querySelector(".workspace-status")?.textContent.includes("0 drafts")','copied gump publication');
  await navigate('/animations','Animation browser');
  await alice.evaluate(click('Find archives'));
  await alice.wait('document.querySelectorAll(".asset-grid button").length===1','animation body catalog');
  await alice.evaluate('document.querySelector(".asset-grid button").click()');
  await alice.wait('document.querySelectorAll(".skia-stage canvas").length===1','animation atlas canvas');
  await alice.evaluate(click('Next frame'));
  await navigate('/plugins','Server plugins');
  await alice.wait('document.querySelector(".tool-page")?.textContent.includes("SHA256")','plugin quarantine controls');
  const pluginDll=path.resolve('examples/Fiddler.Plugin.AssetInfo/bin/Debug/net10.0/Fiddler.Plugin.AssetInfo.dll');
  const dom=await alice.command('DOM.getDocument');
  const input=await alice.command('DOM.querySelector',{nodeId:dom.result.root.nodeId,selector:'input[type=file]'});
  await alice.command('DOM.setFileInputFiles',{nodeId:input.result.nodeId,files:[pluginDll]});
  await alice.wait('document.querySelector(".tool-page")?.textContent.includes("Quarantined · awaiting operator approval")','plugin upload quarantine');
  if(!await alice.evaluate('Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Activate")?.disabled')) throw new Error('Unapproved plugin activation was enabled');
  await alice.evaluate(click('Remove package'));
  await alice.wait('!document.querySelector(".package-hash")','quarantined package removal');
  await navigate('/configuration','Configuration');
  await alice.evaluate(click('Scan this library'));
  await alice.wait('document.querySelectorAll("tbody tr").length>0','inventory table');
  await navigate('/diagnostics','Diagnostics');
  await alice.wait('document.querySelectorAll("input[type=datetime-local]").length===2','diagnostic time filters');
  await alice.evaluate(click('Clear this view'));
  await navigate('/tiles','Land tile workshop');
  await alice.wait('document.querySelectorAll(".asset-tile").length>0','land tile catalog');
  if(process.env.UOAS_REGIONS==='1') {
    await navigate('/regions','Server regions');
    await alice.wait('document.querySelectorAll(".tool-fields select option").length>1','configured region source');
    await alice.evaluate(click('Open regions'));
    await alice.wait('Array.from(document.querySelectorAll("td button")).some(b=>b.textContent.trim()==="Harbor")','region catalog');
    await alice.evaluate('Array.from(document.querySelectorAll("td button")).find(b=>b.textContent.trim()==="Harbor").click()');
    await alice.wait('!!document.querySelector(".region-stage canvas")','Skia region boundaries');
    await alice.evaluate(click('Start editing region'));
    await alice.wait('document.querySelector(".lock-status")?.textContent.includes("Alice")','region owned by Alice');
    await start(bob,'Bob');
    await navigate('/regions','Server regions',bob);
    await bob.wait('document.querySelectorAll(".tool-fields select option").length>1','Bob region sources');
    await bob.evaluate(click('Open regions'));
    await bob.wait('Array.from(document.querySelectorAll("td button")).some(b=>b.textContent.trim()==="Harbor")','Bob region catalog');
    await bob.evaluate('Array.from(document.querySelectorAll("td button")).find(b=>b.textContent.trim()==="Harbor").click()');
    await bob.wait('document.querySelector(".lock-status")?.textContent.includes("Alice")','Bob sees region lock owner');
    if(!await bob.evaluate('Array.from(document.querySelectorAll("button")).find(b=>b.textContent.trim()==="Save region draft")?.disabled')) throw new Error('Foreign region save was enabled');
    await alice.evaluate(`(()=>{const field=Array.from(document.querySelectorAll('label')).find(l=>l.textContent==='Priority').querySelector('input');field.value='91';field.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await alice.evaluate(click('Save region draft'));
    await alice.wait('document.querySelector(".tool-page")?.textContent.includes("1 drafts")','saved region draft');
    await alice.evaluate(click('Publish regions'));
    await alice.wait('document.querySelector(".notice.success")?.textContent.includes("Published region revision")','published compatible regions.json');
    await bob.evaluate(click('Refresh published regions'));
    await bob.wait('Array.from(document.querySelectorAll("td")).some(td=>td.textContent.includes("priority 91"))','Bob sees published region fields');
    await alice.evaluate('Array.from(document.querySelectorAll("td button")).find(b=>b.textContent.trim()==="Harbor").click()');
    await alice.wait('!!document.querySelector(".region-stage canvas")','published region preview');
    const capture=await alice.command('Page.captureScreenshot',{format:'png'});
    await fs.writeFile(`${output}/uoas-regions.png`,Buffer.from(capture.result.data,'base64'));
    await bob.evaluate(click('End session'));
    console.log('PASS: region boundaries, named ID lock, blocked foreign save and merged JSON publication.');
    await navigate('/tiles','Land tile workshop');
  }
  await alice.command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:false});
  await sleep(300);
  if (!await alice.evaluate('document.documentElement.scrollWidth<=innerWidth')) throw new Error('Mobile horizontal overflow');
  await alice.evaluate(click('End session'));
  if (failures.length) throw new Error(failures.join('\n'));
  console.log('PASS: WASM sessions, Skia, locks, hue board/publication, Gump comparison/copy/publication, animation catalog, plugins, inventory, diagnostics, land tiles and mobile layout.');
} finally {
  if(process.env.UOAS_KEEP_BROWSER !== '1') await Promise.race([alice.command('Browser.close'), sleep(1000)]);
  alice.socket.close(); bob.socket.close();
}
