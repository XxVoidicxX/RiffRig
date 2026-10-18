@echo off
rem Installs RiffRig for the current user: copies dist\RiffRig.exe to %LOCALAPPDATA%\Programs\RiffRig,
rem adds a Start menu shortcut (so Windows search finds "RiffRig"), then opens the app.
setlocal
set "SRC=%~dp0dist\RiffRig.exe"
set "DEST=%LOCALAPPDATA%\Programs\RiffRig"
set "REPORT=%~dp0install_report.txt"

if not exist "%SRC%" (
    echo dist\RiffRig.exe not found. Build it first, see README.md.
    echo FAIL no dist exe > "%REPORT%"
    pause
    exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"
copy /y "%SRC%" "%DEST%\RiffRig.exe" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'RiffRig.lnk';" ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk);" ^
  "$s.TargetPath = '%DEST%\RiffRig.exe'; $s.WorkingDirectory = '%DEST%';" ^
  "$s.Description = 'Guitar monitor, tuner and amp'; $s.Save();" ^
  "$ok = (Test-Path $lnk) -and (Test-Path '%DEST%\RiffRig.exe');" ^
  "Set-Content -Path '%REPORT%' -Value ((@('OK','FAIL')[[int](-not $ok)]) + ' ' + $lnk)"

start "" "%DEST%\RiffRig.exe"
