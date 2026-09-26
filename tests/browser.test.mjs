// Usage: PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs CHROME_PATH=/path/to/chrome node tests/browser.test.mjs /path/to/stage-manager
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, basename } from 'node:path';
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const app = resolve(process.argv[2]);
const root = resolve(import.meta.dirname, '..');
let lastPacket, packageCount = 0;
const scope = {territory:339,world:21,ward:0,plot:0,room:0,houseId:'123',instance:0};
let snapshot = {position:{x:0,y:0,z:0},yaw:0,scope,name:'Alice Actor'};
const server = createServer(async(req,res)=>{
  try {
    const name = basename(new URL(req.url,'http://localhost').pathname);
    if (name === 'db.js') {
      const source = await readFile(resolve(app, 'app.js'), 'utf8');
      const imports = source.match(/import \{([\s\S]*?)\} from '\.\/db\.js(?:\?[^']*)?';/)[1].split(',').map(s => s.trim()).filter(Boolean);
      res.setHeader('Content-Type', 'text/javascript');
      res.end('export const supabaseClient = {};\n' + imports.map(name => `export async function ${name}(){ return ${name === 'getSession' ? 'null' : '[]'}; }`).join('\n'));
      return;
    }
    const file = name === 'harness' ? resolve(root,'tests/browser-harness.html') : resolve(app,name);
    if (!['harness','index.html','app.js','app.css','stage.js','player.js','sync.js','media-cache.js','game-bridge.js','game-model.js','game-bridge.css'].includes(name)) {res.writeHead(404);res.end();return;}
    res.setHeader('Content-Type',name==='harness'||name.endsWith('.html')?'text/html':name.endsWith('.css')?'text/css':'text/javascript');
    res.end(await readFile(file));
  } catch {res.writeHead(500);res.end();}
});
const bridge = createServer(async(req,res)=>{
  res.setHeader('Access-Control-Allow-Origin','http://127.0.0.1:17846');
  res.setHeader('Access-Control-Allow-Headers','Authorization, Content-Type');
  res.setHeader('Access-Control-Allow-Methods','POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Private-Network','true');
  if(req.method==='OPTIONS'){res.writeHead(200);res.end();return;}
  const chunks=[];for await(const chunk of req)chunks.push(chunk);
  const body=JSON.parse(Buffer.concat(chunks));
  if(req.url==='/state'){lastPacket=body;if(body.package)packageCount++;}
  res.setHeader('Content-Type','application/json');res.end(JSON.stringify({ok:true,message:'Connected',snapshot}));
});
await new Promise(r=>server.listen(17846,'127.0.0.1',r));
await new Promise(r=>bridge.listen(17845,'127.0.0.1',r));
const browser = await chromium.launch({headless:true,...(process.env.CHROME_PATH?{executablePath:process.env.CHROME_PATH}:{})});
try {
  const page=await browser.newPage({viewport:{width:1440,height:1000}}), errors=[];
  page.on('pageerror',e=>{errors.push(e.message);console.error('Page error:',e.message);});
  await page.route('**/*',route=>route.request().url().startsWith('http://127.0.0.1:')?route.continue():route.abort());
  await page.goto('http://127.0.0.1:17846/harness');
  assert.deepEqual(errors, [], 'harness initializes');
  await page.getByRole('button',{name:'In-game rehearsal',exact:true}).click();
  await page.locator('[data-token]').fill('demo-token');
  await page.locator('[data-connect]').click();
  await page.locator('[data-status]').filter({hasText:'Connected'}).waitFor();
  await page.getByText('2 · Calibrate the stage',{exact:true}).click();
  const captures=page.locator('[data-capture]');
  await captures.nth(0).click(); await page.locator('[data-result]').nth(0).filter({hasText:'XYZ'}).waitFor();
  snapshot={...snapshot,position:{x:20,y:0,z:0}}; await captures.nth(1).click(); await page.locator('[data-result]').nth(1).filter({hasText:'XYZ'}).waitFor();
  snapshot={...snapshot,position:{x:0,y:0,z:10}}; await captures.nth(2).click(); await page.locator('[data-result]').nth(2).filter({hasText:'XYZ'}).waitFor();
  await page.locator('[data-save-venue]').click();
  await page.locator('[data-venue-status]').filter({hasText:'Using Main stage'}).waitFor();
  await page.getByText('2 · Calibrate the stage',{exact:true}).click();
  await page.locator('[data-height]').fill('1.5');await page.locator('[data-heading]').fill('90');await page.locator('[data-emote]').fill('/beesknees');
  await page.locator('[data-save-actor]').click();
  await page.waitForFunction(()=>window.fixture.ctx.slides[0].stage_data.cast_positions[0].game?.emote==='beesknees');
  await new Promise(r=>setTimeout(r,400));
  assert.equal(lastPacket.transport.songId,'demo');assert.ok(packageCount>=1);
  snapshot={...snapshot,position:{x:5,y:2,z:5},yaw:Math.PI/2};
  await page.locator('[data-record]').click();
  await page.waitForFunction(()=>window.fixture.ctx.slides[0].stage_data.cast_positions[0].game?.height===2);
  const pos=await page.evaluate(()=>window.fixture.ctx.slides[0].stage_data.cast_positions[0]);
  assert.equal(pos.x,400);assert.equal(pos.y,350);assert.ok(Math.abs(pos.game.heading-90)<.001);assert.equal(pos.game.emote,'beesknees');
  await page.getByText('4 · Preview and share',{exact:true}).click();
  const downloadPromise=page.waitForEvent('download');await page.locator('[data-export]').click();const download=await downloadPromise;
  await mkdir(resolve(root,'artifacts'),{recursive:true});await download.saveAs(resolve(root,'artifacts/browser-export.stage.json'));
  const exported=JSON.parse(await readFile(resolve(root,'artifacts/browser-export.stage.json')));
  assert.equal(exported.slides[0].positions[0].y,2);assert.equal(exported.cues[0].kind,'preview');
  await page.getByText('1 · Connect this PC',{exact:true}).click();
  await page.screenshot({path:resolve(root,'artifacts/web-prototype.png'),fullPage:true});
  await page.locator('#next').click();await page.locator('[data-slide]').filter({hasText:'Chorus'}).waitFor();
  assert.equal(await page.locator('[data-height]').inputValue(),'0');
  assert.equal(await page.locator('[data-emote]').inputValue(),'');
  // The actual application must also initialize when there is no logged-in user.
  // Only its database module is stubbed; no production endpoint is contacted.
  await page.goto('http://127.0.0.1:17846/index.html');
  await page.locator('#view-login.active').waitFor();
  assert.equal(await page.locator('.game-open').count(), 1);
  assert.deepEqual(errors,[]);
  console.log('PASS browser: pairing, calibration, actor cue save, position capture, JSON export, slide switching, actual app startup, no page errors');
} finally {await browser.close();server.close();bridge.close();}
