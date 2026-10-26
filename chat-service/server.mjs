// 游戏旁边的本地聊天服务：聊天页（index.html）和几个 API。零依赖，Node.js 22.12 或更新（和别的组件一样，见 docs/安装.md）。
// 只监听 127.0.0.1；页面发言、agent 回话、页面自报的诊断都记在 data/ 下。
//
//   node server.mjs            启动（端口默认 8740，LUDUS_PORT 可改）
//   node server.mjs --hub      顺手起 Ludus hub（mcp-hub/hub.mjs，8741，LUDUS_HUB_PORT 可改）：已经开着就不起；
//                              hub 自己跑，这边停了它不停。start-server.bat 用的是这个
//   node say.mjs "话"          以现在这个玩家的名字回一句（读 data/token；名字和 vp_player 一样认）
//   名字：LUDUS_USER_NAME 是人的名字（默认 user），LUDUS_AGENT_NAME 是 agent 的默认名字（默认 agent）。
//                              页面上的名字从这里拿（GET /api/names，SSE 的 hello 里也带着）
//   POST /api/session          开新的一局（带 token），页面清空，只显示这一局的消息
//   POST /api/presence         一个会话报到：{ id, label }，{ id, gone: true } 是走了（带 token）。
//                              带 claim: 'hello' 是接手、'act' 是要动游戏（别人在玩就挡回去，回 you: 'blocked'）
//   GET  /api/presence         最近十分钟有动静的会话，在玩的那一个 active: true（面板据此出不出来，标题栏写谁在）
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.LUDUS_PORT || 8740);
const DATA = process.env.LUDUS_DATA || path.join(HERE, 'data');  // 测试时可以指到别的文件夹
const CHAT = path.join(DATA, 'chat.jsonl');
const DIAG = path.join(DATA, 'diag.jsonl');
const TOKEN_FILE = path.join(DATA, 'token');
const SESSION_FILE = path.join(DATA, 'session.json');
const MODES_FILE = path.join(DATA, 'modes.json');  // 游戏插件读它：selfplay agent 自己玩（关着就是 user 玩、陪玩同步），skipGames 跳过小游戏，readCps 看剧情的速度
const PRESENCE_FILE = path.join(DATA, 'presence.json');  // 谁挂着：重启服务也不丢
const PRESENCE_TTL_MS = Number(process.env.LUDUS_PRESENCE_TTL_MS || 10 * 60_000);  // 这么久没动静就算走了
const ACTIVE_FILE = path.join(DATA, 'active.json');      // 现在在玩的那一个：重启服务也不丢
const USER_NAME = process.env.LUDUS_USER_NAME?.trim() || 'user';     // 人的名字：页面上自己的气泡、输入框的提示
const AGENT_NAME = process.env.LUDUS_AGENT_NAME?.trim() || 'agent';  // 没说是谁的回话（say.mjs）记在这个名字下，页面上的按钮也用它
const NAMES = { user: USER_NAME, agent: AGENT_NAME };
// 谁的是谁的（设计第十一节）：一个玩家一个文件夹。selfplay/players.json 在才开（tools/migrate_players.py 建它），不在就什么都不写
const SELFPLAY = path.join(DATA, 'selfplay');
const PLAYERS_FILE = path.join(SELFPLAY, 'players.json');  // {"players": {名字: {dir, first}}}
const SP_ACTIVE_FILE = path.join(SELFPLAY, 'active.json'); // 给插件的：{player, dir, since, inbox}

fs.mkdirSync(DATA, { recursive: true });
if (!fs.existsSync(TOKEN_FILE)) fs.writeFileSync(TOKEN_FILE, crypto.randomBytes(24).toString('hex'));
const TOKEN = fs.readFileSync(TOKEN_FILE, 'utf8').trim();

// Host 只认本机名，挡 DNS rebinding；页面来的 POST 若带 Origin 必须是本页。
const HOSTS = new Set([`127.0.0.1:${PORT}`, `localhost:${PORT}`]);
const ORIGINS = new Set([...HOSTS].map((h) => `http://${h}`));

