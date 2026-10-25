@echo off
rem Build the text-only BepInEx 5 plugin (0.3.7) for Volcano Princess and copy it into the game's BepInEx\plugins\LudusLunae folder.
rem Settings (set them in the shell, or in a build.local.bat next to this file, or edit the defaults below):
rem   GAME       game folder (contains VolcanoPrincess_Data; install BepInEx 5 there and start the game once before building)
rem   CSC        Roslyn csc.exe from Visual Studio 2022 / Build Tools. Not set: found with vswhere.
rem   DATA_DIR   the chat service's data folder, compiled in as the default of DataDir
rem              (BepInEx\config\ludus-lunae.volcano-princess.cfg can still change it). Default: ..\..\chat-service\data
rem   OUT        where the dll goes (default: this folder)
rem   NOINSTALL  set to 1 to only build, without copying into the game (by default the dll is copied into the game)
rem   BEPINEX_CORE  BepInEx's core folder (BepInEx.dll, 0Harmony.dll). Default: %GAME%\BepInEx\core.
rem              To build before BepInEx is in the game, point it at the BepInEx\core folder of a separately unzipped BepInEx 5.
setlocal
cd /d "%~dp0"
if exist build.local.bat call build.local.bat
if not defined GAME set "GAME=C:\Program Files (x86)\Steam\steamapps\common\VolcanoPrincess"
if not defined DATA_DIR for %%I in ("%~dp0..\..\chat-service\data") do set "DATA_DIR=%%~fI"
if not defined OUT set "OUT=%~dp0."
if not defined CSC call :findcsc
if not defined CSC goto :nocsc
if not exist "%CSC%" goto :nocsc
set "M=%GAME%\VolcanoPrincess_Data\Managed"
set "CORE=%GAME%\BepInEx\core"
if defined BEPINEX_CORE set "CORE=%BEPINEX_CORE%"
if not exist "%M%\Assembly-CSharp.dll" goto :nogame
if not exist "%CORE%\BepInEx.dll" goto :nobepinex
if not exist "%OUT%" mkdir "%OUT%"
> BuildInfo.g.cs echo static class BuildInfo { public const string DataDir = @"%DATA_DIR%"; }
"%CSC%" /nologo /target:library /langversion:latest /nostdlib /noconfig /out:"%OUT%\LudusLunae.VolcanoPrincess.dll" ^
  /r:"%M%\mscorlib.dll" /r:"%M%\netstandard.dll" /r:"%M%\System.dll" /r:"%M%\System.Core.dll" ^
  /r:"%M%\UnityEngine.dll" /r:"%M%\UnityEngine.CoreModule.dll" /r:"%M%\UnityEngine.UI.dll" /r:"%M%\UnityEngine.UIModule.dll" ^
  /r:"%M%\UnityEngine.TextRenderingModule.dll" /r:"%M%\UnityEngine.Physics2DModule.dll" /r:"%M%\UnityEngine.JSONSerializeModule.dll" ^
  /r:"%M%\UnityEngine.AudioModule.dll" /r:"%M%\UnityEngine.AnimationModule.dll" /r:"%M%\UnityEngine.InputLegacyModule.dll" /r:"%M%\UnityEngine.ScreenCaptureModule.dll" ^
  /r:"%M%\Assembly-CSharp.dll" /r:"%M%\Assembly-CSharp-firstpass.dll" /r:"%M%\DOTween.dll" /r:"%M%\spine-unity.dll" ^
  /r:"%CORE%\BepInEx.dll" /r:"%CORE%\0Harmony.dll" ^
  BuildInfo.g.cs Plugin.cs SelfPlay.cs MiniGames.cs TextPlay.cs || exit /b 1
echo built "%OUT%\LudusLunae.VolcanoPrincess.dll" (DataDir default: %DATA_DIR%)
if "%NOINSTALL%"=="1" exit /b 0
if not exist "%GAME%\BepInEx\core\BepInEx.dll" goto :notinstalled
if not exist "%GAME%\BepInEx\plugins\LudusLunae" mkdir "%GAME%\BepInEx\plugins\LudusLunae"
copy /y "%OUT%\LudusLunae.VolcanoPrincess.dll" "%GAME%\BepInEx\plugins\LudusLunae\" >nul || exit /b 1
echo copied to "%GAME%\BepInEx\plugins\LudusLunae". Restart the game to load it.
exit /b 0

:findcsc
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :eof
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "CSC=%%I\MSBuild\Current\Bin\Roslyn\csc.exe"
goto :eof

:nocsc
echo Cannot find the C# compiler. Install Visual Studio 2022 or "Build Tools for Visual Studio 2022" (workload: .NET desktop build tools),
echo or set CSC to the full path of Roslyn's csc.exe (for example ...\MSBuild\Current\Bin\Roslyn\csc.exe).
exit /b 1
:nogame
echo Cannot find "%M%\Assembly-CSharp.dll". Set GAME to the Volcano Princess folder (Steam: right-click the game, Manage, Browse local files).
exit /b 1
:nobepinex
echo Cannot find "%CORE%\BepInEx.dll". Install BepInEx 5 (x64) into the game folder first, start the game once, then build again.
echo Or set BEPINEX_CORE to the BepInEx\core folder of a separately unzipped BepInEx 5 (x64) to build now.
exit /b 1
:notinstalled
echo BepInEx 5 is not installed in the game yet, so the dll was not copied into it. Install BepInEx 5 into the game folder,
echo start the game once, then build again (or copy the dll into the game's BepInEx\plugins\LudusLunae folder yourself).
exit /b 0
