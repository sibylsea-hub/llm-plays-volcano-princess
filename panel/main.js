// 对话和输入合一的置顶面板：把本地聊天页装进一个无边框、置顶的窗口。
// 热键（默认 Alt+`）在面板和游戏之间切换焦点；页面里按 Enter 发送或 Esc 后，焦点回到游戏。
// 收起热键（默认 Ctrl+Alt+`）或标题栏的 – 把面板折成一个小按钮，点按钮或再按一次热键展开。
// 什么时候出来：游戏开着（Steam 的 RunningAppID）、而且有会话挂着（聊天服务的 /api/presence）。
// 标题栏的 × 只关这一次：游戏下次启动、或者有新的会话挂上来，就又出来。托盘、Alt+` 随时手动打开。
// 整个窗口的透明度在页面的「透明」里调。收成小按钮的状态、小按钮的位置、透明度记在 data/panel-state.json。
// 展开时的位置和大小拖动后自动记住（data/panel-bounds.json）。程序一直留在托盘里。
const { app, BrowserWindow, Menu, Tray, dialog, globalShortcut, ipcMain, nativeImage, screen } = require('electron');
const { execFile } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const PORT = process.env.LUDUS_PORT || 8740;
const ORIGIN = `http://127.0.0.1:${PORT}`;
const HOTKEY = process.env.LUDUS_HOTKEY || 'Alt+`';
const HIDE_HOTKEY = process.env.LUDUS_HIDE_HOTKEY || 'Ctrl+Alt+`';
const DATA = process.env.LUDUS_DATA || path.join(__dirname, '..', 'chat-service', 'data');  // 测试时和聊天服务指到同一个临时文件夹
const BOUNDS_FILE = path.join(DATA, 'panel-bounds.json');
const STATE_FILE = path.join(DATA, 'panel-state.json');
const TOKEN_FILE = path.join(DATA, 'token');
const MINI_SIZE = 56;          // 收起后小按钮的边长
const MIN_OPACITY = 0.2;       // 再淡就找不到面板了

// Electron 的用户数据（缓存、单实例锁）默认在系统给的 userData 文件夹；设了 LUDUS_CACHE 就放到它下面
if (process.env.LUDUS_CACHE) app.setPath('userData', path.join(process.env.LUDUS_CACHE, 'panel-userdata'));

if (!app.requestSingleInstanceLock()) {
  app.quit();
  return;
}

let win;
let tray;

function onScreen(p) {
  return screen.getAllDisplays().some(({ bounds: d }) => p.x >= d.x && p.y >= d.y && p.x < d.x + d.width && p.y < d.y + d.height);
}

function defaultBounds() {
  const { workArea } = screen.getPrimaryDisplay();
  return { x: workArea.x + 12, y: workArea.y + 60, width: 416, height: Math.max(480, workArea.height - 120) };
}

function loadBounds() {
  try {
    const b = JSON.parse(fs.readFileSync(BOUNDS_FILE, 'utf8'));
    if (onScreen(b)) return b;
  } catch {}
  return defaultBounds();
}

let fullBounds;  // 展开时的位置和大小
let saveTimer;
function saveBoundsSoon() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    if (state.collapsed) return;  // 小按钮的位置另记，不覆盖展开时的
    fullBounds = win.getBounds();
    fs.mkdirSync(path.dirname(BOUNDS_FILE), { recursive: true });
    fs.writeFileSync(BOUNDS_FILE, JSON.stringify(fullBounds));
  }, 500);
}

// collapsed：折成小按钮；mini：小按钮的位置；opacity：整个窗口的透明度。× 关掉只管这一次，不记。
const state = { collapsed: false, mini: null, opacity: 1 };
try { Object.assign(state, JSON.parse(fs.readFileSync(STATE_FILE, 'utf8'))); } catch {}
delete state.closed;  // × 关掉不该记在文件里；旧文件里有就丢掉
state.collapsed = state.collapsed === true;
state.opacity = Math.min(1, Math.max(MIN_OPACITY, Number(state.opacity) || 1));

function saveState() {
  fs.mkdirSync(path.dirname(STATE_FILE), { recursive: true });
  fs.writeFileSync(STATE_FILE, JSON.stringify(state));
}

// 小按钮现在的位置自己记：窗口缩小、去掉拖边以后，getBounds() 报的位置会差十几像素
let miniPos;
function miniBounds() {
  miniPos = state.mini && onScreen(state.mini) ? { ...state.mini } : { x: fullBounds.x, y: fullBounds.y };
  return { ...miniPos, width: MINI_SIZE, height: MINI_SIZE };
}

// 小按钮的大小锁住，免得拖到边上把它拉大。用最小、最大尺寸锁，不用 setResizable：在 Windows 的无边框窗口上
// 来回切 setResizable，窗口大小会算错几十像素（展开回来变宽，记下来的变窄）。
function lockMiniSize(lock) {
  win.setMinimumSize(lock ? MINI_SIZE : 0, lock ? MINI_SIZE : 0);
  win.setMaximumSize(lock ? MINI_SIZE : 0, lock ? MINI_SIZE : 0);
}

