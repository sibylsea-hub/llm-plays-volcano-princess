# 聊天面板（Electron）

把聊天服务的聊天页（`http://127.0.0.1:8740/?panel=1`）装进一个无边框、置顶的小窗，浮在游戏画面上（游戏开着全屏也行，不用切回桌面）：对话和输入在一处，热键切焦点，打完字焦点回到游戏。Electron 44.5.1。

## 装、起

```bat
cd panel
start.bat
```

- 第一次开时 `start.bat` 自己 `npm.cmd install`；有的 npm 版本（或者设了不跑安装脚本）不会自己下 Electron 本体，它也会补跑 `node node_modules/electron/install.js`。之后双击 `start.bat` 就行。装 Electron 要 Node.js 22.12 或更新（和别的组件一样，见 `docs/安装.md`）。
- 聊天服务要开着（`chat-service\start-server.bat`）。
- 退出：托盘里的月牙图标右键 →「退出」。改了 `main.js`、`preload.js` 要重开面板；聊天页（`index.html`）改了会自己刷新。

## 用

- **什么时候出来**：游戏开着（读 Steam 的 `RunningAppID`，每 2 秒一次；非 Steam 游戏不算）、有 agent 挂着（聊天服务的「谁挂着」）、这一次没被 × 关掉。游戏关了、最后一个 agent 走了，面板藏起来，程序留在托盘里。× 只管这一次：游戏下次启动、或者有新的 agent 挂上来，就又出来。
- **手动打开**：点托盘的月牙，或者按 Alt+\`。
- **热键 Alt+\`**：面板没焦点时拿过焦点、聚焦输入框；有焦点时把焦点还给游戏。页面里按 Enter 发送后、或者按 Esc，也会还给游戏。
- **收成小按钮**：Ctrl+Alt+\` 或标题栏的「–」把面板折成一个 56×56 的月牙按钮；点一下、或者再按热键展开。agent 说话时按钮上亮一个红点。小按钮可以拖，放哪儿记哪儿。
- **透明**：标题栏「透明」拉一根滑条，20% 到 100%，给面板只能压在游戏上的时候用。
- **位置**：默认在主屏左边，宽 416。标题栏可以拖、边缘可以拉，位置、大小、收起、透明度记在 `chat-service\data\panel-bounds.json`、`panel-state.json`。托盘右键「放回默认位置」。
- 标题栏下面一行写现在谁挂着，在玩的那个排最前面、加粗。顶上三个开关（agent 自己玩、边玩边聊、跳过小游戏）见 `docs/安装.md` 第七节。

## 可以改的

| 环境变量 | 默认 | 是什么 |
|---|---|---|
| `LUDUS_PORT` | 8740 | 聊天服务的端口 |
| `LUDUS_DATA` | `..\chat-service\data` | 读写 `panel-bounds.json`、`panel-state.json`、`token` 的文件夹 |
| `LUDUS_HOTKEY` | `` Alt+` `` | 说话的热键（Electron 的写法，比如 `Ctrl+Shift+Space`） |
| `LUDUS_HIDE_HOTKEY` | `` Ctrl+Alt+` `` | 收起 / 展开的热键 |
| `LUDUS_CACHE` | Electron 默认的用户数据位置 | 设了就把用户数据（和单实例锁）放进 `<这里>\panel-userdata` |

热键被别的程序占了不会弹窗，托盘提示里写一句；用标题栏的按钮一样能收起、展开。测试时把这几个都换掉，就能和正在用的面板同时开、互不干扰。

全屏游戏盖不住别的窗口时（老的独占全屏），改用 Steam 覆盖层里的网页，见 `chat-service/README.md`。
