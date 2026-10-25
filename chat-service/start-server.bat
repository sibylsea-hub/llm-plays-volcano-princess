@echo off
rem Double-click to start the chat service (chat page + API, port 8740). Close this window to stop it.
rem Names: set LUDUS_USER_NAME (your name, default user) and LUDUS_AGENT_NAME (default agent) before starting.
rem --hub also starts the Ludus hub (mcp-hub\hub.mjs, port 8741) in the background if it is not running yet;
rem the hub keeps running after this window is closed (log: mcp-hub\data\hub.log).
cd /d "%~dp0"
node server.mjs --hub
pause