// 折成小按钮：页面先换成按钮的样子，再把窗口缩小。焦点原来在面板上的话，还给游戏。
function collapse() {
  if (state.collapsed) return;
  fullBounds = win.getBounds();
  state.collapsed = true;
  saveState();
  win.webContents.send('mode', 'mini');
  lockMiniSize(true);
  win.setBounds(miniBounds());
  returnToGame();
  updateTray();
}

// ---- 什么时候出来 ----
// 自动：游戏开着、而且有会话挂着就出来，不然藏起来。
// 手动（manual）：托盘、Alt+`、收起热键打开是 'open'，× 和托盘关掉是 'closed'。手动只管这一次：
//   关掉的，游戏下次启动、或者有新的会话挂上来，就又照自动的来；
//   打开的，游戏关了、或者最后一个会话走了，也照自动的来（就藏起来了）。
let manual = null;
let runningAppId = 0;
let present = [];  // 聊天服务报的挂着的会话 [{ id, label, ... }]

function shouldShow() {
  if (manual) return manual === 'open';
  return runningAppId !== 0 && present.length > 0;
}

function applyVisibility() {
  if (shouldShow()) {
    if (!win.isVisible()) win.showInactive();
  } else if (win.isVisible()) {
    win.hide();  // 焦点在面板上的话，Windows 交给下面的游戏
  }
  updateTray();
}

function openManually() {
  manual = 'open';
  applyVisibility();
}

function closePanel() {
  manual = 'closed';
  applyVisibility();
}

const toggleClosed = () => (win.isVisible() ? closePanel() : openManually());

// 展开：点小按钮展开时顺便把输入框点亮；热键展开不抢游戏的焦点
function expand({ focus = false } = {}) {
  if (state.collapsed) {
    state.collapsed = false;
    saveState();
    lockMiniSize(false);
    win.setBounds(fullBounds);
    win.webContents.send('mode', 'full');
    updateTray();
  }
  if (focus) {
    if (!win.isVisible()) openManually();
    win.focus();
    win.webContents.send('focus-input');
  }
}

// 收起热键：藏着的时候按，先打开（原来是小按钮就还是小按钮）
function toggleCollapsed() {
  if (!win.isVisible()) openManually();
  else if (state.collapsed) expand();
  else collapse();
}

function focusPanel() {
  expand({ focus: true });
}

// 先藏再不激活地显示：窗口一藏，Windows 就把焦点交给下面那个窗口，也就是刚才的游戏。
function returnToGame() {
  if (!win.isFocused()) return;
  win.hide();
  win.showInactive();
}

// 拖小按钮：页面送来从按下那一刻算起鼠标移了多少，松手时送 done
let moveFrom = null;
function moveBy(_e, dx, dy) {
  if (!state.collapsed || !Number.isFinite(dx) || !Number.isFinite(dy)) return;
  const from = moveFrom ??= { ...miniPos };
  miniPos = { x: from.x + Math.round(dx), y: from.y + Math.round(dy) };
  win.setBounds({ ...miniPos, width: MINI_SIZE, height: MINI_SIZE });
}

function moveDone() {
  if (!moveFrom) return;
  moveFrom = null;
  state.mini = { ...miniPos };
  saveState();
  returnToGame();  // 按下去那一下把焦点拿过来了，拖完还给游戏
}

let opacityTimer;
function setOpacity(_e, value) {
  const v = Number(value);
  if (!Number.isFinite(v)) return;
  state.opacity = Math.min(1, Math.max(MIN_OPACITY, v));
  win.setOpacity(state.opacity);
  clearTimeout(opacityTimer);
  opacityTimer = setTimeout(saveState, 300);
}

// Steam 正在运行的游戏：HKCU\Software\Valve\Steam 的 RunningAppID，没在玩时是 0
function readRunningAppId() {
  return new Promise((resolve) => {
    execFile('reg', ['query', 'HKCU\\Software\\Valve\\Steam', '/v', 'RunningAppID'], { windowsHide: true }, (err, out) => {
      const m = !err && /RunningAppID\s+REG_DWORD\s+0x([0-9a-f]+)/i.exec(out);
      resolve(m ? parseInt(m[1], 16) : 0);
    });
  });
}

// 新的一局：告诉聊天服务，页面清空，只显示这一局的消息
async function newSession(appId) {
  try {
    const token = fs.readFileSync(TOKEN_FILE, 'utf8').trim();
    await fetch(`${ORIGIN}/api/session`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ appId }),
    });
  } catch (err) {
    console.error('开新一局失败：', err.message);
  }
}

