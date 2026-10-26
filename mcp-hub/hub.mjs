// Ludus hub：把游戏的工具包成一个 MCP 服务（Streamable HTTP，地址 /mcp），谁连上来都用同一套。
// 现在只有火山的女儿：每个工具是 tools/vp_player.py 的一条命令，子进程调用，输出原样返回。
// 只能读写文件的客户端走文字信箱（data/selfplay/mailbox/，见下面「文字信箱」），和 MCP 共用同一套。
// 设计见 docs/设计理念.md 第十节，用法见 mcp-hub/README.md，怎么接各家客户端见 docs/连接.md。
//
//   node hub.mjs                       只听 127.0.0.1:8741，客户端不用带 token
//   node hub.mjs --lan                 听局域网（0.0.0.0）：局域网里别的机器也能连，所有请求都要 token（data/token）
//   node hub.mjs --token               只听本机，但也要 token（接隧道时用：隧道进来的请求看着都像本机）
//   node hub.mjs --log data/hub.log    日志也写进文件（不开窗口、在后台起时用）
//
// 其他参数：--port N  --data DIR（聊天服务的 data 文件夹）  --chat-port N  --python EXE  --ffmpeg EXE
//           --token-file FILE  --fresh-command NAME（会话开头清空「见过的东西」的命令名，none 是不调）
//           --no-mailbox（不看文字信箱：同一个 data 文件夹上开第二个 hub 时用）
// 名字：LUDUS_USER_NAME 是人的名字（默认 user），给 agent 看的说明和回话里都用它；
//       LUDUS_AGENT_NAME 是 agent 的默认名字（默认 agent）。聊天服务、channel、vp_player 读的是同一对环境变量。
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { StringDecoder } from 'node:string_decoder';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { CallToolRequestSchema, ListToolsRequestSchema, isInitializeRequest } from '@modelcontextprotocol/sdk/types.js';
import { WAIT_SECONDS, GAME_TOOLS, PLAY_TOOL, SAY_TOOL, PANEL_TOOL, MEMORY_TOOLS, INSTRUCTIONS, namesNote, pub } from './tools.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const VERSION = '0.1.0';

const { values: opt } = parseArgs({
  options: {
    port: { type: 'string', default: process.env.LUDUS_HUB_PORT || '8741' },
    lan: { type: 'boolean', default: false },
    token: { type: 'boolean', default: false },
    data: { type: 'string', default: process.env.LUDUS_DATA || path.join(ROOT, 'chat-service', 'data') },
    'chat-port': { type: 'string', default: process.env.LUDUS_CHAT_PORT || '8740' },
    python: { type: 'string', default: process.env.LUDUS_PYTHON || 'python' },
    ffmpeg: { type: 'string', default: process.env.LUDUS_FFMPEG || 'ffmpeg' },
    player: { type: 'string', default: path.join(ROOT, 'tools', 'vp_player.py') },
    'token-file': { type: 'string', default: path.join(HERE, 'data', 'token') },
    'fresh-command': { type: 'string', default: process.env.LUDUS_FRESH_COMMAND || '' },
    'allow-origin': { type: 'string', multiple: true, default: [] },
    'no-mailbox': { type: 'boolean', default: false },
    log: { type: 'string' },
  },
});

const PORT = Number(opt.port);
const DATA = path.resolve(opt.data);
const SELFPLAY = path.join(DATA, 'selfplay');
const CHAT = `http://127.0.0.1:${Number(opt['chat-port'])}`;
const PLAYER = path.resolve(opt.player);
const MEMORY = path.join(path.dirname(PLAYER), 'vp_memory.py');  // 印象簿、反馈、上回说到（note / feedback / recap）：有这个文件就接上
const CACHE = path.join(HERE, 'cache');
const USER_NAME = process.env.LUDUS_USER_NAME?.trim() || 'user';
const AGENT_NAME = process.env.LUDUS_AGENT_NAME?.trim() || 'agent';

const CALL_LIMIT_MS = Number(process.env.LUDUS_HUB_CALL_LIMIT_MS || 45_000);  // 一次工具调用最多占这么久：有的客户端 60 秒就算超时
const JOB_KILL_MS = 20 * 60_000;
const MAX_IMAGES = 8;
const IMAGE_SIDE = 1568;        // 长边超过这个就缩（模型那边也会缩到这么大）
const IMAGE_BYTES = 1_500_000;  // PNG 超过这么大就转成 JPEG
const SESSION_IDLE_MS = 12 * 3600_000;

// ---- 日志 ----
if (opt.log) fs.mkdirSync(path.dirname(path.resolve(opt.log)), { recursive: true });
const logFile = opt.log ? fs.createWriteStream(path.resolve(opt.log), { flags: 'a' }) : null;
function log(...args) {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  const line = `[${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}] ${args.join(' ')}`;
  console.log(line);
  logFile?.write(line + '\n');
}

// ---- token（局域网模式用）----
function loadToken() {
  const file = path.resolve(opt['token-file']);
  if (!fs.existsSync(file)) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, crypto.randomBytes(24).toString('hex'));
    log(`新的 token 写在 ${file}`);
  }
  return fs.readFileSync(file, 'utf8').trim();
}
const TOKEN = opt.lan || opt.token ? loadToken() : null;

// ---- 子进程：一次只跑一个 vp_player ----
// vp_player 在 Claude Code 里跑时，看 CLAUDE_CODE_SESSION_ID 换没换自己发 hello。hub 是从哪儿起的跟玩家无关，
// 不把它传下去：hub 按 MCP 会话自己发 hello（gameCall）。
// 向聊天服务报到也是 hub 按 MCP 会话自己报（chatPresence），vp_player 不用再报（LUDUS_PRESENCE=0）。
// vp_player 自己找聊天服务的命令（skip on/off）跟着 hub 的 --chat-port 走（LUDUS_PORT）。
const CHILD_ENV = { ...process.env, PYTHONIOENCODING: 'utf-8', PYTHONUTF8: '1', LUDUS_PRESENCE: '0', LUDUS_PORT: String(Number(opt['chat-port'])) };
delete CHILD_ENV.CLAUDE_CODE_SESSION_ID;

// 「再等一下」怎么说：MCP 的客户端调 wait 工具，信箱的客户端再写一个 wait
const AGAIN_MCP = { call: '调 wait', once: '再 wait 一次', hello: '调 play 写 hello' };
const AGAIN_MAIL = { call: '再写一个 wait', once: '再写一个 wait', hello: '写一个 hello' };