function readChat(limit = 200) {
  if (!fs.existsSync(CHAT)) return [];
  return fs.readFileSync(CHAT, 'utf8').split('\n').filter(Boolean).slice(-limit).map((l) => JSON.parse(l));
}

let seq = readChat(1).at(-1)?.id ?? 0;
let tick = 0;
const streams = new Set();

// 一局游戏一段对话：页面只显示 since 之后的消息，旧消息仍留在 chat.jsonl 里
let session = { since: 0 };
try { session = JSON.parse(fs.readFileSync(SESSION_FILE, 'utf8')); } catch {}

// packByScene：陪玩同步按地点 / 按剧情分包（关了就按月）。
// skipGames 默认开：限时小游戏 agent 一步一步点来不及（面板上的开关、skip off 可以关）。已经有 modes.json 的照它，不改
// readCps：agent 自己玩时插件翻台词、翻页的速度，每秒几个字（面板上「看剧情」那根滑条），0 是自己翻
const DEFAULT_MODES = { selfplay: false, skipGames: true, packByScene: true, readCps: 12 };

function readModes() {
  try { return { ...DEFAULT_MODES, ...JSON.parse(fs.readFileSync(MODES_FILE, 'utf8')) }; }
  catch { return { ...DEFAULT_MODES }; }
}

function setModes(changes) {
  const modes = readModes();
  for (const key of Object.keys(DEFAULT_MODES)) {
    const v = changes[key];
    if (typeof DEFAULT_MODES[key] === 'boolean' && typeof v === 'boolean') modes[key] = v;
    if (typeof DEFAULT_MODES[key] === 'number' && Number.isFinite(v) && v >= 0 && v <= 100) modes[key] = v;
  }
  fs.writeFileSync(MODES_FILE, JSON.stringify(modes));
  broadcast('modes', modes);
  return modes;
}

if (!fs.existsSync(MODES_FILE)) fs.writeFileSync(MODES_FILE, JSON.stringify(DEFAULT_MODES));

function startSession(appId) {
  session = { since: seq, appId: appId ?? null, ts: new Date().toISOString() };
  fs.writeFileSync(SESSION_FILE, JSON.stringify(session));
  broadcast('session', session);
  console.log(`[session] 新的一局 appId=${session.appId} since=#${session.since}`);
  return session;
}

function broadcast(event, data) {
  const frame = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  for (const res of streams) res.write(frame);
}

function addMessage(from, text, extra = {}) {
  const msg = { id: ++seq, ts: new Date().toISOString(), from, text, ...extra };
  fs.appendFileSync(CHAT, JSON.stringify(msg) + '\n');
  broadcast('message', msg);
  return msg;
}

// ---- 谁挂着：会话自己来报到（channel 定时报，vp_player 每条命令报，hub 每个 MCP 会话、每个信箱名字报），
// 带 token 回话也算一次。过了 PRESENCE_TTL_MS 没动静就算走了。
// 有人来、有人走、换了在玩的时推 presence 给页面和面板；只是又报了一次就不推。
const presence = new Map();  // id → { id, label, since, last }
try {
  for (const p of JSON.parse(fs.readFileSync(PRESENCE_FILE, 'utf8'))) {
    if (p?.id && Date.now() - p.last < PRESENCE_TTL_MS) presence.set(p.id, p);
  }
} catch {}

// ---- 现在在玩的那一个：同一时间只有一个会话在玩，新的接手、旧的自动交班 ----
// 接手：claim 'hello'（vp_player 的 hello、hub 替 MCP 会话跑的 hello、信箱里写的 hello、channel 起来时）。
// 动游戏的命令（wait、do、click……）和回话：claim 'act'。没人在玩、或者在玩的就是它，照常（没人在玩就算它在玩）；
// 别人在玩就挡回去，它那边说一句交班的话。只读的命令（status、look……）只报到，不接手。
// 在玩的那一个 PRESENCE_TTL_MS 没动静（报到过期）就算走了，下一个来的直接接手。
// 不硬杀谁：远程那边也杀不了。被接手的下一次来，自己就知道该歇着了。
let active = null;             // { id, label, since }
const handedOver = new Map();  // 被接手的 id → 接手的那个的名字：它回来时说「X 接手了」，而不是「X 在玩」
try {
  const saved = JSON.parse(fs.readFileSync(ACTIVE_FILE, 'utf8'));
  if (saved.active?.id && presence.has(saved.active.id)) active = saved.active;
  for (const [id, by] of saved.handedOver ?? []) handedOver.set(id, by);
} catch {}

