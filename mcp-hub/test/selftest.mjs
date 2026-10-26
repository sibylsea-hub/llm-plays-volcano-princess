// hub 的自测：临时 data 文件夹 + 假插件（答 inbox.jsonl 里的命令）+ 临时聊天服务（另一个端口），
// 用 MCP SDK 的客户端连 hub，列工具、调命令、看图有没有跟着回来、说的话有没有进面板；
// 再从文字信箱（临时 data 下的 selfplay/mailbox/）走一遍，试一遍交班（同一时间只有一个在玩）；最后看聊天服务 --hub 会不会顺手起 hub。不碰 8740、8741，不碰真游戏。
// 人的名字默认是 user（LUDUS_USER_NAME 没设）；另有几个 hub 设了名字，看说明、信箱的 README 里是不是跟着换了。
//
//   node test/selftest.mjs        （在 mcp-hub/ 里跑；--keep 留下临时文件夹）
import fs from 'node:fs';
import os from 'node:os';
import net from 'node:net';
import path from 'node:path';
import zlib from 'node:zlib';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

const HUB_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ROOT = path.resolve(HUB_DIR, '..');
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'ludus-hub-test-'));
const DATA = path.join(TMP, 'data');
const SP = path.join(DATA, 'selfplay');
const SCENES = path.join(SP, 'scenes');
fs.mkdirSync(SCENES, { recursive: true });

const children = [];
const timers = [];
let autoHubPid = null;  // 聊天服务 --hub 起的 hub 是单独跑的，最后自己停
let failed = 0;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function check(ok, what, detail = '') {
  console.log(`${ok ? '  ok ' : 'FAIL '} ${what}${!ok && detail ? `\n       ${String(detail).slice(0, 600).replace(/\n/g, '\n       ')}` : ''}`);
  if (!ok) failed++;
}

function freePort() {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

// ---- 假图 ----
function png(w, h, seed) {
  const row = w * 3 + 1;
  const raw = Buffer.alloc(row * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = y * row + 1 + x * 3;
      raw[i] = (x * 255 / w) | 0; raw[i + 1] = (y * 255 / h) | 0; raw[i + 2] = seed;
    }
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body));
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

function jpegSize(buf) {
  for (let i = 2; i < buf.length - 9;) {
    if (buf[i] !== 0xff) return null;
    const marker = buf[i + 1];
    if (marker >= 0xc0 && marker <= 0xc3) return { h: buf.readUInt16BE(i + 5), w: buf.readUInt16BE(i + 7) };
    i += 2 + buf.readUInt16BE(i + 2);
  }
  return null;
}

const scene = (n) => path.join(SCENES, `${String(n).padStart(4, '0')}.png`);
fs.writeFileSync(scene(1), png(320, 180, 10));    // look 名字：绝对路径
fs.writeFileSync(scene(2), png(320, 180, 20));    // look 名字：相对路径
fs.writeFileSync(scene(3), png(320, 180, 30));    // 换场景那行：不该发
fs.writeFileSync(scene(4), png(2400, 1350, 40));  // 第一次见的地方：大图，该缩成 JPEG
fs.writeFileSync(scene(5), png(640, 360, 50));    // shot
fs.writeFileSync(scene(6), png(320, 180, 70));    // CG：也该发（以前只认「第一次见」）
fs.writeFileSync(path.join(TMP, 'outside.png'), png(64, 64, 60));  // selfplay 外面的：不该发

// ---- 假插件：写 state.json，答 inbox.jsonl ----
const localIso = () => {
  const d = new Date();
  const p = (n, w = 2) => String(n).padStart(w, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
};
const game = { phase: 'click', why: '等着点按钮' };
function writeState() {
  const state = {
    ts: localIso(), phase: game.phase, why: game.why, snap: 7, hidden: 0,
    status: { turn: 38, year: '冬火年', month: 9, coin: 6930, energy: 13, maxEnergy: 13, favor: 4, favorLevel: 4, favorNext: 200,
      darkness: 0, inspiration: 3, dauName: '露丝', faName: '伍柏', height: 160, fame: 300, mood: 100, lover: 2,
      nature: { 体魄: 900, 智力: 1000 }, inAttri: { 力量: 50 } },
    view: '在：家（女儿房间）',
    items: [{ n: 1, label: '见女儿', kind: '按钮' }],
    menu: [],
  };
  fs.writeFileSync(path.join(SP, 'state.json.tmp'), JSON.stringify(state));
  fs.renameSync(path.join(SP, 'state.json.tmp'), path.join(SP, 'state.json'));
}
writeState();
timers.push(setInterval(writeState, 1000));
fs.writeFileSync(path.join(SP, 'events.jsonl'), JSON.stringify({ ts: localIso(), type: 'line', who: '露丝', say: '早上好' }) + '\n');

const REPLIES = {
  look: () => ({ ok: true, text: `在：广场\n[第一次见] 克莱尔（图：${scene(1)}）\n[第一次见] 本森（图：scenes/0002.png）\n不相干的图：${path.join(TMP, 'outside.png')}` }),
  lookname: () => REPLIES.look(),
  acts: () => ({ ok: true, text: `== 换场景：家 → 广场 ==（画面：${scene(3)}）\n[第一次来] 广场（图：scenes\\0004.png；user 那边也看得到）\n[CG] 猫头鹰送信（图：${scene(6)}；user 那边也看得到）\n家：玩具积木.搭积木（行动力 2）` }),
  shot: () => ({ ok: true, path: scene(5), snap: 7, view: '在：家', items: [{ n: 1, label: '见女儿', kind: '按钮' }] }),
  click: () => ({ ok: true, text: '点了 1 号 见女儿' }),
  hello: () => ({ ok: true, text: '换了个人来玩：见过的东西清空了' }),
  find: (req) => ({ ok: false, text: `（假的）这儿找不到「${req.text}」` }),
};
let inboxOffset = 0;
const handled = [];
timers.push(setInterval(() => {
  const inbox = path.join(SP, 'inbox.jsonl');
  if (!fs.existsSync(inbox)) return;
  const all = fs.readFileSync(inbox);
  const fresh = all.subarray(inboxOffset, all.lastIndexOf(0x0a) + 1);
  inboxOffset += fresh.length;
  for (const line of fresh.toString('utf8').split('\n').filter(Boolean)) {
    const req = JSON.parse(line);
    handled.push(req.cmd);
    const reply = REPLIES[req.cmd]?.(req) ?? { ok: false, text: `假插件不认 ${req.cmd}` };
    fs.appendFileSync(path.join(SP, 'outbox.jsonl'), JSON.stringify({ id: req.id, kind: 'reply', ...reply }) + '\n');
  }
}, 100));

// ---- 子进程 ----
// 名字照默认的来（user / agent），这台电脑上设了也不算；要测名字的地方自己传
const BASE_ENV = { ...process.env };
delete BASE_ENV.LUDUS_USER_NAME;
delete BASE_ENV.LUDUS_AGENT_NAME;
function start(args, env = {}, name = 'child') {
  const child = spawn(process.execPath, args, { cwd: HUB_DIR, env: { ...BASE_ENV, ...env }, windowsHide: true });
  let out = '';
  child.stdout.on('data', (b) => { out += b; });
  child.stderr.on('data', (b) => { out += b; });
  child.output = () => out;
  child.name = name;
  children.push(child);
  return child;
}

async function until(url, ms = 10_000) {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    try { const r = await fetch(url); if (r.status < 500) return true; } catch {}
    await sleep(150);
  }
  return false;
}