// 同一时间只有一个会话在玩（聊天服务记着是谁，见 chat-service/server.mjs「现在在玩的那一个」）。
// 只看不动游戏的命令不接手，别人在玩时也照跑；别的命令先问聊天服务，别人在玩就不跑，回一句交班的话。和 vp_player 的 READ_ONLY 一样。
const READ_ONLY = new Set(['status', 'log', 'look', 'home', '近况', 'acts', 'plan', 'wish', 'screen', 'shot', 'tip', 'read',
  'recap', 'note', 'feedback']);
class Job {
  /** player：这一步是谁的（presenceOf 的 { id, label }）。vp_player / vp_memory 按 LUDUS_PLAYER 找它自己的玩家文件夹 */
  constructor(script, args, player = null) {
    this.label = args.join(' ');
    this.owner = player?.id ?? '';
    this.out = '';
    this.taken = 0;
    this.code = null;
    const env = player ? { ...CHILD_ENV, LUDUS_PLAYER: player.label, LUDUS_PLAYER_ID: player.id } : CHILD_ENV;
    this.proc = spawn(opt.python, ['-X', 'utf8', '-u', script, '--data', DATA, ...args], { cwd: ROOT, windowsHide: true, env });
    for (const stream of [this.proc.stdout, this.proc.stderr]) {
      const dec = new StringDecoder('utf8');
      stream.on('data', (b) => { this.out += dec.write(b); });
      stream.on('end', () => { this.out += dec.end(); });
    }
    this.done = new Promise((resolve) => {
      this.proc.on('error', (err) => { this.out += `\n（跑不起来 ${opt.python}：${err.message}）`; this.code = -1; resolve(); });
      this.proc.on('close', (code) => { if (this.code === null) this.code = code ?? -1; resolve(); });
    });
    this.killer = setTimeout(() => this.proc.kill(), JOB_KILL_MS);
    this.done.then(() => clearTimeout(this.killer));
  }

  /** 等到跑完或者到点，返回跑完没有 */
  async until(deadline) {
    if (this.code !== null) return true;
    const left = deadline - Date.now();
    if (left <= 0) return false;
    let timer;
    const finished = await Promise.race([
      this.done.then(() => true),
      new Promise((r) => { timer = setTimeout(() => r(false), left); }),
    ]);
    clearTimeout(timer);
    // close 之后 stdout 的 end 可能还差一拍
    if (finished) await new Promise((r) => setImmediate(r));
    return finished;
  }

  /** 上次拿走以后新来的输出 */
  take() {
    const text = this.out.slice(this.taken);
    this.taken = this.out.length;
    return text;
  }
}

class Game {
  constructor() {
    this.tail = Promise.resolve();
    this.job = null;       // 上一条命令：到点时还没走完，接着在后台跑，输出留给下一次
    this.leftover = new Map();  // 玩家 id → 它上一步后来的结果（中间别的玩家来过，先替它收着）
    this.helpCache = new Map();
  }

  /** 一次只让一个调用碰游戏；排队等到 deadline 还没轮到就返回 null */
  async exclusive(deadline, fn) {
    let release;
    const mine = new Promise((r) => { release = r; });
    const prev = this.tail;
    this.tail = prev.then(() => mine);
    let timer;
    const got = await Promise.race([
      prev.then(() => true),
      new Promise((r) => { timer = setTimeout(() => r(false), Math.max(0, deadline - Date.now())); }),
    ]);
    clearTimeout(timer);
    if (!got) { prev.then(release); return null; }
    try { return await fn(); } finally { release(); }
  }

  /** 跑一条命令。返回 { text, finished, code, hint }；到点没走完，finished 为 false，进程留在后台，
   *  hint 是「再等一下」那句（放在回话最后，面板上的话后面） */
  async run(args, { deadline, script = PLAYER, again = AGAIN_MCP, player = null } = {}) {
    const owner = player?.id ?? '';
    const result = await this.exclusive(deadline, async () => {
      let before = '';
      if (this.job && this.job.owner !== owner) {
        // 上一步是别的玩家的：它后来的结果留给它自己，不给这一个（谁的是谁的）
        const job = this.job;
        if (!(await job.until(deadline))) {
          return { text: '', finished: false, code: null, hint: `（游戏这会儿腾不出手：上一步是别人的，还在走。再等一下：${again.call}。）` };
        }
        this.job = null;
        const rest = job.take();
        if (rest.trim()) this.leftover.set(job.owner, { label: job.label, text: rest });
      }
      const left = this.leftover.get(owner);
      if (left) {
        this.leftover.delete(owner);
        before = `（上一步「${left.label}」后来的结果：）\n${left.text.trimEnd()}\n\n`;
      }
      if (this.job) {
        const job = this.job;
        const finished = await job.until(deadline);
        const text = job.take();
        if (!finished) {
          return { text: before + text, finished: false, code: null, hint: `（上一步「${job.label}」还没走完，游戏还在往下走。再等一下：${again.call}。）` };
        }
        this.job = null;
        if (text.trim()) {
          if (args[0] === 'wait') return { text: before + text, finished: true, code: job.code };  // 上一步的结尾就是现在轮到你
          before += `（上一步「${job.label}」后来的结果：）\n${text.trimEnd()}\n\n`;
        }
      }
      const job = new Job(script, args, player);
      const finished = await job.until(deadline);
      const text = before + job.take();
      if (finished) return { text, finished, code: job.code };
      this.job = job;
      return { text, finished, code: null, hint: `（还没轮到，再等一下：游戏还在往下走（${job.label}）。${again.call} 接着等。）` };
    });
    return result ?? { text: '', finished: false, code: null, hint: `（上一步还在跑，游戏这会儿腾不出手。再等一下：${again.call}。）` };
  }

  /** vp_player（或 vp_memory）有哪些子命令：读 --help 的 {a,b,c}，文件改了就重读 */
  commands(script = PLAYER) {
    let mtime;
    try { mtime = fs.statSync(script).mtimeMs; } catch { return { names: [], help: '' }; }
    const hit = this.helpCache.get(script);
    if (hit && hit.mtime === mtime) return hit;
    const r = spawnSync(opt.python, ['-X', 'utf8', script, '--help'], { encoding: 'utf8', windowsHide: true, timeout: 20_000 });
    const help = r.stdout || '';
    const names = (/\{([a-z][^{}\s]*)\}/.exec(help)?.[1] ?? '').split(',').filter(Boolean);  // 里面有中文别名（近况），不能用 \w
    const entry = { mtime, names, help };
    this.helpCache.set(script, entry);
    return entry;
  }

