@echo off
setlocal
cd /d "%~dp0"
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  exit /b 1
)
"%SystemRoot%\System32\tasklist.exe" /fi "imagename eq FlashBar.exe" | "%SystemRoot%\System32\findstr.exe" /i "FlashBar.exe" >nul && (
  echo FlashBar.exe is running, please exit it from the tray first.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:FlashBar.exe src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: FlashBar.exe
endlocal
