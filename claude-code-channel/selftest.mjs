// 自测：找一个空闲端口起一份临时聊天服务（临时数据文件夹），用 MCP 客户端拉起 channel，
// 模拟面板发言和一次换月，确认 channel 推出了通知；再调 reply，确认回话进了聊天服务。
// 再设了名字（LUDUS_USER_NAME / LUDUS_AGENT_NAME）拉一个 channel，看说明和报到的名字跟着换了；
// 最后拉一个 hello 名字 起过名字的会话的 channel，看报到用的是那个名字。不碰 8740、8741。
//   node selftest.mjs
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { z } from 'zod';

function freePort() {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PORT = String(await freePort());
const ORIGIN = `http://127.0.0.1:${PORT}`;
const data = fs.mkdtempSync(path.join(os.tmpdir(), 'ludus-selftest-'));
const env = { ...process.env, LUDUS_PORT: PORT, LUDUS_DATA: data };
delete env.LUDUS_USER_NAME;   // 名字照默认的来（user / agent），这台电脑上设了也不算
delete env.LUDUS_AGENT_NAME;
delete env.CLAUDE_CODE_SESSION_ID;  // 在 Claude Code 里跑自测时别拿这个会话的号，名字那一段自己给
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const fail = (msg) => { console.error('失败：' + msg); process.exitCode = 1; };
const localIso = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60_000).toISOString().slice(0, 19);
console.log(`临时聊天服务 ${ORIGIN}，临时文件夹 ${data}`);

const server = spawn(process.execPath, [path.join(HERE, '..', 'chat-service', 'server.mjs')], { env, stdio: 'inherit' });
await sleep(800);

const client = new Client({ name: 'selftest', version: '0.0.1' });
const got = [];
client.setNotificationHandler(
  z.object({ method: z.literal('notifications/claude/channel'), params: z.object({ content: z.string(), meta: z.record(z.string()).optional() }) }),
  (n) => got.push(n.params),
);
await client.connect(new StdioClientTransport({ command: process.execPath, args: [path.join(HERE, 'ludus-channel.mjs')], env }));
console.log('channel 声明的能力：', JSON.stringify(client.getServerCapabilities()));
console.log('工具：', (await client.listTools()).tools.map((t) => t.name).join(', '));
if (!(client.getInstructions() || '').includes('你在陪 user 玩游戏')) fail('没设名字时，说明里写的不是 user');
if (!(client.getInstructions() || '').includes('名字还是占位的')) fail('没设名字时，说明里没提醒 agent 把名字设上');
await sleep(800);

const presence = async () => (await (await fetch(`${ORIGIN}/api/presence`)).json()).map((p) => p.label);
if (!(await presence()).includes('agent（终端）')) fail(`没设名字时，报到的名字不是「agent（终端）」：${JSON.stringify(await presence())}`);

await fetch(`${ORIGIN}/api/send`, {
  method: 'POST', headers: { 'Content-Type': 'application/json', Origin: ORIGIN }, body: JSON.stringify({ text: '测试：你收到了吗' }),
});
fs.appendFileSync(path.join(data, 'game-events.jsonl'), JSON.stringify({
  ts: localIso(), game: 'volcano-princess', type: 'month',
  state: { turn: 3, coin: 200, dauName: '露丝', faName: '伍柏', height: 70, fame: 3000, mood: 80, lover: 15, nature: { 体魄: 600 }, inAttri: { 力量: 52 } },
  lines: [{ who: '利', say: '早上好' }, { options: ['甲', '乙'] }, { chose: '乙' }],
}) + '\n');
await sleep(2000);

console.log(`收到 ${got.length} 条通知：`);
for (const n of got) console.log('  ', JSON.stringify(n.meta), '|', n.content.split('\n').slice(0, 3).join(' / '));
if (!got.some((n) => n.meta?.kind === 'chat' && n.content === '测试：你收到了吗')) fail('没收到面板发言');
if (!got.some((n) => n.meta?.kind === 'month' && n.meta.turn === '3')) fail('没收到换月');

const result = await client.callTool({ name: 'reply', arguments: { text: '收到啦' } });
console.log('reply：', result.content[0].text);
const history = await (await fetch(`${ORIGIN}/api/history`)).json();
if (!history.some((m) => m.from === 'agent' && m.text === '收到啦' && m.who === 'agent（终端）')) fail('回话没进聊天服务（或者没记着是 agent（终端）说的）');
await client.close();

// ---- 设了名字的 channel：说明里是那个人的名字，报到的是「agent 的名字（终端）」 ----
const named = new Client({ name: 'selftest-named', version: '0.0.1' });
await named.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(HERE, 'ludus-channel.mjs')],
  env: { ...env, LUDUS_USER_NAME: '小月', LUDUS_AGENT_NAME: '阿月' },
}));
const namedText = named.getInstructions() || '';
console.log('设了名字的说明（第一行）：', namedText.split('\n')[0]);
if (!namedText.includes('你在陪 小月 玩游戏') || /\buser\b/.test(namedText)) fail('设了 LUDUS_USER_NAME=小月，说明里没换过来');
if (namedText.includes('名字还是占位的')) fail('两个名字都设了，说明里还在提醒设名字');
if (!(await named.listTools()).tools.find((t) => t.name === 'reply')?.description.includes('小月')) fail('reply 的说明里没换成小月');
let label = false;
for (let i = 0; i < 20 && !label; i++) { label = (await presence()).includes('阿月（终端）'); if (!label) await sleep(200); }
if (!label) fail(`设了 LUDUS_AGENT_NAME=阿月，报到的名字不是「阿月（终端）」：${JSON.stringify(await presence())}`);
await named.close();

// ---- hello 名字 起过名字的会话：channel 报到用那个名字（session-names.json，vp_player 写的），不再冲回默认名 ----
fs.mkdirSync(path.join(data, 'selfplay'), { recursive: true });
fs.writeFileSync(path.join(data, 'selfplay', 'session-names.json'), JSON.stringify({ 'selftest-会话': '小星' }));
const presenceAll = async () => (await (await fetch(`${ORIGIN}/api/presence`)).json());
const sessioned = new Client({ name: 'selftest-session', version: '0.0.1' });
await sessioned.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(HERE, 'ludus-channel.mjs')], env: { ...env, CLAUDE_CODE_SESSION_ID: 'selftest-会话' },
}));
let mineP = null;
for (let i = 0; i < 20 && !mineP; i++) { mineP = (await presenceAll()).find((p) => p.id === 'claude:selftest-会话'); if (!mineP) await sleep(200); }
if (mineP?.label !== '小星') fail(`起过名字的会话报到的名字不是「小星」：${JSON.stringify(await presenceAll())}`);
await sessioned.callTool({ name: 'reply', arguments: { text: '我是小星' } });
const said = (await (await fetch(`${ORIGIN}/api/history`)).json()).find((m) => m.text === '我是小星');
if (said?.who !== '小星') fail(`起过名字的会话回的话没记在「小星」名下：${JSON.stringify(said)}`);
console.log('起过名字的会话：报到、回话都叫', mineP?.label, '/', said?.who);
await sessioned.close();

server.kill();
await sleep(300);
fs.rmSync(data, { recursive: true, force: true });
console.log(process.exitCode ? '自测没通过' : '自测通过');
