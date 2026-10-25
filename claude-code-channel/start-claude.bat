@echo off
rem Start a terminal Claude Code session that receives the Ludus Lunae channel (game chat panel + game events).
rem The chat service (chat-service) must be running. A full-screen warning appears: choose local development.
rem Needs Node.js 22.12 or newer on PATH. Run npm.cmd install in this folder once first.
rem
rem Optional environment variables:
rem   CLAUDE_EXE       the claude executable (default: claude, found on PATH)
rem   LUDUS_WORKDIR    the folder claude starts in (default: this repository). Point it at your agent's own
rem                    project folder, so the session gets that folder's settings and memory.
rem   LUDUS_USER_NAME  your name (default: user); LUDUS_AGENT_NAME the agent's name (default: agent)
rem
rem The MCP config can only hold absolute paths, so it is written to %TEMP% on every start.
setlocal
if not defined CLAUDE_EXE set "CLAUDE_EXE=claude"
if not defined LUDUS_WORKDIR set "LUDUS_WORKDIR=%~dp0.."
set "MCP_JSON=%TEMP%\ludus-channel-mcp.json"
node -e "const [out, ch] = process.argv.slice(1); require('fs').writeFileSync(out, JSON.stringify({ mcpServers: { ludus: { command: 'node', args: [require('path').resolve(ch).split(String.fromCharCode(92)).join('/')] } } }, null, 2))" "%MCP_JSON%" "%~dp0ludus-channel.mjs"
if errorlevel 1 (
  echo Could not write "%MCP_JSON%". Is Node.js on PATH?
  pause
  exit /b 1
)
cd /d "%LUDUS_WORKDIR%"
if errorlevel 1 (
  echo Could not open LUDUS_WORKDIR: "%LUDUS_WORKDIR%"
  pause
  exit /b 1
)
call "%CLAUDE_EXE%" --mcp-config "%MCP_JSON%" --dangerously-load-development-channels server:ludus
if errorlevel 1 pause