const presenceList = () => [...presence.values()]
  .sort((a, b) => a.since - b.since)
  .map((p) => ({ ...p, active: p.id === active?.id }));

let presenceSaveTimer;
function savePresenceSoon() {
  clearTimeout(presenceSaveTimer);
  presenceSaveTimer = setTimeout(() => fs.writeFileSync(PRESENCE_FILE, JSON.stringify([...presence.values()])), 1000);
}

function presenceChanged() {
  savePresenceSoon();
  broadcast('presence', presenceList());
  console.log(`[presence] ${presenceList().map((p) => (p.active ? `▶${p.label}` : p.label)).join('、') || '没有会话挂着'}`);
}

function setActive(p) {
  const prev = active;
  if (prev?.id === p?.id) return;
  active = p ? { id: p.id, label: p.label, since: Date.now() } : null;
  if (p) handedOver.delete(p.id);
  if (prev && p) handedOver.set(prev.id, p.label);
  while (handedOver.size > 50) handedOver.delete(handedOver.keys().next().value);
  saveActive();
  console.log(`[active] ${prev?.label ?? '没人'} → ${active?.label ?? '没人'}`);
}

function saveActive() {
  fs.writeFileSync(ACTIVE_FILE, JSON.stringify({ active, handedOver: [...handedOver] }));
  syncPlayerFiles();
}

// ---- 玩家文件夹（players.json 在才开）----
// 名字就是报到的名字（label）。有人接手时把它记进 players.json（文件夹名用 Windows 上安全的写法，重了加 -2），
// 再写 selfplay/active.json 告诉插件：换了谁、它的文件夹、从 inbox 的哪个位置读起（交班那一刻的长度，
// 之后写进去的都是新命令；插件晚一点才发现换了人也不会漏掉第一条）。
const RESERVED = new Set(['CON', 'PRN', 'AUX', 'NUL', ...[1, 2, 3, 4, 5, 6, 7, 8, 9].flatMap((i) => [`COM${i}`, `LPT${i}`])]);
function safeDir(name) {  // 和 tools/vp_players.py 的 safe_dir 一样
  let s = String(name).replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim().replace(/[. ]+$/, '');
  s = s.replace(/\s+/g, ' ').slice(0, 60).replace(/[. ]+$/, '');
  if (!s) s = 'player';
  if (RESERVED.has(s.split('.')[0].toUpperCase())) s = `_${s}`;
  return s;
}

const localIso = (t = Date.now()) => {
  const d = new Date(t);
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
};

function writeAtomic(file, obj) {
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(obj, null, 1));
  for (let i = 0; ; i++) {
    try { fs.renameSync(tmp, file); return; } catch (err) {
      if (i >= 20) { try { fs.unlinkSync(tmp); } catch {} throw err; }
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 25);  // 插件正开着这个文件读：等一下
    }
  }
}

