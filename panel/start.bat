@echo off
rem Double-click to start the chat panel (Alt+` toggles focus). Quit from the tray icon.
rem The first run installs Electron by itself (needs Node.js 22.12 or newer). The chat service must be running.
cd /d "%~dp0"
rem First run installs Electron. Some npm versions skip Electron's download script, so fetch the binary if it is still missing.
if not exist "%~dp0node_modules\electron\package.json" call npm.cmd install || goto :fail
if not exist "%~dp0node_modules\electron\dist\electron.exe" node "%~dp0node_modules\electron\install.js" || goto :fail
start "" "%~dp0node_modules\electron\dist\electron.exe" "%~dp0."
exit /b 0
:fail
echo Could not install Electron. It needs Node.js 22.12 or newer and a network connection; try npm.cmd install in this folder.
pause
exit /b 1
