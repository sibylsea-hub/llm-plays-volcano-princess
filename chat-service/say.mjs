// 以现在这个玩家的名字往聊天页回一句。
//   node say.mjs "话"
//   echo 话 | node say.mjs -        （长文本或多行时用标准输入）
//
// 名字和 vp_player 一样认（vp_players.identity 的规矩，见下面 identity()）：话记在这个名字下，面板气泡上写它，
// 开着「一个玩家一个文件夹」时印象簿也按它收话（印象簿只收 who 是自己的话，不报名字的话谁的印象簿都进不去）。
// 说话也算动了游戏：别人在玩时这句不进面板。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.LUDUS_PORT || 8740);
const DATA = process.env.LUDUS_DATA || path.join(HERE, 'data');  // 和聊天服务指到同一个 data 文件夹
const token = fs.readFileSync(path.join(DATA, 'token'), 'utf8').trim();
const AGENT_NAME = process.env.LUDUS_AGENT_NAME?.trim() || 'agent';  // 没报过名字的会话的默认名前缀，和 vp_players 一样

/** 我是谁：{ id, label }。hub 起的带 LUDUS_PLAYER；Claude Code 里按会话分（hello 名字 起过名字的用那个名字，
 *  没起过按会话是从哪儿开的写，前面是 LUDUS_AGENT_NAME）；不在 Claude Code 里是「命令行」。
 *  和 tools/vp_players.py 的 identity() 一样，改的话两边一起改 */
function identity() {
  const envName = (process.env.LUDUS_PLAYER || '').trim();
  if (envName) return { id: process.env.LUDUS_PLAYER_ID || `player:${envName}`, label: envName };
  const sid = process.env.CLAUDE_CODE_SESSION_ID || '';
  let names = {};
  try { names = JSON.parse(fs.readFileSync(path.join(DATA, 'selfplay', 'session-names.json'), 'utf8')) || {}; } catch {}
  const named = typeof names[sid || 'cli'] === 'string' ? names[sid || 'cli'] : '';
  if (!sid) return { id: 'vp:cli', label: named || '命令行' };
  if (named) return { id: `claude:${sid}`, label: named };
  const entry = process.env.CLAUDE_CODE_ENTRYPOINT || '';
  const byEntry = new Map([['cli', `${AGENT_NAME}（终端）`], ['claude-desktop', `${AGENT_NAME}（桌面 app）`]]);
  const label = byEntry.get(entry) || (/remote|web/.test(entry) ? `${AGENT_NAME}（claude.ai）` : `Claude Code（${entry || '?'}）`);
  return { id: `claude:${sid}`, label };
}

const args = process.argv.slice(2);
const text = args[0] === '-' ? fs.readFileSync(0, 'utf8') : args.join(' ');
if (!text.trim()) {
  console.error('用法：node say.mjs "话"  或  node say.mjs -');
  process.exit(2);
}

const me = identity();
const res = await fetch(`http://127.0.0.1:${PORT}/api/reply`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
  body: JSON.stringify({ text, client: me }),
});
if (res.status === 409) {
  const turn = await res.json();
  console.error(`（${turn.active?.label || '别人'}正在玩，${me.label}这句没进面板）`);
  process.exit(1);
}
if (!res.ok) {
  console.error(`发送失败：${res.status} ${await res.text()}`);
  process.exit(1);
}
console.log(`已发送 #${(await res.json()).id}（${me.label}）`);