  /** 会话开头清空「见过的东西」的命令：参数指定的，或者 --help 里写着「会话」和「清空 / 见过」的那条 */
  freshCommand() {
    const want = opt['fresh-command'];
    if (want === 'none') return null;
    const { names, help } = this.commands();
    if (want) return names.includes(want) ? want : null;
    for (const line of help.split('\n')) {
      const m = /^\s{2}([a-z][\w-]*)\b(.*)$/.exec(line);
      if (m && names.includes(m[1]) && /会话/.test(m[2]) && /清空|见过|忘/.test(m[2])) return m[1];
    }
    // 插件那边（Sights.cs）写的是「新会话开头 vp_player 发 hello」
    for (const name of ['hello', 'fresh', 'newsession', 'new-session', 'forget']) {
      if (names.includes(name)) return name;
    }
    return null;
  }
}

const game = new Game();

// ---- 聊天面板（chat-service/ 的聊天服务）----
async function chatHistory() {
  const res = await fetch(`${CHAT}/api/history`, { signal: AbortSignal.timeout(5000) });
  if (!res.ok) throw new Error(`聊天服务回了 ${res.status}`);
  return res.json();
}

// 这个会话在聊天服务那边叫什么：MCP 会话的标题栏写「hub：名字」，信箱的写它自己报的名字。
// MCP 会话的名字是 play hello 名字 报过的那个，没报过是客户端名。Claude Code 报的客户端名都是 claude-code，
// 不报名字的话所有 Claude Code 会话共用一个玩家文件夹，所以留了 hello 名字 这条路
const presenceOf = (sess) => (sess.mailbox
  ? { id: sess.id, label: sess.name }
  : { id: `hub:${sess.id}`, label: `hub：${sess.playerName || sess.client || '?'}` });

async function chatSay(sess, text) {
  const token = fs.readFileSync(path.join(DATA, 'token'), 'utf8').trim();
  const res = await fetch(`${CHAT}/api/reply`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ text, client: presenceOf(sess) }),
    signal: AbortSignal.timeout(10_000),
  });
  if (res.status === 409) return { blocked: await res.json() };  // 别人在玩：这句不进面板
  if (!res.ok) throw new Error(`聊天服务回了 ${res.status}：${await res.text()}`);
  return res.json();
}

/** 要动游戏之前问聊天服务（claim：'hello' 接手、'act' 动游戏、null 只报到）。
 *  回 { you: 'active' | 'blocked' | 'idle', active, handedOver }；聊天服务没开、没 token 回 null（照常玩，最多等 1 秒）。 */
async function chatClaim(sess, claim) {
  if (!sess.id) return null;
  try {
    const token = fs.readFileSync(path.join(DATA, 'token'), 'utf8').trim();
    const res = await fetch(`${CHAT}/api/presence`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ ...presenceOf(sess), ...(claim ? { claim } : {}) }),
      signal: AbortSignal.timeout(1000),
    });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/** 被接手（或者别人在玩）时回的那一句 */
function handoverText(turn, sess) {
  const who = turn?.active?.label || '别人';
  const how = (sess.mailbox ? AGAIN_MAIL : AGAIN_MCP).hello;
  return turn?.handedOver ? `（${who}接手了，你先歇着。要接回来就${how}）` : `（${who}正在玩，你先歇着。要接手就${how}）`;
}

/** 向聊天服务报到：会话开始、每次调用工具时报，断开时报走了。有会话挂着，游戏里的面板才出来。
 *  聊天服务没开也不要紧，不等它（最多 1 秒，不 await）。 */
function chatPresence(sess, gone = false) {
  if (!sess.id) return;
  let token;
  try { token = fs.readFileSync(path.join(DATA, 'token'), 'utf8').trim(); } catch { return; }
  fetch(`${CHAT}/api/presence`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ ...presenceOf(sess), ...(gone ? { gone: true } : {}) }),
    signal: AbortSignal.timeout(1000),
  }).catch(() => {});
}

