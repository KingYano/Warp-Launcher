@echo off
rem ------------------------------------------------------------------
rem  Recompile Warp.exe a partir de Launcher.cs et Updater.cs.
rem  Utilise le compilateur C# fourni avec Windows : rien a installer.
rem  Ferme Warp avant de lancer ce script.
rem ------------------------------------------------------------------
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Compilateur introuvable : %CSC%
  pause
  exit /b 1
)

if not exist app.ico (
  "%CSC%" /nologo /out:mkicon.exe mkicon.cs || goto fail
  mkicon.exe app.ico || goto fail
  del mkicon.exe
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:..\Warp.exe ^
  /win32icon:app.ico /win32manifest:app.manifest /resource:app.ico,app.ico ^
  /r:..\Microsoft.Web.WebView2.Core.dll /r:..\Microsoft.Web.WebView2.WinForms.dll ^
  /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Launcher.cs Updater.cs || goto fail

echo Warp.exe est a jour.
exit /b 0

:fail
echo.
echo La compilation a echoue. Si Warp est ouvert, ferme-le puis recommence.
pause
exit /b 1