function ensurePlayer(name) {
  let doc;
  try { doc = JSON.parse(fs.readFileSync(PLAYERS_FILE, 'utf8')); } catch { doc = null; }
  if (!doc || typeof doc.players !== 'object' || !doc.players) doc = { players: {} };
  const known = doc.players[name];
  if (known?.dir) {
    if (fs.existsSync(path.join(SELFPLAY, known.dir))) return known.dir;
    // 文件夹不在了：被挪走了（清空一个玩家）。同一个名字再来算重新开始，first 记成现在：
    // vp_memory 收话只收 first 以后说的，旧文件夹那时说的话不会又收进新印象簿
    known.first = localIso();
    fs.mkdirSync(path.join(SELFPLAY, known.dir), { recursive: true });
    writeAtomic(PLAYERS_FILE, doc);
    console.log(`[players] ${name} 的文件夹不在了，重新开始：${known.dir}`);
    return known.dir;
  }
  const used = new Set(Object.values(doc.players).map((p) => p?.dir));
  const base = `players/${safeDir(name)}`;
  let dir = base;
  for (let n = 2; used.has(dir); n++) dir = `${base}-${n}`;
  doc.players[name] = { dir, first: localIso() };
  fs.mkdirSync(path.join(SELFPLAY, dir), { recursive: true });  // 建条目时就建文件夹：之后文件夹不在，就是被挪走了
  writeAtomic(PLAYERS_FILE, doc);
  console.log(`[players] 新玩家：${name} → ${dir}`);
  return dir;
}

function syncPlayerFiles() {
  if (!fs.existsSync(PLAYERS_FILE)) return;
  try {
    let cur = null;
    try { cur = JSON.parse(fs.readFileSync(SP_ACTIVE_FILE, 'utf8')); } catch {}
    if (!active) {
      if (cur && !cur.player) return;
      writeAtomic(SP_ACTIVE_FILE, { player: '', dir: '', since: localIso() });
      return;
    }
    const want = { player: active.label, dir: ensurePlayer(active.label), since: localIso(active.since) };
    if (cur && cur.player === want.player && cur.dir === want.dir && cur.since === want.since) return;
    const inbox = path.join(SELFPLAY, want.dir, 'inbox.jsonl');
    want.inbox = fs.existsSync(inbox) ? fs.statSync(inbox).size : 0;
    writeAtomic(SP_ACTIVE_FILE, want);
    console.log(`[players] active.json：${want.player}（${want.dir}，inbox 从 ${want.inbox} 读起）`);
  } catch (err) {
    console.error('[players] 写 players.json / active.json 失败：', err.message);
  }
}

// 在玩的那一个报到过期了、说了走了：没人在玩
function dropActiveIfGone() {
  if (active && !presence.has(active.id)) setActive(null);
}

function touchPresence(client) {
  const str = (v, max) => (typeof v === 'string' ? v.trim().slice(0, max) : '');
  const id = str(client?.id, 160);
  if (!id) return null;
  if (client.gone === true) {
    if (presence.delete(id)) {
      dropActiveIfGone();
      presenceChanged();
    }
    return null;
  }
  const label = str(client.label, 60) || id;
  const now = Date.now();
  let p = presence.get(id);
  if (!p) {
    p = { id, label, since: now, last: now };
    presence.set(id, p);
    presenceChanged();
  } else {
    p.last = now;
    if (p.label !== label) {
      p.label = label;
      if (active?.id === id) {  // 在玩的那个换了名字（hello 名字）：换了一个玩家，文件夹也换
        Object.assign(active, { label, since: now });
        saveActive();
      }
      presenceChanged();
    } else {
      savePresenceSoon();
    }
  }
  return p;
}

/** 报到，顺便看能不能玩：{ you: 'active' | 'idle' | 'blocked', active, handedOver }。
 *  you 是 blocked 时，active 是正在玩的那一个，handedOver 说它是不是刚被那一个接手的。 */
function claimTurn(client, kind) {
  const p = touchPresence(client);
  const publicActive = () => (active ? { id: active.id, label: active.label } : null);
  if (!p) return { you: 'idle', active: publicActive() };
  const before = active?.id;
  if (kind === 'hello' || (kind === 'act' && !active)) setActive(p);
  if (active?.id !== before) presenceChanged();
  syncPlayerFiles();  // 刚搬完家（players.json 刚出现）时，在玩的那个下一次来就把 active.json 补上
  if (active?.id === p.id) return { you: 'active', active: publicActive() };
  if (kind === 'act') return { you: 'blocked', active: publicActive(), handedOver: handedOver.has(p.id) };
  return { you: 'idle', active: publicActive() };
}