const hhmm = (ts) => {
  const d = new Date(ts);
  return Number.isNaN(d.getTime()) ? '' : `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
};

/** 人自上次以来在面板上说的话（每个会话各记各的读到哪）。别人在玩时不给：那是在跟在玩的那一个说话 */
async function panelNews(sess, turn = null) {
  const mine = !turn?.active || turn.you === 'active';
  let msgs;
  try {
    msgs = await chatHistory();
    sess.chatDown = false;
  } catch {
    if (sess.chatDown) return '';
    sess.chatDown = true;
    return `\n（聊天面板连不上：${USER_NAME} 在面板上说的话这会儿收不到，say 也发不出去。）`;
  }
  let fresh;
  if (sess.chatSeen === null) {
    // 刚连上：只带连上前十分钟以内的，更早的是上一局的话
    const since = sess.started - 10 * 60_000;
    fresh = msgs.filter((m) => m.from === 'user' && Date.parse(m.ts) >= since);
  } else {
    fresh = msgs.filter((m) => m.from === 'user' && m.id > sess.chatSeen);
  }
  const last = msgs.at(-1)?.id;
  if (last !== undefined) sess.chatSeen = Math.max(sess.chatSeen ?? 0, last);
  if (!fresh.length || !mine) return '';
  return `\n\n-- ${USER_NAME} 在面板上说 --\n` + fresh.map((m) => `（${hhmm(m.ts)}）${m.text}`).join('\n');
}

// ---- 图：输出里 data/selfplay/ 下的 PNG 跟着回话一起发 ----
const ATTACH_ALL = new Set(['shot', 'look']);  // 这些命令输出里的图都发；别的命令只发「第一次」那几行的
// 「第一次」那几行是 vp_player 的 fmt_sights 打的：「[第一次见] 人」「[第一次来] 地方」「[CG]」「[女儿换上了 …]」「[第一次遇到] 怪物」，
// 结尾都是「（图：…png；… 那边也看得到）」。只认「第一次见」的话，地方、CG、女儿、怪物的图都发不出去
const SIGHT_LINE = /（图：|第一次见/;
// 玩家文件夹名常带全角括号、逗号、空格（比如 agent（claude.ai）、agent（终端）、agent（桌面 app））：绝对路径里这些字都要认，
// 排除了的话，玩家文件夹里的图一张都发不出去。到这一行的第一个 .png 为止
const PNG_ABS = /[A-Za-z]:[\\/][^\r\n"<>|?*:]*?\.png/gi;
const PNG_REL = /(?<![\w\\/:.-])((?:selfplay[\\/])?scenes[\\/][^\s"'<>|?*（）()「」，。；]+?\.png)/gi;

function inside(file, dir) {
  const rel = path.relative(dir, file);
  return rel && !rel.startsWith('..') && !path.isAbsolute(rel);
}

/** 这个会话的玩家文件夹（selfplay/players.json 在才有；不在就是 selfplay/）：相对路径的图在这里找 */
function playerDirOf(sess) {
  try {
    const doc = JSON.parse(fs.readFileSync(path.join(SELFPLAY, 'players.json'), 'utf8'));
    const dir = doc?.players?.[presenceOf(sess).label]?.dir;
    return dir ? path.join(SELFPLAY, dir) : SELFPLAY;
  } catch {
    return SELFPLAY;
  }
}

function findImages(text, command, base = SELFPLAY) {
  const all = ATTACH_ALL.has(command);
  const found = [];
  const seen = new Set();
  for (const line of text.split('\n')) {
    if (!all && !SIGHT_LINE.test(line)) continue;
    const hits = [...line.matchAll(PNG_ABS)].map((m) => m[0]);
    for (const m of line.matchAll(PNG_REL)) hits.push(m[1]);
    for (const hit of hits) {
      const file = path.isAbsolute(hit)
        ? path.resolve(hit)
        : path.resolve(/^selfplay[\\/]/i.test(hit) ? DATA : base, hit);
      if (!inside(file, SELFPLAY)) continue;
      const key = file.toLowerCase().replace(/-marked\.png$/, '.png');  // 同一张图的原图和画了编号的只发一张
      if (seen.has(key)) continue;
      seen.add(key);
      found.push({ file, caption: line.trim().slice(0, 120) });
    }
  }
  return found;
}

function pngSize(buf) {
  if (buf.length < 24 || buf.readUInt32BE(0) !== 0x89504e47) return null;
  return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20) };
}

/** 一张图发哪个文件：小的发原图，太大就用 ffmpeg 缩到长边 1568、转 JPEG（缓存在 cache/）。返回 { file, mimeType } 或 { why } */
function imageFile(file) {
  let stat;
  try { stat = fs.statSync(file); } catch { return { why: '找不到这张图' }; }
  if (!stat.isFile()) return { why: '不是文件' };
  const size = pngSize(fs.readFileSync(file));
  if (!size) return { why: '不是 PNG' };
  if (Math.max(size.w, size.h) <= IMAGE_SIDE && stat.size <= IMAGE_BYTES) return { file, mimeType: 'image/png' };
  fs.mkdirSync(CACHE, { recursive: true });
  const key = crypto.createHash('sha1').update(`${file}|${stat.size}|${stat.mtimeMs}`).digest('hex').slice(0, 16);
  const out = path.join(CACHE, `${key}.jpg`);
  if (!fs.existsSync(out)) {
    const scale = size.w >= size.h ? `scale='min(${IMAGE_SIDE},iw)':-2` : `scale=-2:'min(${IMAGE_SIDE},ih)'`;
    const r = spawnSync(opt.ffmpeg, ['-v', 'error', '-y', '-i', file, '-vf', scale, '-q:v', '3', out],
      { windowsHide: true, timeout: 30_000, encoding: 'utf8' });
    if (r.status !== 0 || !fs.existsSync(out)) {
      try { fs.unlinkSync(out); } catch {}
      if (stat.size <= 4_000_000) return { file, mimeType: 'image/png' };
      return { why: `图太大，又没缩成（ffmpeg：${(r.error?.message || r.stderr || '').trim().split('\n').at(-1) || r.status}）` };
    }
  }
  return { file: out, mimeType: 'image/jpeg' };
}

/** 读一张图（MCP 的图片内容）。返回 { data, mimeType } 或 { why } */
function loadImage(file) {
  const got = imageFile(file);
  if (got.why) return got;
  return { data: fs.readFileSync(got.file).toString('base64'), mimeType: got.mimeType };
}

function pruneCache() {
  try {
    for (const name of fs.readdirSync(CACHE)) {
      const file = path.join(CACHE, name);
      if (Date.now() - fs.statSync(file).mtimeMs > 7 * 86400_000) fs.unlinkSync(file);
    }
  } catch {}
}

// ---- 工具：定义在 tools.mjs（和 stdio 小桥共用）----

/** feedback / note：vp_player 接上了就走 vp_player，还没接就直接调 vp_memory.py；两边都没有就不列 */
function memoryScript(command) {
  if (game.commands().names.includes(command)) return PLAYER;
  if (fs.existsSync(MEMORY) && game.commands(MEMORY).names.includes(command)) return MEMORY;
  return null;
}

function memoryTools() {
  return MEMORY_TOOLS.filter((t) => memoryScript(t.name));
}

function toolList() {
  return [...GAME_TOOLS, ...memoryTools(), PLAY_TOOL, SAY_TOOL, PANEL_TOOL].map(pub);
}

/** 命令行切成参数：空格分开，引号里的算一个 */
function splitCommand(s) {
  const out = [];
  const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
  let m;
  while ((m = re.exec(s))) out.push(m[1] ?? m[2] ?? m[3]);
  return out;
}

function makeServer(sess) {
  const server = new Server(
    { name: 'ludus-hub', title: 'Ludus hub', version: VERSION },
    { capabilities: { tools: {} }, instructions: INSTRUCTIONS },
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: toolList() }));

  server.setRequestHandler(CallToolRequestSchema, async (req) => {
    sess.last = Date.now();
    const name = req.params.name;
    const a = req.params.arguments ?? {};
    // play hello 名字：接手那一下会用新名字报到。这里先报的那一次还是旧名字，常常后到，
    // 把名字和在玩的人冲回旧的，要等下一次调工具才对。这一次就不先报了
    const renaming = name === 'play' && /^\s*hello\s+\S/.test(String(a.command ?? ''));
    if (!renaming) chatPresence(sess);
    const deadline = Date.now() + CALL_LIMIT_MS;
    try {
      if (name === 'say') return mcpResult(await toolSay(sess, String(a.text ?? '')));
      if (name === 'panel') return mcpResult(await toolPanel(sess, a.n));
      if (name === 'play') return mcpResult(await runLine(sess, String(a.command ?? ''), deadline));

      const tool = GAME_TOOLS.find((t) => t.name === name) ?? MEMORY_TOOLS.find((t) => t.name === name);
      if (!tool) return textResult(`没有这个工具：${name}`, true);
      // 少了必填的参数：不检查的话会照样拼进命令，回「没找到「undefined」」（客户端没读工具参数、先传 {} 时会碰到）
      const missing = (tool.inputSchema.required ?? []).filter((k) => a[k] === undefined || a[k] === null || a[k] === '');
      if (missing.length) {
        const want = missing.map((k) => `${k}（${tool.inputSchema.properties?.[k]?.description ?? ''}）`).join('、');
        return textResult(`${name} 少了参数：${want}。`, true);
      }
      const script = MEMORY_TOOLS.includes(tool) ? memoryScript(tool.name) ?? PLAYER : PLAYER;
      return mcpResult(await gameCall(sess, tool.argv(a).map(String), script, deadline));
    } catch (err) {
      log(`工具 ${name} 出错：`, err.stack || err.message);
      return textResult(`hub 这边出错了：${err.message}`, true);
    }
  });

  return server;
}

function textResult(text, isError = false) {
  return { content: [{ type: 'text', text }], ...(isError ? { isError: true } : {}) };
}

// 下面几个函数回的是「回话」：{ text, tail, images, isError }。text 是游戏的字，images 是里面该发的图，
// tail 放最后（面板上的话、「再等一下」）。MCP 和信箱各自把它排成自己的样子（mcpResult / mailText）。

/** 回话 → MCP 的结果：字在前，图跟在后面 */
function mcpResult({ text, tail = '', images = [], isError }) {
  const content = [{ type: 'text', text: (text + tail).replace(/^\n+/, '') }];
  for (const [i, img] of images.entries()) {
    if (i >= MAX_IMAGES) {
      content.push({ type: 'text', text: `（还有 ${images.length - MAX_IMAGES} 张没附上：look 名字 再看）` });
      break;
    }
    const got = loadImage(img.file);
    if (got.why) {
      content.push({ type: 'text', text: `（${path.basename(img.file)} 没附上：${got.why}）` });
      continue;
    }
    content.push({ type: 'text', text: `图：${img.caption}` });
    content.push({ type: 'image', data: got.data, mimeType: got.mimeType });
  }
  return { content, ...(isError ? { isError: true } : {}) };
}

function playHelp() {
  const { help } = game.commands();
  let text = help || '（读不到 vp_player 的 --help）';
  if (fs.existsSync(MEMORY)) text += '\n\n' + (game.commands(MEMORY).help || '');
  return text + '\n\n（在这里 say 走 hub 的 say，panel 翻面板上最近的话；wait 一次最多等 40 秒。）';
}

/** 一行命令，写法和 vp_player 的命令行一样：MCP 的 play 和文字信箱都走这里 */
async function runLine(sess, line, deadline, again = AGAIN_MCP) {
  line = line.trim();
  const argv = splitCommand(line);
  if (!argv.length || ['help', '-h', '--help'].includes(argv[0])) return { text: playHelp() };
  if (argv.some((x) => x === '--data' || x.startsWith('--data='))) return { text: '这里不能换 data 文件夹。' };
  if (argv[0] === 'say') return toolSay(sess, line.replace(/^\S+\s*/, ''));  // 话原样发，引号不拆
  if (argv[0] === 'panel') return toolPanel(sess, argv[1]);
  if (argv[0] === 'wait') {
    // 等再久也只等 40 秒：等得更久的话客户端那边先超时了
    const i = argv.indexOf('--timeout');
    const want = i >= 0 ? Number(argv[i + 1]) : NaN;
    if (i >= 0) argv.splice(i, 2);
    argv.push('--timeout', String(want > 0 ? Math.min(want, WAIT_SECONDS) : WAIT_SECONDS));
  }
  return gameCall(sess, argv, memoryScript(argv[0]) ?? PLAYER, deadline, again);
}

async function gameCall(sess, argv, script, deadline, again = AGAIN_MCP) {
  const fresh = game.freshCommand();
  const verb = argv[0];
  // hello 名字：MCP 会话以后用这个名字（面板上的名字、玩家文件夹都跟着它）。在接手之前换，聊天服务就把它当新来的玩家
  if (verb === 'hello' && argv.length > 1 && !sess.mailbox) sess.playerName = argv.slice(1).join(' ').trim().slice(0, 40) || sess.playerName;
  // 接手：hello，或者 MCP 会话第一条动游戏的命令（hub 先替它 hello）。只看的命令不接手，也不替它 hello。
  let claim = null;
  if (verb === 'hello' || (fresh && verb === fresh)) claim = 'hello';
  else if (!READ_ONLY.has(verb) && !(verb === 'talent' && argv.length === 1)) claim = sess.freshDone ? 'act' : 'hello';  // talent 不写名字只是看看
  const turn = await chatClaim(sess, claim);
  if (turn?.you === 'blocked') {
    log(`会话 ${sess.id.slice(0, 16)}：${verb} 没跑（${turn.active?.label} 在玩）`);
    return { text: handoverText(turn, sess) };
  }
  let head = '';
  if (claim) {
    if (!sess.freshDone && fresh && verb !== fresh) {
      const r = await game.run([fresh], { deadline, again, player: presenceOf(sess) });
      log(`会话 ${sess.id.slice(0, 8)} 开头：${fresh} → ${r.code}`);
      if (r.text.trim()) head = r.text.trimEnd() + '\n\n';
    }
    sess.freshDone = true;
  }
  const r = await game.run(argv, { deadline, script, again, player: presenceOf(sess) });
  let text = head + r.text.trimEnd();
  let hint = r.hint;
  if (r.finished && verb === 'wait' && r.code === 2) hint = `（还没轮到，再等一下：${again.once}。）`;
  if (!text.trim() && !hint) text = '（没有输出）';
  const tail = await panelNews(sess, turn) + (hint ? `\n${hint}` : '');
  return { text, tail, images: findImages(r.text, verb, playerDirOf(sess)) };
}

async function toolSay(sess, text, deadline = Date.now() + CALL_LIMIT_MS) {
  text = text.trim();
  if (!text) return { text: '要说的话是空的。', isError: true };
  // MCP 会话的第一句话也算来玩了：和第一条动游戏的命令一样，先替它 hello。不然别人还挂着在玩时，
  // 新会话先 say 会被挡回来，回话里还带出那个人的名字。信箱不自动 hello
  let head = '', tail = '';
  const fresh = game.freshCommand();
  if (!sess.mailbox && !sess.freshDone && fresh) {
    const r = await gameCall(sess, [fresh], PLAYER, deadline);
    if (r.text && r.text !== '（没有输出）') head = r.text.trimEnd() + '\n\n';
    tail = r.tail || '';  // hello 那一下顺手带回来的面板上的话，别丢了
  }
  try {
    const msg = await chatSay(sess, text);
    if (msg.blocked) return { text: `${head}${handoverText(msg.blocked, sess)}（这句没进面板）`, tail };
    return { text: `${head}说出去了（面板 #${msg.id}）`, tail };
  } catch (err) {
    return { text: `没说出去：${err.message}（聊天面板开着吗？）`, isError: true };
  }
}