// 谁挂着：聊天服务记着。连不上时返回 null，照上次的算（聊天服务重启那几秒面板不跟着闪）
async function readPresence() {
  try {
    const res = await fetch(`${ORIGIN}/api/presence`, { signal: AbortSignal.timeout(1500) });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

// 每 2 秒看一眼游戏和会话。手动开关只在这几件事发生的那一刻作废（见上面「什么时候出来」）。
async function watch() {
  const [appId, list] = await Promise.all([readRunningAppId(), readPresence()]);
  if (appId !== runningAppId) {
    if (appId) {
      await newSession(appId);
      if (manual === 'closed') manual = null;  // 游戏启动：× 不算数了
    } else {
      manual = null;                           // 游戏关了：跟着藏起来
    }
    runningAppId = appId;
  }
  if (Array.isArray(list)) {
    const before = new Set(present.map((p) => p.id));
    if (manual === 'closed' && list.some((p) => !before.has(p.id))) manual = null;  // 有新的会话挂上来
    if (manual === 'open' && present.length && !list.length) manual = null;          // 最后一个会话走了
    present = list;
  }
  applyVisibility();
  setTimeout(watch, 2000);
}

let hideHotkeyOk = true;
function updateTray() {
  if (!tray) return;
  const parts = ['Ludus Lunae', present.length ? present.map((p) => (p.active ? `▶${p.label}` : p.label)).join('、') : '没有会话挂着'];
  if (!win.isVisible()) parts.push(manual === 'closed' ? '关着，点这里打开' : '藏着，点这里打开');
  else if (state.collapsed) parts.push('收成小按钮');
  if (runningAppId) parts.push(`正在玩 ${runningAppId}`);
  parts.push(`${HOTKEY} 说话`, hideHotkeyOk ? `${HIDE_HOTKEY} 收起` : `收起热键 ${HIDE_HOTKEY} 被占用`);
  tray.setToolTip(parts.join(' · ').slice(0, 127));  // Windows 的托盘提示最多 127 个字
}

// 托盘图标：一弯金色月牙（16×16，BGRA）
function moonIcon() {
  const size = 16;
  const buf = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const inMoon = (x - 7.5) ** 2 + (y - 7.5) ** 2 <= 49;
      const inBite = (x - 10.5) ** 2 + (y - 5) ** 2 <= 30;
      if (inMoon && !inBite) buf.set([0x7a, 0xc2, 0xe6, 0xff], (y * size + x) * 4);
    }
  }
  return nativeImage.createFromBitmap(buf, { width: size, height: size });
}

function createWindow() {
  fullBounds = loadBounds();
  win = new BrowserWindow({
    ...(state.collapsed ? miniBounds() : fullBounds),
    title: 'Ludus Lunae',
    frame: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    show: false,
    opacity: state.opacity,
    backgroundColor: '#15171d',
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, sandbox: true },
  });
  // 'screen-saver' 层级比普通置顶更高，压得住全屏游戏
  win.setAlwaysOnTop(true, 'screen-saver');
  if (state.collapsed) lockMiniSize(true);

  // 只许待在本地聊天页上
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('will-navigate', (e, url) => { if (!url.startsWith(ORIGIN)) e.preventDefault(); });

  // 聊天服务还没起来时，每两秒重试
  win.webContents.on('did-fail-load', (_e, _code, _desc, url, isMainFrame) => {
    if (isMainFrame) setTimeout(() => win.loadURL(`${ORIGIN}/?panel=1`), 2000);
  });

  win.on('moved', saveBoundsSoon);
  win.on('resized', saveBoundsSoon);
  win.once('ready-to-show', applyVisibility);
  win.loadURL(`${ORIGIN}/?panel=1`);
}

app.whenReady().then(() => {
  createWindow();
  ipcMain.on('return-to-game', returnToGame);
  ipcMain.on('minimize', collapse);
  ipcMain.on('close-panel', closePanel);
  ipcMain.on('expand', () => expand({ focus: true }));
  ipcMain.on('move-by', moveBy);
  ipcMain.on('move-done', moveDone);
  ipcMain.on('set-opacity', setOpacity);
  ipcMain.handle('get-state', () => ({ collapsed: state.collapsed, opacity: state.opacity, minOpacity: MIN_OPACITY }));

  if (!globalShortcut.register(HOTKEY, () => (win.isFocused() && !state.collapsed ? returnToGame() : focusPanel()))) {
    dialog.showErrorBox('Ludus Lunae', `热键 ${HOTKEY} 被别的程序占用了。先退出那个程序，或者用环境变量 LUDUS_HOTKEY 换一个热键。`);
  }
  // 收起热键被占用时不弹窗挡着游戏，托盘提示里写一句；用 – 和小按钮一样能收起、展开
  hideHotkeyOk = globalShortcut.register(HIDE_HOTKEY, toggleCollapsed);

  tray = new Tray(moonIcon());
  updateTray();
  tray.on('click', focusPanel);
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: '收成小按钮 / 展开', click: toggleCollapsed },
    { label: '关掉 / 打开面板', click: toggleClosed },
    { label: '放回默认位置', click: () => { if (!win.isVisible()) openManually(); expand(); win.setBounds(defaultBounds()); } },
    { type: 'separator' },
    { label: '退出', click: () => app.quit() },
  ]));
  watch();
});

app.on('second-instance', focusPanel);
app.on('will-quit', () => globalShortcut.unregisterAll());
