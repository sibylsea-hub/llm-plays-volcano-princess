@echo off
rem Double-click to start the Ludus hub (MCP server that lets clients play the game). Close this window to stop it.
rem Extra arguments are passed through, e.g.  start-hub.bat --lan   (LAN mode, every request needs data\token)
rem Names: LUDUS_USER_NAME (your name, default user) and LUDUS_AGENT_NAME (default agent) are read from the environment.
rem Needs Node.js 22.12 or newer on PATH.
cd /d "%~dp0"
if not exist node_modules call npm.cmd install --no-audit --no-fund
node hub.mjs %*
pause