async function toolPanel(sess, n) {
  let msgs;
  try { msgs = await chatHistory(); } catch (err) { return { text: `聊天面板连不上：${err.message}`, isError: true }; }
  const last = msgs.at(-1)?.id;
  if (last !== undefined) sess.chatSeen = Math.max(sess.chatSeen ?? 0, last);
  const shown = msgs.slice(-Math.min(100, Math.max(1, Number(n) || 20)));
  if (!shown.length) return { text: '（面板上这一局还没有话）' };
  const me = presenceOf(sess).id;
  const who = (m) => (m.from === 'user' ? USER_NAME : m.client === me ? '你' : m.who || AGENT_NAME);
  return { text: shown.map((m) => `（${hhmm(m.ts)}）${who(m)}：${m.text}`).join('\n') };
}

// ---- 文字信箱：给只能读写文件、跑不了命令的客户端（比如 claude.ai 隔着桌面 app 的桥）----
// 客户端在 mailbox/in/ 里写一个小文件：一行命令（写法和 play 一样），可以再加一行「# 名字」（面板上显示的名字）。
// hub 等文件不再变了才拿（挪进 work/ 读完就删），照 play 跑，回话写成 out/ 里的同名 .txt（先写临时文件再改名）。
// 排队、45 秒、报到、say 进面板、面板上的话都和 MCP 共用；图写成这台电脑上的路径，客户端自己去读。
// 一个名字就是一个会话：不自动 hello（hub 重启不该把见过的东西清掉），客户端开头自己写 hello；bye 是走了。
// 给客户端的说明正本是 mailbox-README.txt，启动时填上这台电脑上的名字和路径（mailboxReadme），写到 data/selfplay/README.txt（data/ 不进 git）。
const MAILBOX = path.join(SELFPLAY, 'mailbox');
const MAIL_IN = path.join(MAILBOX, 'in');
const MAIL_OUT = path.join(MAILBOX, 'out');
const MAIL_WORK = path.join(MAILBOX, 'work');
const MAIL_POLL_MS = 150;
const MAIL_SETTLE_MS = 300;  // 大小和修改时间这么久没变，才算写完（可能是别的机器写过来的，写到一半）
const MAIL_KEEP = 50;        // out/ 只留最近这么多个
const MAIL_DEFAULT_NAME = '文字信箱';
const mailSessions = new Map();  // 名字 → sess
const mailSeen = new Map();      // in/ 里的文件名 → { size, mtime, since }
const mailRunning = new Set();   // 正在跑的信（名字，不带扩展名）：out/ 里有它的 .running 标记

