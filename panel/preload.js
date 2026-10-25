// 给聊天页几个口子：把焦点还给游戏；收成小按钮、展开、拖动小按钮；彻底关掉；调整个窗口的透明度；
// 面板被热键叫出来时聚焦输入框；收起展开时换页面的样子。
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('ludus', {
  returnToGame: () => ipcRenderer.send('return-to-game'),
  minimize: () => ipcRenderer.send('minimize'),
  closePanel: () => ipcRenderer.send('close-panel'),
  expand: () => ipcRenderer.send('expand'),
  moveBy: (dx, dy) => ipcRenderer.send('move-by', dx, dy),
  moveDone: () => ipcRenderer.send('move-done'),
  setOpacity: (value) => ipcRenderer.send('set-opacity', value),
  getState: () => ipcRenderer.invoke('get-state'),
  onMode: (fn) => ipcRenderer.on('mode', (_e, mode) => fn(mode)),
  onFocusInput: (fn) => ipcRenderer.on('focus-input', () => fn()),
});