setInterval(() => {
  let gone = false;
  for (const [id, p] of presence) {
    if (Date.now() - p.last >= PRESENCE_TTL_MS) gone = presence.delete(id) || gone;
  }
  if (gone) {
    dropActiveIfGone();
    presenceChanged();
  }
}, 15_000);

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function send(res, status, body, type = 'text/plain; charset=utf-8') {
  res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store' });
  res.end(body);
}

const sendJson = (res, status, obj) => send(res, status, JSON.stringify(obj), 'application/json; charset=utf-8');

async function readJson(req) {
  let size = 0;
  const chunks = [];
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 64 * 1024) throw new HttpError(413, 'too large');
    chunks.push(chunk);
  }
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}'); }
  catch { throw new HttpError(400, 'bad json'); }
}

// 页面来的写请求：JSON 类型（跨站发不了不经预检的 JSON），Origin 若有必须是本页。
function checkPage(req) {
  const origin = req.headers.origin;
  if (origin && !ORIGINS.has(origin)) throw new HttpError(403, 'bad origin');
  if (!String(req.headers['content-type'] || '').startsWith('application/json')) throw new HttpError(415, 'json only');
}

function checkToken(req) {
  const got = Buffer.from(String(req.headers.authorization || ''));
  const want = Buffer.from(`Bearer ${TOKEN}`);
  if (got.length !== want.length || !crypto.timingSafeEqual(got, want)) throw new HttpError(401, 'bad token');
}

function textOf(body) {
  const text = typeof body.text === 'string' ? body.text.trim() : '';
  if (!text) throw new HttpError(400, 'empty');
  return text.slice(0, 4000);
}

function openStream(req, res) {
  res.writeHead(200, {
    'Content-Type': 'text/event-stream; charset=utf-8',
    'Cache-Control': 'no-store',
    Connection: 'keep-alive',
  });
  res.write(`retry: 2000\nevent: hello\ndata: ${JSON.stringify({ seq, tick, since: session.since, presence: presenceList(), names: NAMES })}\n\n`);
  streams.add(res);
  req.on('close', () => streams.delete(res));
}

const server = http.createServer(async (req, res) => {
  try {
    if (!HOSTS.has(req.headers.host)) throw new HttpError(421, 'bad host');
    const { pathname } = new URL(req.url, `http://${req.headers.host}`);
    const route = `${req.method} ${pathname}`;

    if (route === 'GET /') {
      return send(res, 200, fs.readFileSync(path.join(HERE, 'index.html')), 'text/html; charset=utf-8');
    }
    if (route === 'GET /events') return openStream(req, res);
    if (route === 'GET /api/history') return sendJson(res, 200, readChat().filter((m) => m.id > session.since));
    if (route === 'GET /api/names') return sendJson(res, 200, NAMES);
    if (route === 'GET /api/modes') return sendJson(res, 200, readModes());
    if (route === 'POST /api/modes') {
      checkPage(req);
      return sendJson(res, 200, setModes(await readJson(req)));
    }
    if (route === 'POST /api/session') {
      checkToken(req);
      return sendJson(res, 200, startSession((await readJson(req)).appId));
    }

    if (route === 'POST /api/send') {
      checkPage(req);
      return sendJson(res, 200, addMessage('user', textOf(await readJson(req))));
    }
    if (route === 'POST /api/diag') {
      checkPage(req);
      const entry = { ts: new Date().toISOString(), ua: req.headers['user-agent'], ...(await readJson(req)) };
      fs.appendFileSync(DIAG, JSON.stringify(entry) + '\n');
      console.log(`[diag] ${entry.kind ?? '?'} ${JSON.stringify(entry.stats ?? {})}`);
      return sendJson(res, 200, { ok: true });
    }
    if (route === 'POST /api/reply') {
      checkToken(req);
      const body = await readJson(req);
      const text = textOf(body);
      if (!body.client?.id) {
        touchPresence({ id: 'reply', label: AGENT_NAME });  // 没说是谁就记成 AGENT_NAME，不管谁在玩（say.mjs 会说是谁）
        return sendJson(res, 200, addMessage('agent', text, { who: AGENT_NAME }));
      }
      // 说话也算动了游戏：别人在玩就不进面板，回 409，它那边说一句交班的话
      const turn = claimTurn(body.client, 'act');
      if (turn.you === 'blocked') return sendJson(res, 409, { error: 'handed-over', ...turn });
      const p = presence.get(String(body.client.id).trim());
      return sendJson(res, 200, addMessage('agent', text, { who: p?.label ?? AGENT_NAME, client: p?.id }));
    }
    if (route === 'GET /api/presence') return sendJson(res, 200, presenceList());
    if (route === 'POST /api/presence') {
      checkToken(req);
      const body = await readJson(req);
      const kind = body.claim === 'hello' || body.claim === 'act' ? body.claim : null;
      return sendJson(res, 200, { ...claimTurn(body, kind), presence: presenceList() });
    }
    throw new HttpError(404, 'not found');
  } catch (err) {
    if (!res.headersSent) send(res, err.status ?? 500, err.message);
    if (!err.status) console.error(err);
  }
});