function mailSession(name) {
  let sess = mailSessions.get(name);
  if (!sess) {
    sess = { id: `mailbox:${name}`, mailbox: true, name, started: Date.now(), last: Date.now(), chatSeen: null, chatDown: false, freshDone: true };
    mailSessions.set(name, sess);
  }
  return sess;
}

// 还在写的临时文件不拿
const mailIgnored = (name) => name.startsWith('.') || /\.(tmp|part|partial|crdownload|swp)$|~$/i.test(name);

function scanMailbox() {
  let names;
  try { names = fs.readdirSync(MAIL_IN); } catch { return; }
  const now = Date.now();
  const ready = [];
  for (const name of names) {
    if (mailIgnored(name)) continue;
    let st;
    try { st = fs.statSync(path.join(MAIL_IN, name)); } catch { continue; }
    if (!st.isFile()) continue;
    const seen = mailSeen.get(name);
    if (!seen || seen.size !== st.size || seen.mtime !== st.mtimeMs) {
      mailSeen.set(name, { size: st.size, mtime: st.mtimeMs, since: now });
      continue;
    }
    if (now - seen.since < MAIL_SETTLE_MS) continue;
    if (st.size === 0 && now - seen.since < 10_000) continue;  // 有的写法先建空文件再写
    ready.push({ name, mtime: st.mtimeMs });
  }
  for (const name of mailSeen.keys()) if (!names.includes(name)) mailSeen.delete(name);
  ready.sort((a, b) => a.mtime - b.mtime || a.name.localeCompare(b.name));
  for (const { name } of ready) claimMail(name);  // 按写的先后进队（game.exclusive 先来先跑）
}

function claimMail(name) {
  const work = path.join(MAIL_WORK, `${process.pid}-${name}`);
  try { fs.renameSync(path.join(MAIL_IN, name), work); } catch { return; }  // 还被占着，或者别的 hub 拿走了
  mailSeen.delete(name);
  let raw;
  try { raw = fs.readFileSync(work, 'utf8'); } catch (err) { log(`信箱 ${name} 读不了：${err.message}`); return; }
  try { fs.unlinkSync(work); } catch {}
  const base = name.replace(/\.[^.]*$/, '') || name;
  handleMail(base, raw).catch((err) => log(`信箱 ${base} 出错：`, err.stack || err.message));
}

// 「在跑」标记：拿走信、开始跑时在 out/ 里放一个 名字.running（写着命令和开始的时间），回话写好以后删掉。
// 客户端列一下 out/ 就知道跑到哪了，不用去拿还没有的文件（隔着桥去拿还没有的文件会报「文件不存在」）。
// hub 跑到一半被停了，标记会留下：下次启动时改成一份回话，说上次没跑完（startMailbox），客户端不会一直等。
const markerOf = (base) => path.join(MAIL_OUT, `${base}.running`);

