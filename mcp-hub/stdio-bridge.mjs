// 给只认 stdio 的客户端（Claude Desktop 的本机配置）用的小桥：stdio ↔ hub 的 HTTP（/mcp）。
// 调用都转给 hub，这里不懂游戏。stdout 是 MCP 的通道，日志只写 stderr。
// 工具清单和说明：hub 开着就从 hub 拿，没开就用 tools.mjs 里那份（和 hub 共用）。桌面 app 只在启动时列一次工具，
// 只会现场找 hub 要的话，hub 后开它就一个工具都拿不到，得把桌面 app 整个重开。
// 所以先给本地那份；调用时 hub 还没开，回一句「请人起 hub」，起好以后再调就通，不用重开客户端。
//
//   node stdio-bridge.mjs [http://127.0.0.1:8741/mcp]
//   局域网模式的 hub：环境变量 LUDUS_HUB_TOKEN 设成 mcp-hub/data/token 里的字
//   LUDUS_USER_NAME：人的名字（默认 user），hub 没开时小桥先给的说明、回的那句话里用
//   名字：连 hub 时报的客户端名，hub 拿它当这个会话的名字（面板上、玩家文件夹叫「hub：名字」，play hello 名字 照旧能改）。
//   设了 LUDUS_AGENT_NAME 就用它，没设就用客户端自己报的名字（Claude 桌面 app 报的是 claude-ai）。
//   所以等客户端 initialize 了才连 hub：一启动就连的话，还不知道客户端叫什么
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema, InitializeRequestSchema, LATEST_PROTOCOL_VERSION, ListToolsRequestSchema, SUPPORTED_PROTOCOL_VERSIONS,
} from '@modelcontextprotocol/sdk/types.js';
import { INSTRUCTIONS, offlineTools } from './tools.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HUB = new URL(process.argv[2] || process.env.LUDUS_HUB_URL || 'http://127.0.0.1:8741/mcp');
const TOKEN = process.env.LUDUS_HUB_TOKEN;
const USER_NAME = process.env.LUDUS_USER_NAME?.trim() || 'user';
const AGENT_NAME = process.env.LUDUS_AGENT_NAME?.trim() || '';
const log = (...args) => console.error('[ludus-bridge]', ...args);

let name = AGENT_NAME || 'stdio-bridge';  // 报给 hub 的客户端名：客户端 initialize 时定下来，重连也用它
let client = null;
let connecting = null;

async function hub() {
  if (client) return client;
  connecting ??= (async () => {
    const c = new Client({ name, version: '0.3.0' });
    const transport = new StreamableHTTPClientTransport(HUB, {
      requestInit: TOKEN ? { headers: { Authorization: `Bearer ${TOKEN}` } } : undefined,
    });
    await c.connect(transport);
    c.onclose = () => { if (client === c) client = null; };
    client = c;
    return c;
  })().finally(() => { connecting = null; });
  return connecting;
}

/** hub 重启过，会话就没了：丢掉旧的连接再来一次 */
async function retry(fn) {
  try {
    return await fn(await hub());
  } catch (err) {
    log('hub 那边出错，重连再试：', err.message);
    try { await client?.close(); } catch {}
    client = null;
    return fn(await hub());
  }
}

const within = (p, ms) => Promise.race([p, new Promise((_, no) => setTimeout(() => no(new Error(`${ms / 1000} 秒没连上`)), ms).unref())]);

/** 调用时连不上 hub：告诉 agent 请人把 hub 起起来，起好再调就行 */
function hubDown(err) {
  const local = ['127.0.0.1', 'localhost', '[::1]'].includes(HUB.hostname);
  const startServer = path.resolve(HERE, '..', 'chat-service', 'start-server.bat');
  const startHub = path.join(HERE, 'start-hub.bat');
  let how;
  if (!local) how = `hub 在另一台电脑上（${HUB.host}）：请 ${USER_NAME} 看看那边 hub 开着没有、这台电脑连不连得上。`;
  else if (fs.existsSync(startServer)) how = `请 ${USER_NAME} 起一下：双击 ${startServer}（聊天服务会顺手在后台起 hub）；只要 hub 的话，双击 ${startHub}。`;
  else how = `请 ${USER_NAME} 起一下：双击仓库里的 chat-service\\start-server.bat（会顺手在后台起 hub），或者 mcp-hub\\start-hub.bat。`;
  const text = `Ludus hub 没开，游戏的工具现在用不了。${how}起好以后再调一次就行，不用重开这个客户端。（连 ${HUB} 时：${err.message}）`;
  return { content: [{ type: 'text', text }], isError: true };
}

const names = (tools) => tools.map((t) => t.name).sort().join(',');
let gaveOffline = null;  // hub 没开时交出去的那份工具清单（名字）；hub 接上以后对一下，不一样就通知客户端重列

const INFO = { name: 'ludus-hub', title: 'Ludus hub', version: '0.3.0' };
const server = new Server(INFO, { capabilities: { tools: { listChanged: true } } });
// initialize 自己答（和 SDK 的 Server 默认答的一样）：先记下客户端报的名字，用这个名字连 hub，带上说明再回
server.setRequestHandler(InitializeRequestSchema, async (req) => {
  const { protocolVersion, clientInfo } = req.params;
  name = AGENT_NAME || String(clientInfo?.name ?? '').trim().slice(0, 40) || name;
  let instructions = INSTRUCTIONS;
  try {
    instructions = (await within(hub(), 5_000)).getInstructions() || INSTRUCTIONS;
    log(`已接上 ${HUB}：客户端报的名字 ${clientInfo?.name || '（没报）'}，在 hub 那边叫 ${name}`);
  } catch (err) {
    log(`连不上 ${HUB}（${err.message}）：先用本地的说明和工具清单，调用时再连`);
  }
  return {
    protocolVersion: SUPPORTED_PROTOCOL_VERSIONS.includes(protocolVersion) ? protocolVersion : LATEST_PROTOCOL_VERSION,
    capabilities: server.getCapabilities(),
    serverInfo: INFO,
    instructions,
  };
});
server.setRequestHandler(ListToolsRequestSchema, async () => {
  try {
    const r = await within(retry((c) => c.listTools()), 5_000);
    gaveOffline = null;
    return r;
  } catch (err) {
    log(`连不上 ${HUB}（${err.message}）：给本地的工具清单`);
    const tools = offlineTools();
    gaveOffline = names(tools);
    return { tools };
  }
});
server.setRequestHandler(CallToolRequestSchema, async (req) => {
  let r;
  try {
    r = await retry((c) => c.callTool(req.params, undefined, { timeout: 120_000 }));
  } catch (err) {
    if (/timed? ?out/i.test(err.message)) {
      return { content: [{ type: 'text', text: `hub 那边这一下太久没回（${err.message}），再调一次试试。` }], isError: true };
    }
    return hubDown(err);
  }
  if (gaveOffline) {
    const before = gaveOffline;
    gaveOffline = null;
    hub().then((c) => c.listTools()).then(({ tools }) => {
      if (names(tools) !== before) server.notification({ method: 'notifications/tools/list_changed' }).catch(() => {});
    }).catch(() => {});
  }
  return r;
});
await server.connect(new StdioServerTransport());
log(`等客户端连上来，再接 ${HUB}`);