// 改了 index.html 就让所有打开的页面自己刷新，调样式时不用手动刷新。
let reloadTimer;
fs.watch(path.join(HERE, 'index.html'), () => {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(() => broadcast('reload', {}), 300);
});

// 每秒一跳：钉在 Steam 覆盖层里时，看它是不是实时刷新。
setInterval(() => broadcast('tick', { n: ++tick, ts: new Date().toISOString() }), 1000);

// ---- 顺手起 Ludus hub（--hub）----
// hub 和这个服务各跑各的：重启聊天服务不该断掉连着 hub 的客户端，所以 hub 单独起（detached），这边停了它照旧。
// 端口上已经有东西在答话（hub 开着，哪怕是要 token 的局域网模式）就不起；同时起了两个，后来的那个端口拿不到会自己退出。
const HUB_DIR = path.resolve(HERE, '..', 'mcp-hub');
const HUB_PORT = Number(process.env.LUDUS_HUB_PORT || 8741);
const HUB_LOG = process.env.LUDUS_HUB_LOG || path.join(HUB_DIR, 'data', 'hub.log');

async function startHub() {
  try {
    await fetch(`http://127.0.0.1:${HUB_PORT}/health`, { signal: AbortSignal.timeout(2000) });
    console.log(`[hub] 已经开着（127.0.0.1:${HUB_PORT}），不再起`);
    return;
  } catch {}
  if (!fs.existsSync(path.join(HUB_DIR, 'node_modules'))) {
    console.log('[hub] 没起：hub 还没装依赖，双击 mcp-hub\\start-hub.bat 装一次（之后就能跟着起了）');
    return;
  }
  fs.mkdirSync(path.dirname(HUB_LOG), { recursive: true });
  const child = spawn(process.execPath, ['hub.mjs', '--port', String(HUB_PORT), '--data', DATA, '--chat-port', String(PORT), '--log', HUB_LOG], {
    cwd: HUB_DIR, detached: true, stdio: 'ignore', windowsHide: true,
  });
  child.on('error', (err) => console.log(`[hub] 起不来：${err.message}`));
  child.unref();
  console.log(`[hub] 起了 Ludus hub：http://127.0.0.1:${HUB_PORT}/mcp（进程 ${child.pid}，日志 ${HUB_LOG}）`);
}

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Ludus Lunae 聊天服务：http://127.0.0.1:${PORT}/（${USER_NAME} 和 ${AGENT_NAME}）`);
  console.log(fs.existsSync(PLAYERS_FILE) ? '[players] 玩家文件夹开着（selfplay/players.json）' : '[players] 玩家文件夹没开（没有 selfplay/players.json），大家共用 selfplay/');
  syncPlayerFiles();
  if (process.argv.includes('--hub')) startHub();
});