async function handleMail(base, raw) {
  const deadline = Date.now() + CALL_LIMIT_MS;
  const outFile = path.join(MAIL_OUT, `${base}.txt`);
  try { fs.unlinkSync(outFile); } catch {}  // 同名的旧回话先清掉，免得被当成这次的
  const lines = raw.replace(/^﻿/, '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  const name = lines.find((l) => l.startsWith('#'))?.replace(/^#+/, '').trim().slice(0, 40) || MAIL_DEFAULT_NAME;
  const commands = lines.filter((l) => !l.startsWith('#'));
  mailRunning.add(base);
  try {
    fs.writeFileSync(markerOf(base), `在跑：${commands.join(' ／ ') || '（没有命令）'}\n开始：${new Date().toLocaleTimeString('zh-CN', { hour12: false })}\n`);
  } catch (err) {
    log(`信箱 ${base} 的在跑标记没写上：${err.message}`);
  }
  try {
    await answerMail(base, name, commands, deadline, outFile);
  } finally {
    try { fs.unlinkSync(markerOf(base)); } catch {}
    mailRunning.delete(base);
  }
  pruneMailOut();
}

async function answerMail(base, name, commands, deadline, outFile) {
  let text;
  if (commands.length !== 1) {
    text = commands.length
      ? `一个文件只放一行命令，这次一行都没跑（写了 ${commands.length} 行）。分成几个文件，一个一个写。\n`
      : '文件里没有命令：写一行，比如 wait。\n';
  } else {
    const line = commands[0];
    const verb = splitCommand(line)[0];
    const sess = mailSession(name);
    sess.last = Date.now();
    log(`信箱 ${base}（${name}）：${line}`);
    if (verb === 'bye') {
      chatPresence(sess, true);
      mailSessions.delete(name);
      text = '好，走了。面板上没人挂着就会收起来。下回再来，先写 hello。\n';
    } else {
      chatPresence(sess);
      if (verb === 'hello') Object.assign(sess, { started: Date.now(), chatSeen: null, chatDown: false });  // 新的一次：面板上的话从十分钟前带起
      try {
        text = mailText(await runLine(sess, line, deadline, AGAIN_MAIL));
      } catch (err) {
        log(`信箱 ${base} 出错：`, err.stack || err.message);
        text = `hub 这边出错了：${err.message}\n`;
      }
    }
  }
  await writeMailOut(outFile, text);
}

/** 回话 → 信箱的字：图写成这台电脑上的完整路径（太大的写缩小过的 JPEG），面板上的话和「再等一下」放最后 */
function mailText({ text, tail = '', images = [] }) {
  let out = text;
  if (images.length) {
    out += '\n\n-- 图（这台电脑上的文件，想看就去读）--';
    for (const img of images) {
      const got = imageFile(img.file);
      out += `\n${img.caption}\n  ${got.why ? `（${path.basename(img.file)} 没附上：${got.why}）` : got.file}`;
    }
  }
  return (out + tail).replace(/^\n+/, '') + '\n';
}

async function writeMailOut(file, text) {
  const tmp = path.join(MAIL_WORK, `${process.pid}-${crypto.randomUUID()}.tmp`);
  fs.writeFileSync(tmp, text);
  for (let i = 0; ; i++) {
    try { fs.renameSync(tmp, file); return; } catch (err) {
      // 客户端正好开着同名的旧文件读：等一下再换
      if (i >= 30) {
        log(`信箱回话写不进 ${file}：${err.message}`);
        try { fs.unlinkSync(tmp); } catch {}
        return;
      }
      await new Promise((r) => setTimeout(r, 100));
    }
  }
}

function pruneMailOut() {
  let names;
  try { names = fs.readdirSync(MAIL_OUT); } catch { return; }
  if (names.length <= MAIL_KEEP) return;
  const files = [];
  for (const name of names) {
    if (name.endsWith('.running') && mailRunning.has(name.slice(0, -'.running'.length))) continue;  // 正在跑的标记不清
    const file = path.join(MAIL_OUT, name);
    try { files.push({ file, t: fs.statSync(file).mtimeMs }); } catch {}
  }
  files.sort((a, b) => b.t - a.t);
  for (const { file } of files.slice(MAIL_KEEP)) try { fs.unlinkSync(file); } catch {}
}

/** 给客户端的说明：mailbox-README.txt 里的 {{名字}} 换成这台电脑上的值（认不得的照原样留着） */
function mailboxReadme() {
  const fill = {
    USER: USER_NAME,
    AGENT: AGENT_NAME,
    ROOT,                                       // 仓库的绝对路径：在这里跑 python -X utf8 tools\vp_player.py
    MAILBOX,                                    // 信箱的绝对路径
    HUB_URL: `http://127.0.0.1:${PORT}/mcp`,
    HUB_MODE: opt.lan ? '局域网模式，要 token' : TOKEN ? '只听本机，要 token' : '只听本机',
    NAMES_NOTE: namesNote() ? `\n${namesNote()}\n` : '',  // 名字还是占位的：请 agent 问问人、设上
  };
  return fs.readFileSync(path.join(HERE, 'mailbox-README.txt'), 'utf8').replace(/\{\{([A-Z_]+)\}\}/g, (m, key) => fill[key] ?? m);
}

function startMailbox() {
  for (const dir of [MAIL_IN, MAIL_OUT, MAIL_WORK]) fs.mkdirSync(dir, { recursive: true });
  // work/ 里只有一眨眼的临时文件；剩下很久的是上次停在半路的，清掉
  for (const name of fs.readdirSync(MAIL_WORK)) {
    const file = path.join(MAIL_WORK, name);
    try { if (Date.now() - fs.statSync(file).mtimeMs > 10 * 60_000) fs.unlinkSync(file); } catch {}
  }
  // 上次停在半路留下的「在跑」标记：没有回话的，写一份回话说没跑完（客户端在等这个文件）；有回话的只删标记
  for (const name of fs.readdirSync(MAIL_OUT)) {
    if (!name.endsWith('.running')) continue;
    const base = name.slice(0, -'.running'.length);
    const reply = path.join(MAIL_OUT, `${base}.txt`);
    try {
      if (!fs.existsSync(reply)) {
        const what = /^在跑：(.*)$/m.exec(fs.readFileSync(markerOf(base), 'utf8'))?.[1] ?? '';
        fs.writeFileSync(reply, `hub 上次停在了半路，这一条${what ? `（${what}）` : ''}没跑完，也不知道游戏那边做了一半没有。\n`
          + '先写一个 wait 看看现在怎么样，要的话再写一次。\n');
        log(`信箱 ${base}：上次没跑完，写了一份回话`);
      }
      fs.unlinkSync(markerOf(base));
    } catch (err) {
      log(`信箱 ${base} 的旧标记没处理掉：${err.message}`);
    }
  }
  try {
    const readme = mailboxReadme();
    const dest = path.join(SELFPLAY, 'README.txt');
    let old = null;
    try { old = fs.readFileSync(dest, 'utf8'); } catch {}
    if (old !== readme) fs.writeFileSync(dest, readme);
  } catch (err) {
    log(`给客户端的说明没复制过去：${err.message}`);
  }
  setInterval(scanMailbox, MAIL_POLL_MS);
  log(`文字信箱：${MAILBOX}`);
}

// ---- HTTP ----
const sessions = new Map();  // sessionId → { transport, server, sess }

function allowedHost(host) {
  if (opt.lan) return true;  // 局域网模式靠 token 挡
  return [`127.0.0.1:${PORT}`, `localhost:${PORT}`, `[::1]:${PORT}`].includes(String(host).toLowerCase());
}

function tokenOk(req) {
  const got = Buffer.from(String(req.headers.authorization || ''));
  const want = Buffer.from(`Bearer ${TOKEN}`);
  return got.length === want.length && crypto.timingSafeEqual(got, want);
}

function sendJson(res, status, obj, headers = {}) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...headers });
  res.end(JSON.stringify(obj));
}