async function connect(url, token) {
  const client = new Client({ name: 'ludus-selftest', version: '0.0.0' });
  const transport = new StreamableHTTPClientTransport(new URL(url), {
    requestInit: token ? { headers: { Authorization: `Bearer ${token}` } } : undefined,
  });
  await client.connect(transport);
  return client;
}

const textOf = (r) => r.content.filter((c) => c.type === 'text').map((c) => c.text).join('\n');
const imagesOf = (r) => r.content.filter((c) => c.type === 'image');
const call = (client, name, args = {}) => client.callTool({ name, arguments: args }, undefined, { timeout: 120_000 });
const chatLines = () => fs.readFileSync(path.join(DATA, 'chat.jsonl'), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));

try {
  const CHAT_PORT = await freePort();
  const HUB_PORT = await freePort();
  console.log(`临时文件夹 ${TMP}\n聊天服务 127.0.0.1:${CHAT_PORT}，hub 127.0.0.1:${HUB_PORT}`);

  start([path.join(ROOT, 'chat-service', 'server.mjs')], { LUDUS_PORT: String(CHAT_PORT), LUDUS_DATA: DATA }, 'chat');
  check(await until(`http://127.0.0.1:${CHAT_PORT}/api/history`), '临时聊天服务起来了');

  const limits = { LUDUS_HUB_CALL_LIMIT_MS: '8000', LUDUS_HUB_WAIT_SECONDS: '3' };
  const hubProc = start(['hub.mjs', '--port', String(HUB_PORT), '--data', DATA, '--chat-port', String(CHAT_PORT)], limits, 'hub');
  check(await until(`http://127.0.0.1:${HUB_PORT}/health`), 'hub 起来了');
  const MCP = `http://127.0.0.1:${HUB_PORT}/mcp`;

  // ---- 门口 ----
  const bad = await new Promise((resolve) => {
    const s = net.connect(HUB_PORT, '127.0.0.1', () => s.write(`GET /health HTTP/1.1\r\nHost: evil.example:${HUB_PORT}\r\nConnection: close\r\n\r\n`));
    let buf = ''; s.on('data', (b) => { buf += b; }); s.on('end', () => resolve(buf));
  });
  check(/^HTTP\/1\.1 421/.test(bad), '别的 Host 进不来（421）', bad.split('\r\n')[0]);
  const fromPage = await fetch(MCP, { method: 'POST', headers: { Origin: 'https://evil.example', 'Content-Type': 'application/json' }, body: '{}' });
  check(fromPage.status === 403, '网页来的请求（带 Origin）进不来（403）', fromPage.status);

  // ---- 列工具、说明 ----
  const client = await connect(MCP);
  const { tools } = await client.listTools();
  const names = tools.map((t) => t.name);
  console.log(`  工具：${names.join(' ')}`);
  for (const n of ['wait', 'status', 'look', 'acts', 'plan', 'do', 'go', 'choose', 'click', 'schedule', 'shot', 'skill', 'play', 'say', 'panel']) {
    check(names.includes(n), `有 ${n}`);
  }
  check(tools.every((t) => /^[a-zA-Z0-9_-]{1,64}$/.test(t.name)), '工具名都是英文（各家客户端都认）');
  const instructions = client.getInstructions() || '';
  check(instructions.includes('say') && instructions.includes('面板'), 'instructions 里写了用 say 说话、面板', instructions);
  check(instructions.includes('名字还是占位的') && instructions.includes('LUDUS_USER_NAME') && instructions.includes('LUDUS_AGENT_NAME'),
    '名字：没设名字时，说明里提醒 agent 问问人、把两个名字设上', instructions);
  check(/先问 user。$/.test(instructions), '名字：安全那句还在说明的最后', instructions.slice(-200));

  // ---- status ----
  let r = await call(client, 'status');
  check(textOf(r).includes('露丝') && textOf(r).includes('冬火年'), 'status 回了状态', textOf(r));

  // ---- user 在面板上说的话，附在游戏回话后面 ----
  await fetch(`http://127.0.0.1:${CHAT_PORT}/api/send`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '先去找莉莉娅吧' }) });
  r = await call(client, 'look', {});
  let t = textOf(r);
  check(t.includes('user 在面板上说') && t.includes('先去找莉莉娅吧'), 'look 后面带上了 user 刚在面板上说的话', t);
  check(imagesOf(r).length === 2, 'look：两张图（绝对路径、相对路径各一张），selfplay 外面的不发', `${imagesOf(r).length} 张\n${t}`);
  const small = [fs.readFileSync(scene(1)), fs.readFileSync(scene(2))];
  check(imagesOf(r).every((i) => {
    const b = Buffer.from(i.data, 'base64');
    return i.mimeType === 'image/png' && small.some((s) => s.equals(b));
  }), 'look：小图原样发');
  r = await call(client, 'status');
  check(!textOf(r).includes('先去找莉莉娅吧'), '同一句不再带第二次', textOf(r));

  // ---- acts：只发「第一次」那几行的图（地方、CG 都算），大图缩成 JPEG ----
  r = await call(client, 'acts');
  const imgs = imagesOf(r);
  check(imgs.length === 2, 'acts：发了第一次来的地方和 CG 两张，换场景那张没发', `${imgs.length} 张\n${textOf(r)}`);
  if (imgs[0]) {
    const size = jpegSize(Buffer.from(imgs[0].data, 'base64'));
    check(imgs[0].mimeType === 'image/jpeg' && size && Math.max(size.w, size.h) <= 1568, `大图缩成了 JPEG（${size ? `${size.w}×${size.h}` : '?'}）`, imgs[0].mimeType);
  }

  // ---- 少了必填参数：直接说少了哪个，不拼成「undefined」去问游戏 ----
  r = await call(client, 'do', {});
  check(r.isError && textOf(r).includes('少了参数') && textOf(r).includes('target') && !textOf(r).includes('undefined'), 'do 少了 target：说少了哪个参数', textOf(r));

  // ---- shot ----
  r = await call(client, 'shot');
  check(textOf(r).includes('画面：') && imagesOf(r).length === 1, 'shot：图跟着回来了', textOf(r));

  // ---- 通用的 play ----
  r = await call(client, 'play', { command: 'log -n 5' });
  check(textOf(r).includes('早上好'), 'play "log -n 5"', textOf(r));
  r = await call(client, 'play', { command: 'help' });
  check(textOf(r).includes('vp_player'), 'play help 给出全部命令', textOf(r).slice(0, 200));
  r = await call(client, 'play', { command: 'look --data C:/' });
  check(textOf(r).includes('不能换 data'), 'play 不让换 data 文件夹', textOf(r));
  r = await call(client, 'play', { command: 'wish' });
  check(textOf(r).includes('假插件不认 wish'), 'play 把插件的回话原样带回来', textOf(r));

  // ---- say：进了临时的面板 ----
  r = await call(client, 'say', { text: '露丝今天好有精神' });
  check(textOf(r).includes('说出去了'), 'say 回了「说出去了」', textOf(r));
  let last = chatLines().at(-1);
  check(last.from === 'agent' && last.text === '露丝今天好有精神', 'say 的话写进了临时 chat.jsonl', JSON.stringify(last));
  check(last.who === 'hub：ludus-selftest', 'say 的话记着是谁说的（面板气泡上显示）', JSON.stringify(last));
  r = await call(client, 'play', { command: 'say 从 play 说的' });
  last = chatLines().at(-1);
  check(last.text === '从 play 说的', 'play "say …" 也走 hub 的 say（不会发到 8740）', JSON.stringify(last));
  r = await call(client, 'panel', { n: 5 });
  check(textOf(r).includes('user：先去找莉莉娅吧') && textOf(r).includes('你：露丝今天好有精神'), 'panel 翻得到前面的话', textOf(r));

  // ---- feedback：vp_player 还没接上时直接调 vp_memory.py，写进临时 data ----
  if (names.includes('feedback')) {
    r = await call(client, 'feedback', { text: '战斗读起来很顺' });
    const fb = path.join(SP, 'feedback.jsonl');
    const saved = fs.existsSync(fb) ? fs.readFileSync(fb, 'utf8') : '';
    check(saved.includes('战斗读起来很顺'), `feedback 记进了临时的 feedback.jsonl（回话：${textOf(r).split('\n')[0]}）`, textOf(r));
  } else {
    console.log('  （没有 feedback：vp_memory.py 还没有这条命令，跳过）');
  }

  // ---- wait：没轮到就说再等一下 ----
  game.phase = 'busy'; game.why = '对话在播'; writeState();
  let t0 = Date.now();
  r = await call(client, 'wait');
  check(textOf(r).includes('还没轮到') && Date.now() - t0 < 8000, `wait 没轮到时 ${((Date.now() - t0) / 1000).toFixed(1)} 秒就回来说再等一下`, textOf(r));

  // ---- 走得久的命令：到点先回话，进程留在后台，下一次 wait 拿结果 ----
  t0 = Date.now();
  r = await call(client, 'click', { button: '1' });
  const cut = Date.now() - t0;
  check(textOf(r).includes('点了 1 号') && textOf(r).includes('还没轮到，再等一下') && cut < 10_000, `click 后游戏一直在走：${(cut / 1000).toFixed(1)} 秒先回话`, textOf(r));
  r = await call(client, 'status');
  check(textOf(r).includes('还没走完'), '上一步还在走时，别的命令先说「还没走完」', textOf(r));
  fs.appendFileSync(path.join(SP, 'events.jsonl'), JSON.stringify({ ts: localIso(), type: 'line', who: '露丝', say: '爸爸！' }) + '\n');
  game.phase = 'click'; game.why = '等着点按钮'; writeState();
  r = await call(client, 'wait');
  check(textOf(r).includes('爸爸！') && textOf(r).includes('在：家'), '接着 wait 拿到了 click 后来的结果', textOf(r));
  r = await call(client, 'status');
  check(!textOf(r).includes('还没走完') && textOf(r).includes('露丝'), '之后的命令照常', textOf(r));
  check(handled.includes('hello'), 'MCP 会话开头发了 hello（vp_player 的命令表里有中文别名也认得出）', handled.join(' '));
  await client.close();

  // ---- 文字信箱 ----
  const MB = path.join(SP, 'mailbox');
  const readme = path.join(SP, 'README.txt');
  check(fs.existsSync(readme) && fs.readFileSync(readme, 'utf8').includes('文字信箱'), '给客户端的说明复制到了 selfplay/README.txt');
  const readmeText = fs.existsSync(readme) ? fs.readFileSync(readme, 'utf8') : '';
  check(!readmeText.includes('{{') && readmeText.includes(MB) && readmeText.includes(ROOT) && readmeText.includes(`127.0.0.1:${HUB_PORT}/mcp`)
    && readmeText.includes('来和 user 一起玩'),
  'README.txt 里的占位符都填上了：信箱、仓库的绝对路径，hub 的地址，名字是默认的 user', readmeText.slice(0, 600));
  check(fs.existsSync(path.join(MB, 'in')) && fs.existsSync(path.join(MB, 'out')), '信箱的 in/、out/ 建好了');
  const presence = async () => (await (await fetch(`http://127.0.0.1:${CHAT_PORT}/api/presence`)).json()).map((p) => p.label);
  /** 等 out/ 里出现回话 */
  async function outOf(name, ms = 30_000) {
    const out = path.join(MB, 'out', `${name}.txt`);
    const deadline = Date.now() + ms;
    while (Date.now() < deadline) {
      if (fs.existsSync(out)) return fs.readFileSync(out, 'utf8');
      await sleep(100);
    }
    return `（${ms / 1000} 秒没等到 out/${name}.txt）`;
  }
  let mailNo = 0;
  /** 往 in/ 写一个文件，等 out/ 里出现同名的回话 */
  async function mail(body, { name = `m${String(++mailNo).padStart(3, '0')}` } = {}) {
    fs.writeFileSync(path.join(MB, 'in', `${name}.txt`), body);
    return outOf(name);
  }
  const helloCount = () => handled.filter((c) => c === 'hello').length;

  const hellosBefore = helloCount();
  let m = await mail('# 测试客户端\nhello\n');
  check(m.includes('见过的东西清空了') && !fs.existsSync(path.join(MB, 'in', 'm001.txt')), '信箱：hello 跑了，in 里的文件拿走了', m);
  check(helloCount() === hellosBefore + 1, `信箱：只有客户端自己写的那一次 hello（${helloCount() - hellosBefore} 次）`);
  check((await presence()).includes('测试客户端'), '信箱：报到用的是「# 名字」那一行', JSON.stringify(await presence()));

  await fetch(`http://127.0.0.1:${CHAT_PORT}/api/send`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '信箱那边看得到吗' }) });
  m = await mail('# 测试客户端\nwait\n');
  check(m.includes('露丝') && m.includes('在：家'), '信箱：wait 回了现在的状况和能做什么', m);
  check(/-- user 在面板上说 --[\s\S]*信箱那边看得到吗\s*$/.test(m), '信箱：user 在面板上说的话在最后', m);

  m = await mail('# 测试客户端\ndo 书柜.读书');  // 没有换行结尾也行
  check(m.includes('找不到「书柜.读书」'), '信箱：do 书柜.读书 原样交给了 vp_player', m);

  m = await mail('# 测试客户端\nsay 信箱里说的，带"引号"也照样\n');
  last = chatLines().at(-1);
  check(m.includes('说出去了') && last.text === '信箱里说的，带"引号"也照样', '信箱：say 进了临时面板，话原样', `${m}\n${JSON.stringify(last)}`);
  check(last.who === '测试客户端', '信箱：say 的话记着是谁说的', JSON.stringify(last));
  m = await mail('status\n');
  check((await presence()).includes('文字信箱'), '信箱：没写名字就叫「文字信箱」', JSON.stringify(await presence()));

  m = await mail('# 测试客户端\nlook\n');
  check(m.includes(scene(1)) && m.includes(scene(2)) && !m.includes(`\n  ${path.join(TMP, 'outside.png')}`),
    '信箱：look 的图写成了绝对路径（相对路径的也是），selfplay 外面的不写', m);
  m = await mail('# 测试客户端\nacts\n');
  const jpg = /\n {2}(\S+\.jpg)\n/.exec(m)?.[1];
  check(jpg && fs.existsSync(jpg) && !m.includes(`\n  ${scene(3)}`), `信箱：大图写的是缩小过的 JPEG（${jpg}），换场景那张不写`, m);

  m = await mail('# 测试客户端\nsay 一\nwait\n');
  check(m.includes('只放一行命令') && chatLines().at(-1).text !== '一', '信箱：一个文件两行命令，一行都不跑', m);

  // 写到一半（别的机器写过来、慢慢写）：不能拿半个文件
  fs.writeFileSync(path.join(MB, 'in', 'slow.txt'), '# 测试客户端\n');
  await sleep(200);
  fs.appendFileSync(path.join(MB, 'in', 'slow.txt'), 'say 慢慢写完的\n');
  m = await outOf('slow');
  check(m.includes('说出去了') && chatLines().some((l) => l.text === '慢慢写完的'), '信箱：写到一半的文件等写完才拿', m);
  m = await mail('# 测试客户端\npanel 3\n');
  check(m.includes('你：慢慢写完的'), '信箱：panel 翻得到面板上的话', m);

  fs.writeFileSync(path.join(MB, 'in', 'still-writing.txt.tmp'), 'wait\n');
  await sleep(800);
  check(fs.existsSync(path.join(MB, 'in', 'still-writing.txt.tmp')) && !fs.readdirSync(path.join(MB, 'out')).some((n) => n.startsWith('still-writing')), '信箱：.tmp 临时文件不拿');
  fs.unlinkSync(path.join(MB, 'in', 'still-writing.txt.tmp'));

  // 45 秒（测试里 8 秒）没走完：先回话，最后一句叫再写一个 wait；下一个 wait 拿到后来的结果
  game.phase = 'busy'; game.why = '对话在播'; writeState();
  t0 = Date.now();
  // 跑着的时候：out/ 里有「在跑」标记（客户端列目录就知道），还没有回话；回话写好以后标记删掉
  fs.writeFileSync(path.join(MB, 'in', 'run1.txt'), '# 测试客户端\nclick 1\n');
  const runMark = path.join(MB, 'out', 'run1.running');
  let running = null;
  for (let i = 0; i < 100 && running === null; i++) {
    if (fs.existsSync(runMark)) running = fs.readFileSync(runMark, 'utf8'); else await sleep(100);
  }
  check(running?.includes('click 1') && running.includes('开始') && !fs.existsSync(path.join(MB, 'out', 'run1.txt')),
    '信箱：跑着的时候 out 里有 run1.running（写着命令和开始的时间），还没有 run1.txt', running);
  m = await outOf('run1');
  check(!fs.existsSync(runMark), '信箱：回话写好以后，在跑标记删了');
  const lastLine = m.trim().split('\n').at(-1);
  check(m.includes('点了 1 号') && lastLine.includes('还没轮到') && lastLine.includes('再写一个 wait') && Date.now() - t0 < 12_000,
    `信箱：click 后游戏一直在走，${((Date.now() - t0) / 1000).toFixed(1)} 秒先回话，最后一句是「再写一个 wait」`, m);
  m = await mail('# 测试客户端\nstatus\n');
  check(m.includes('还没走完') && m.includes('再写一个 wait'), '信箱：上一步还在走时，别的命令先说「还没走完，再写一个 wait」', m);
  fs.appendFileSync(path.join(SP, 'events.jsonl'), JSON.stringify({ ts: localIso(), type: 'line', who: '露丝', say: '信箱里的爸爸！' }) + '\n');
  game.phase = 'click'; game.why = '等着点按钮'; writeState();
  m = await mail('# 测试客户端\nwait\n');
  check(m.includes('信箱里的爸爸！'), '信箱：接着写 wait 拿到了 click 后来的结果', m);

  // out/ 只留最近的几十个（旧的在跑标记也算在里面）
  const old = Date.now() / 1000 - 3600;
  for (let i = 0; i < 60; i++) {
    const f = path.join(MB, 'out', `old-${i}.${i % 2 ? 'running' : 'txt'}`);
    fs.writeFileSync(f, '旧的');
    fs.utimesSync(f, old, old);
  }
  m = await mail('# 测试客户端\nstatus\n', { name: 'newest' });
  await sleep(200);
  const outs = fs.readdirSync(path.join(MB, 'out'));
  check(outs.length <= 50 && outs.includes('newest.txt'), `信箱：out/ 清到只剩 ${outs.length} 个（.running 也算），新的还在`);

  // ---- 交班：同一时间只有一个在玩，hello 接手，旧的歇着----
  const playing = async () => (await (await fetch(`http://127.0.0.1:${CHAT_PORT}/api/presence`)).json()).find((p) => p.active)?.label ?? null;
  check(await playing() === '测试客户端', `交班：测试客户端动过游戏，算它在玩（${await playing()}）`);
  m = await mail('# 新来的\nwait\n');
  check(m.includes('测试客户端正在玩，你先歇着') && m.includes('要接手就写一个 hello') && !m.includes('露丝'),
    '交班：别人在玩时，没 hello 的新来的动游戏，不跑，说一句谁在玩', m);
  m = await mail('# 新来的\nstatus\n');
  check(m.includes('露丝') && await playing() === '测试客户端', '交班：只看的命令（status）谁都能跑，也不接手', m);
  const clicksBefore = handled.filter((c) => c === 'click').length;
  m = await mail('# 新来的\nhello\n');
  check(m.includes('见过的东西清空了') && await playing() === '新来的', '交班：新来的 hello 就接手了', `${m}\n${await playing()}`);
  m = await mail('# 测试客户端\nclick 1\n');
  check(m.includes('新来的接手了，你先歇着') && m.includes('要接回来就写一个 hello') && handled.filter((c) => c === 'click').length === clicksBefore,
    '交班：被接手的那个再动游戏，不跑（假插件没收到 click），说一句谁接手了', m);
  const linesBefore = chatLines().length;
  m = await mail('# 测试客户端\nsay 我还想说一句\n');
  check(m.includes('新来的接手了') && m.includes('没进面板') && chatLines().length === linesBefore, '交班：被接手的那个 say 不进面板', m);
  await fetch(`http://127.0.0.1:${CHAT_PORT}/api/send`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '这句是对新来的说的' }) });
  m = await mail('# 测试客户端\nstatus\n');
  check(!m.includes('这句是对新来的说的'), '交班：面板上的话不给歇着的那个', m);
  m = await mail('# 新来的\nwait\n');
  check(m.includes('这句是对新来的说的'), '交班：面板上的话只给在玩的那个', m);
  m = await mail('# 新来的\nsay 我接着玩啦\n');
  last = chatLines().at(-1);
  check(last.who === '新来的' && last.client === 'mailbox:新来的', '交班：在玩的那个说的话记着是它', JSON.stringify(last));
  // MCP 会话：只看的不接手；第一条动游戏的命令（hub 替它 hello）就是接手
  const mc = await connect(MCP);
  r = await call(mc, 'status');
  check(await playing() === '新来的', '交班：新的 MCP 会话只 status，不接手', await playing());
  r = await call(mc, 'click', { button: '1' });
  check(textOf(r).includes('点了 1 号') && await playing() === 'hub：ludus-selftest', '交班：新的 MCP 会话第一条动游戏的命令（先替它 hello）接手了', `${textOf(r)}\n${await playing()}`);
  m = await mail('# 新来的\nwait\n');
  check(m.includes('hub：ludus-selftest接手了'), '交班：信箱那个被 MCP 会话接手了', m);
  m = await mail('# 测试客户端\nhello\n');
  check(await playing() === '测试客户端', '交班：歇着的那个 hello，接回来了', await playing());
  r = await call(mc, 'click', { button: '1' });
  check(textOf(r).includes('测试客户端接手了') && textOf(r).includes('调 play 写 hello'), '交班：MCP 会话被接手后，动游戏的命令回一句（教它调 play 写 hello）', textOf(r));
  r = await call(mc, 'say', { text: 'MCP 这边还想说' });
  check(textOf(r).includes('没进面板') && chatLines().at(-1).text !== 'MCP 这边还想说', '交班：MCP 会话被接手后 say 不进面板', textOf(r));
  r = await call(mc, 'panel', { n: 3 });
  check(textOf(r).includes('新来的：我接着玩啦'), 'panel 里别人说的话写着是谁（自己说的才写「你」）', textOf(r));
  await mc.close();

  m = await mail('# 测试客户端\nbye\n');
  check(m.includes('走了') && !(await presence()).includes('测试客户端'), '信箱：bye 以后面板上不再挂着这个名字', `${m}\n${JSON.stringify(await presence())}`);
  check(await playing() === null, '交班：在玩的那个 bye 了，没人在玩（下一个来的直接接手）', await playing());
  check(fs.readdirSync(path.join(MB, 'in')).length === 0 && fs.readdirSync(path.join(MB, 'work')).length === 0, '信箱：in/、work/ 都空了');

  // ---- 要 token 的模式（--token：只听本机也要 token；--lan 也是这一套）----
  // 下面几个 hub 和上面的用同一个 data 文件夹，不让它们也看信箱
  const TOKEN_PORT = await freePort();
  const tokenFile = path.join(TMP, 'hub-token');
  // 这个 hub 顺便设了人的名字（LUDUS_USER_NAME）：说明、工具说明、面板上的话都该跟着换
  start(['hub.mjs', '--port', String(TOKEN_PORT), '--data', DATA, '--chat-port', String(CHAT_PORT), '--token', '--token-file', tokenFile, '--no-mailbox'],
    { ...limits, LUDUS_USER_NAME: '小月' }, 'hub-token');
  check(await until(`http://127.0.0.1:${TOKEN_PORT}/`), '要 token 的 hub 起来了');
  const noToken = await fetch(`http://127.0.0.1:${TOKEN_PORT}/mcp`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
  check(noToken.status === 401, '不带 token：401', noToken.status);
  const token = fs.readFileSync(tokenFile, 'utf8').trim();
  const tc = await connect(`http://127.0.0.1:${TOKEN_PORT}/mcp`, token);
  const tcTools = (await tc.listTools()).tools;
  check(tcTools.length > 10, '带 token 能连上、列工具');
  const named = tc.getInstructions() || '';
  check(named.includes('名字还是占位的') && named.includes('LUDUS_AGENT_NAME') && !named.includes('LUDUS_USER_NAME'),
    '名字：只设了人的名字，提醒里只剩 agent 的名字', named);
  check(named.includes('你在和 小月 一起玩') && named.includes('先问 小月') && !/\buser\b/.test(named),
    '名字：设了 LUDUS_USER_NAME=小月，说明里写的是小月，没有 user', named);
  check(tcTools.find((t) => t.name === 'say')?.description.includes('对 小月 说一句话'), '名字：say 的说明里也是小月',
    tcTools.find((t) => t.name === 'say')?.description);
  r = await call(tc, 'panel', { n: 100 });
  check(textOf(r).includes('小月：先去找莉莉娅吧'), '名字：panel 里人说的话写着小月', textOf(r));
  await tc.close();

  // ---- 会话开头清空「见过的东西」：用一个假的 vp_player（有 hello）----
  const fake = path.join(TMP, 'fake_player.py');
  const helloLog = path.join(TMP, 'hello.log');
  fs.writeFileSync(fake, [
    'import argparse, pathlib, sys',
    `LOG = pathlib.Path(${JSON.stringify(helloLog)})`,
    'p = argparse.ArgumentParser(description="假的 vp_player\\n\\n  hello                   新会话开头：清空这个会话见过的东西\\n")',
    'p.add_argument("--data")',
    'sub = p.add_subparsers(dest="command", required=True)',
    'sub.add_parser("hello"); sub.add_parser("status"); sub.add_parser("click").add_argument("n")',
    'a = p.parse_args()',
    'if a.command == "hello":',
    '    LOG.open("a", encoding="utf-8").write("hello\\n"); print("（新的会话：见过的东西清空了）")',
    'elif a.command == "click":',
    '    print("点了（假的）")',
    'else:',
    '    print("现在：click（假的）")',
  ].join('\n'));
  const FRESH_PORT = await freePort();
  start(['hub.mjs', '--port', String(FRESH_PORT), '--data', DATA, '--chat-port', String(CHAT_PORT), '--player', fake, '--no-mailbox'], limits, 'hub-fresh');
  check(await until(`http://127.0.0.1:${FRESH_PORT}/`), '用假 vp_player 的 hub 起来了');
  const f1 = await connect(`http://127.0.0.1:${FRESH_PORT}/mcp`);
  r = await call(f1, 'status');
  check(!textOf(r).includes('见过的东西清空了') && !fs.existsSync(helloLog), '只看的命令（status）之前不替它 hello', textOf(r));
  r = await call(f1, 'click', { button: '1' });
  check(textOf(r).includes('见过的东西清空了') && textOf(r).includes('点了（假的）'), '会话里第一条动游戏的命令之前先 hello', textOf(r));
  await call(f1, 'click', { button: '1' });
  const f2 = await connect(`http://127.0.0.1:${FRESH_PORT}/mcp`);
  await call(f2, 'click', { button: '1' });
  const hellos = fs.readFileSync(helloLog, 'utf8').split('\n').filter(Boolean).length;
  check(hellos === 2, `两个会话各 hello 一次（共 ${hellos} 次）`);
  await f1.close(); await f2.close();

  // ---- stdio 小桥（给 Claude Desktop）----
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  const bridge = new Client({ name: 'ludus-selftest-bridge', version: '0.0.0' });
  await bridge.connect(new StdioClientTransport({ command: process.execPath, args: [path.join(HUB_DIR, 'stdio-bridge.mjs'), MCP], stderr: 'pipe' }));
  check((bridge.getInstructions() || '').includes('say'), 'stdio 小桥带过来了 hub 的说明');
  r = await bridge.callTool({ name: 'shot', arguments: {} }, undefined, { timeout: 60_000 });
  check(imagesOf(r).length === 1, 'stdio 小桥：shot 的图也过来了');
  // 名字：没设 LUDUS_AGENT_NAME 就用客户端自己报的名字，设了用它
  const labelSoon = async (want) => {
    for (let i = 0; i < 20 && !(await presence()).includes(want); i++) await sleep(100);
    return (await presence()).includes(want);
  };
  check(await labelSoon('hub：ludus-selftest-bridge'), 'stdio 小桥：hub 那边用的是客户端报的名字', JSON.stringify(await presence()));
  await bridge.close();
  const { getDefaultEnvironment } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  const namedBridge = new Client({ name: 'ludus-selftest-bridge', version: '0.0.0' });
  await namedBridge.connect(new StdioClientTransport({ command: process.execPath, args: [path.join(HUB_DIR, 'stdio-bridge.mjs'), MCP],
    env: { ...getDefaultEnvironment(), LUDUS_AGENT_NAME: '阿桥' }, stderr: 'pipe' }));
  check(await labelSoon('hub：阿桥'), 'stdio 小桥：设了 LUDUS_AGENT_NAME 就用它', JSON.stringify(await presence()));
  check(!(await presence()).some((l) => l.includes('ludus-stdio-bridge')), 'stdio 小桥：名字不是写死的 ludus-stdio-bridge', JSON.stringify(await presence()));
  await namedBridge.close();

  // ---- stdio 小桥：客户端先开、hub 后开（桌面 app 常这样，它只在启动时列一次工具）----
  // 工具清单和说明先给本地那份；调用回「请…起 hub」；hub 起来以后不用重开客户端就能用
  {
    const LATE_PORT = await freePort();
    const late = new Client({ name: 'ludus-selftest-late', version: '0.0.0' });
    await late.connect(new StdioClientTransport({ command: process.execPath, args: [path.join(HUB_DIR, 'stdio-bridge.mjs'), `http://127.0.0.1:${LATE_PORT}/mcp`], stderr: 'pipe' }));
    const ins = late.getInstructions() || '';
    check(ins.includes('say') && !ins.includes('没开'), 'stdio 小桥（hub 没开）：说明用本地那份，不写「hub 没开」', ins);
    const offline = (await late.listTools()).tools.map((t) => t.name);
    const live = await connect(MCP);
    const online = (await live.listTools()).tools.map((t) => t.name);
    await live.close();
    check(online.every((n) => offline.includes(n)), `stdio 小桥（hub 没开）：工具清单齐全（${offline.length} 个，hub 给 ${online.length} 个）`, offline.join(','));
    r = await late.callTool({ name: 'status', arguments: {} }, undefined, { timeout: 30_000 });
    check(r.isError && textOf(r).includes('Ludus hub 没开') && textOf(r).includes('start-server.bat') && textOf(r).includes('不用重开'),
      'stdio 小桥（hub 没开）：调用回「hub 没开，请起一下」，说了双击哪个、不用重开', textOf(r));
    start(['hub.mjs', '--port', String(LATE_PORT), '--data', DATA, '--chat-port', String(CHAT_PORT), '--player', fake, '--no-mailbox'], limits, 'hub-late');
    check(await until(`http://127.0.0.1:${LATE_PORT}/`), '后开的 hub 起来了');
    r = await late.callTool({ name: 'status', arguments: {} }, undefined, { timeout: 60_000 });
    check(!r.isError && textOf(r).includes('假的'), 'stdio 小桥：hub 后开，不重开客户端也能用了', textOf(r));
    await late.close();
  }

  // ---- 一个玩家一个文件夹（设计第十一节）：另一份临时 data，selfplay/players.json 在（开关开着）----
  // 假插件照约定：读 selfplay/active.json，换了人就从它写的 inbox 位置读起；回话、事件写进那个玩家的文件夹
  {
    const PD = path.join(TMP, 'players-data');
    const PSP = path.join(PD, 'selfplay');
    fs.mkdirSync(PSP, { recursive: true });
    fs.writeFileSync(path.join(PSP, 'players.json'), JSON.stringify({ players: {} }));
    const writeStateP = () => fs.writeFileSync(path.join(PSP, 'state.json'), JSON.stringify({
      ts: localIso(), phase: 'click', why: '等着点按钮', snap: 1, hidden: 0,
      status: { turn: 2, year: '春火年', month: 3, coin: 160, dauName: '露丝', faName: '伍柏' }, view: '在：家', items: [{ n: 1, label: '见女儿', kind: '按钮' }], menu: [],
    }));
    writeStateP();
    timers.push(setInterval(writeStateP, 1000));
    // 和真插件（plugin/Players.cs）一样：每个玩家的 inbox 都看着；在玩的照常执行，别人只回答只看不动的和 hello
    const LOOK_ONLY = new Set(['ping', 'look', 'lookname', 'acts', 'plan', 'wish', 'home', 'read', 'fight']);
    const plug = { player: null, dir: null, offsets: new Map(), got: [] };
    timers.push(setInterval(() => {
      let act = {};
      try { act = JSON.parse(fs.readFileSync(path.join(PSP, 'active.json'), 'utf8')); } catch {}
      if ((act.player || null) !== plug.player || (act.dir || null) !== plug.dir) {
        Object.assign(plug, { player: act.player || null, dir: act.dir || null });
        if (plug.dir) plug.offsets.set(plug.dir, Math.max(plug.offsets.get(plug.dir) ?? 0, act.inbox ?? 0));
      }
      let dirs = [];
      try { dirs = fs.readdirSync(path.join(PSP, 'players')).map((d) => `players/${d}`); } catch {}
      for (const rel of dirs) {
        const dir = path.join(PSP, rel);
        const inbox = path.join(dir, 'inbox.jsonl');
        if (!fs.existsSync(inbox)) continue;
        const name = Object.entries(JSON.parse(fs.readFileSync(path.join(PSP, 'players.json'), 'utf8')).players).find(([, p]) => p.dir === rel)?.[0];
        const all = fs.readFileSync(inbox);
        const from = plug.offsets.get(rel) ?? 0;
        const fresh = all.subarray(from, all.lastIndexOf(0x0a) + 1);
        plug.offsets.set(rel, from + fresh.length);
        for (const line of fresh.toString('utf8').split('\n').filter(Boolean)) {
          const req = JSON.parse(line);
          plug.got.push(`${name}:${req.cmd}`);
          let reply = { ok: true, text: `（假插件）${req.cmd}` };
          if (req.cmd === 'hello') reply = { ok: true, text: '换了个人来玩：见过的东西清空了' };
          else if (rel !== plug.dir && !LOOK_ONLY.has(req.cmd)) reply = { ok: false, text: `（现在是${plug.player || '没人'}在玩）` };
          else if (req.cmd === 'click') {
            fs.appendFileSync(path.join(dir, 'events.jsonl'), JSON.stringify({ ts: localIso(), type: 'line', who: '露丝', say: `${name}点的` }) + '\n');
            reply = { ok: true, text: '点了 1 号 见女儿' };
          }
          fs.appendFileSync(path.join(dir, 'outbox.jsonl'), JSON.stringify({ id: req.id, kind: 'reply', ...reply }) + '\n');
        }
      }
    }, 100));

    const PCHAT = await freePort();
    const PHUB = await freePort();
    start([path.join(ROOT, 'chat-service', 'server.mjs')], { LUDUS_PORT: String(PCHAT), LUDUS_DATA: PD }, 'chat-players');
    check(await until(`http://127.0.0.1:${PCHAT}/api/history`), '玩家文件夹：临时聊天服务起来了');
    start(['hub.mjs', '--port', String(PHUB), '--data', PD, '--chat-port', String(PCHAT)], limits, 'hub-players');
    check(await until(`http://127.0.0.1:${PHUB}/health`), '玩家文件夹：hub 起来了');
    const PMB = path.join(PSP, 'mailbox');
    let pNo = 0;
    async function mailP(who, line) {
      const name = `p${String(++pNo).padStart(3, '0')}`;
      fs.writeFileSync(path.join(PMB, 'in', `${name}.txt`), `# ${who}\n${line}\n`);
      const out = path.join(PMB, 'out', `${name}.txt`);
      for (let i = 0; i < 300 && !fs.existsSync(out); i++) await sleep(100);
      return fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : `（没等到 out/${name}.txt）`;
    }
    const readJ = (f) => { try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch { return null; } };
    const A = '露丝的爸爸（测试）';
    const B = '另一个玩家';

    m = await mailP(A, 'hello');
    const players = readJ(path.join(PSP, 'players.json'))?.players ?? {};
    const actA = readJ(path.join(PSP, 'active.json')) ?? {};
    const dirA = players[A]?.dir;
    check(dirA && actA.player === A && actA.dir === dirA && Number.isInteger(actA.inbox),
      `玩家文件夹：A hello 以后聊天服务记进 players.json（${dirA}），active.json 写着 A 和 inbox 从哪读起`, JSON.stringify({ players, actA }));
    check(m.includes('见过的东西清空了') && fs.existsSync(path.join(PSP, dirA, 'inbox.jsonl')) && !fs.existsSync(path.join(PSP, 'inbox.jsonl')),
      '玩家文件夹：A 的 hello 写进 A 自己的 inbox，插件从那里回话（selfplay/ 下面没有 inbox）', m);
    m = await mailP(A, 'click 1');
    m = await mailP(A, 'log -n 5');
    check(m.includes(`${A}点的`), '玩家文件夹：A 的事件写在 A 的文件夹里，A 翻得到', m);
    m = await mailP(A, 'note 克莱尔 A 记的一句');
    const bookA = readJ(path.join(PSP, dirA, 'impressions.json')) ?? {};
    check(JSON.stringify(bookA).includes('A 记的一句'), '玩家文件夹：A 的 note 记进 A 自己的印象簿', `${m}\n${JSON.stringify(bookA)}`);

    m = await mailP(B, 'hello');
    const dirB = (readJ(path.join(PSP, 'players.json'))?.players ?? {})[B]?.dir;
    const actB = readJ(path.join(PSP, 'active.json')) ?? {};
    check(dirB && dirB !== dirA && actB.player === B && m.includes('见过的东西清空了'),
      `玩家文件夹：B hello 接手，有了自己的文件夹（${dirB}），插件马上听 B 的（换人后的第一条没漏）`, `${m}\n${JSON.stringify(actB)}`);
    m = await mailP(B, 'click 1');
    m = await mailP(B, 'log -n 5');
    check(m.includes(`${B}点的`) && !m.includes(`${A}点的`), '玩家文件夹：B 只翻得到 B 自己的事件，A 的不在', m);
    m = await mailP(B, 'look 克莱尔');
    check(!m.includes('A 记的一句'), '玩家文件夹：B 翻克莱尔，看不到 A 记的话（印象簿只认自己的）', m);
    m = await mailP(A, 'status');
    check(m.includes('露丝'), '玩家文件夹：A 歇着时 status 照看（state.json 大家共用）', m);
    m = await mailP(A, 'look');
    check(m.includes('（假插件）look') && plug.got.includes(`${A}:look`) && fs.readFileSync(path.join(PSP, dirA, 'outbox.jsonl'), 'utf8').includes('（假插件）look'),
      '玩家文件夹：A 歇着时 look 照样看（插件也回答别人只看不动的命令），回话在 A 自己的 outbox', m);
    m = await mailP(A, 'hello');
    m = await mailP(A, 'look 克莱尔');
    check(m.includes('A 记的一句'), '玩家文件夹：A 接回来，翻克莱尔看得到自己记的话', m);
    // B 没见过克莱尔也会问一次插件（vp_player 对游戏里认识的人照给资料），所以有 B:lookname
    check(plug.got.join(' ') === [`${A}:hello`, `${A}:click`, `${B}:hello`, `${B}:click`, `${B}:lookname`, `${A}:look`, `${A}:hello`, `${A}:lookname`].join(' '),
      '玩家文件夹：每条命令都进了发命令的那个人自己的 inbox，插件一条不漏', plug.got.join(' '));
  }

  // ---- 聊天服务 --hub：顺手起 hub（自己跑，不跟着聊天服务停）；已经开着就不起第二个 ----
  const AUTO_DATA = path.join(TMP, 'auto-data');
  fs.mkdirSync(path.join(AUTO_DATA, 'selfplay'), { recursive: true });
  // 上一个 hub 跑到一半被停了：留下两个在跑标记，一个没有回话、一个回话已经写好了
  const AUTO_OUT = path.join(AUTO_DATA, 'selfplay', 'mailbox', 'out');
  fs.mkdirSync(AUTO_OUT, { recursive: true });
  fs.writeFileSync(path.join(AUTO_OUT, 'L019.running'), '在跑：click 1\n开始：16:00:00\n');
  fs.writeFileSync(path.join(AUTO_OUT, 'L020.running'), '在跑：status\n开始：16:00:05\n');
  fs.writeFileSync(path.join(AUTO_OUT, 'L020.txt'), '写好了的回话\n');
  const AUTO_HUB = await freePort();
  // 这一对设了两个名字：聊天服务给页面的名字、顺手起的 hub（继承聊天服务的环境变量）写的 README 都该跟着换
  const autoEnv = { ...limits, LUDUS_DATA: AUTO_DATA, LUDUS_HUB_PORT: String(AUTO_HUB), LUDUS_HUB_LOG: path.join(TMP, 'auto-hub.log'),
    LUDUS_USER_NAME: '小月', LUDUS_AGENT_NAME: '阿月' };
  const serverJs = path.join(ROOT, 'chat-service', 'server.mjs');
  const AUTO_CHAT = await freePort();
  start([serverJs, '--hub'], { ...autoEnv, LUDUS_PORT: String(AUTO_CHAT) }, 'chat-hub-a');
  check(await until(`http://127.0.0.1:${AUTO_HUB}/health`, 15_000), '聊天服务 --hub 顺手起了 hub');
  const h1 = await (await fetch(`http://127.0.0.1:${AUTO_HUB}/health`)).json();
  autoHubPid = h1.pid;
  check(h1.data === path.resolve(AUTO_DATA) && h1.mailbox, '顺手起的 hub 用的是聊天服务的 data 文件夹，信箱开着', JSON.stringify(h1));
  const autoNames = await (await fetch(`http://127.0.0.1:${AUTO_CHAT}/api/names`)).json();
  check(autoNames.user === '小月' && autoNames.agent === '阿月', '名字：聊天服务 /api/names 给的是设好的名字', JSON.stringify(autoNames));
  const defaultNames = await (await fetch(`http://127.0.0.1:${CHAT_PORT}/api/names`)).json();
  check(defaultNames.user === 'user' && defaultNames.agent === 'agent', '名字：没设时聊天服务给的是 user / agent', JSON.stringify(defaultNames));
  const autoReadme = path.join(AUTO_DATA, 'selfplay', 'README.txt');
  for (let i = 0; i < 50 && !fs.existsSync(autoReadme); i++) await sleep(100);
  const autoText = fs.existsSync(autoReadme) ? fs.readFileSync(autoReadme, 'utf8') : '';
  check(!autoText.includes('名字还是占位的') && !autoText.includes('{{NAMES_NOTE}}'), '名字：两个都设了，信箱说明里没有提醒', autoText.slice(0, 600));
  check(autoText.includes('来和 小月 一起玩') && autoText.includes('# 阿月（claude.ai）') && !/\buser\b/.test(autoText)
    && autoText.includes(path.join(AUTO_DATA, 'selfplay', 'mailbox')) && !autoText.includes('{{'),
  '名字：顺手起的 hub 写的 README.txt 用的是小月、阿月，信箱路径是这份 data 的', autoText.slice(0, 600));
  const left = fs.existsSync(path.join(AUTO_OUT, 'L019.txt')) ? fs.readFileSync(path.join(AUTO_OUT, 'L019.txt'), 'utf8') : '';
  check(left.includes('上次停在了半路') && left.includes('click 1') && !fs.existsSync(path.join(AUTO_OUT, 'L019.running')),
    'hub 启动时：上次没跑完、没有回话的那条，写了一份回话说没跑完，标记清掉了', left);
  check(fs.readFileSync(path.join(AUTO_OUT, 'L020.txt'), 'utf8') === '写好了的回话\n' && !fs.existsSync(path.join(AUTO_OUT, 'L020.running')),
    'hub 启动时：回话已经写好的那条，只清掉标记，回话不动');
  const chatB = start([serverJs, '--hub'], { ...autoEnv, LUDUS_PORT: String(await freePort()) }, 'chat-hub-b');
  for (let i = 0; i < 50 && !chatB.output().includes('[hub]'); i++) await sleep(100);
  const h2 = await (await fetch(`http://127.0.0.1:${AUTO_HUB}/health`)).json();
  check(chatB.output().includes('已经开着') && h2.pid === h1.pid, '第二个聊天服务看到 hub 开着，没再起一个', chatB.output());

  console.log(`  假插件收到的命令：${handled.join(' ')}`);
  if (failed) console.log('\nhub 的日志：\n' + hubProc.output());
} catch (err) {
  failed++;
  console.error('FAIL  测试自己出错：', err);
  for (const c of children) console.error(`--- ${c.name} ---\n${c.output()}`);
} finally {
  for (const t of timers) clearInterval(t);
  for (const c of children) c.kill();
  if (autoHubPid) try { process.kill(autoHubPid); } catch {}
  await sleep(300);
  if (!process.argv.includes('--keep')) fs.rmSync(TMP, { recursive: true, force: true });
  console.log(failed ? `\n${failed} 项没过` : '\n全部通过');
  process.exit(failed ? 1 : 0);
}