const rpcError = (res, status, code, message) => sendJson(res, status, { jsonrpc: '2.0', error: { code, message }, id: null });

async function readBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 1024 * 1024) throw Object.assign(new Error('too large'), { status: 413 });
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks).toString('utf8');
  if (!raw) return undefined;
  try { return JSON.parse(raw); } catch { throw Object.assign(new Error('bad json'), { status: 400 }); }
}

async function handleMcp(req, res) {
  const body = req.method === 'POST' ? await readBody(req) : undefined;
  const sid = req.headers['mcp-session-id'];
  let entry = sid ? sessions.get(String(sid)) : undefined;

  if (!entry) {
    if (sid) return rpcError(res, 404, -32001, 'Session not found');
    const isInit = req.method === 'POST' && (Array.isArray(body) ? body.some(isInitializeRequest) : isInitializeRequest(body));
    if (!isInit) return rpcError(res, 400, -32000, 'Bad Request: No valid session ID provided');
    const sess = { id: '', started: Date.now(), last: Date.now(), chatSeen: null, chatDown: false, freshDone: false };
    const transport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => crypto.randomUUID(),
      onsessioninitialized: (id) => {
        sess.id = id;
        sessions.set(id, entry);
        const client = Array.isArray(body) ? '' : body?.params?.clientInfo?.name ?? '';
        sess.client = client;
        log(`新会话 ${id.slice(0, 8)}（${client || '?'}，来自 ${req.socket.remoteAddress}）`);
        chatPresence(sess);
      },
    });
    transport.onclose = () => {
      if (sess.id && sessions.delete(sess.id)) {
        log(`会话 ${sess.id.slice(0, 8)} 结束`);
        chatPresence(sess, true);
      }
    };
    const server = makeServer(sess);
    entry = { transport, server, sess };
    await server.connect(transport);
  }
  entry.sess.last = Date.now();
  await entry.transport.handleRequest(req, res, body);
}

const httpServer = http.createServer(async (req, res) => {
  try {
    if (!allowedHost(req.headers.host)) return sendJson(res, 421, { error: 'bad host' });
    const origin = req.headers.origin;
    if (origin && !opt['allow-origin'].includes(origin)) return sendJson(res, 403, { error: 'bad origin' });
    if (TOKEN && !tokenOk(req)) return sendJson(res, 401, { error: 'bad token' }, { 'WWW-Authenticate': 'Bearer' });

    const { pathname } = new URL(req.url, 'http://hub');
    if (pathname === '/mcp') return await handleMcp(req, res);
    if (req.method === 'GET' && pathname === '/health') {
      return sendJson(res, 200, { ok: true, version: VERSION, pid: process.pid, sessions: sessions.size, busy: !!game.job, data: DATA,
        mailbox: opt['no-mailbox'] ? null : MAILBOX });
    }
    if (req.method === 'GET' && pathname === '/') {
      res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
      return res.end('Ludus hub：MCP 在 /mcp（Streamable HTTP）。说明见仓库里的 mcp-hub/README.md 和 docs/连接.md。\n');
    }
    sendJson(res, 404, { error: 'not found' });
  } catch (err) {
    if (!res.headersSent) sendJson(res, err.status ?? 500, { error: err.message });
    if (!err.status) log('请求出错：', err.stack || err.message);
  }
});

// 很久没动静的会话收掉
setInterval(() => {
  for (const [id, { transport, sess }] of sessions) {
    if (Date.now() - sess.last > SESSION_IDLE_MS) {
      log(`会话 ${id.slice(0, 8)} 太久没动静，收掉`);
      transport.close().catch(() => {});
      sessions.delete(id);
      chatPresence(sess, true);
    }
  }
  for (const [name, sess] of mailSessions) {
    if (Date.now() - sess.last > SESSION_IDLE_MS) mailSessions.delete(name);  // 聊天服务那边早就过期了，不用再报
  }
}, 10 * 60_000).unref();

pruneCache();
const HOST = opt.lan ? '0.0.0.0' : '127.0.0.1';
httpServer.listen(PORT, HOST, () => {
  log(`Ludus hub ${VERSION}：http://${opt.lan ? os.hostname() : '127.0.0.1'}:${PORT}/mcp`
    + (opt.lan ? '（局域网模式，要 token）' : TOKEN ? '（只听本机，要 token）' : '（只听本机）'));
  log(`游戏工具 ${PLAYER}，data ${DATA}，聊天服务 ${CHAT}`);
  const fresh = game.freshCommand();
  log(fresh ? `会话开头先跑：${fresh}` : '会话开头不清空「见过的东西」（vp_player 还没有这条命令）');
  // 端口拿到了才开信箱：第二个 hub 起不来就退出，不会跟第一个抢信
  if (!opt['no-mailbox']) startMailbox();
});
httpServer.on('error', (err) => {
  log(`起不来：${err.message}`);
  process.exit(1);
});
