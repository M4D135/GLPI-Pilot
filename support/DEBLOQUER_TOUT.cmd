@echo off
setlocal
set "GLPIPILOT_UNBLOCK_ROOT=%~dp0..\.."
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $root=(Get-Item -LiteralPath $env:GLPIPILOT_UNBLOCK_ROOT).FullName; Get-ChildItem -LiteralPath $root -Recurse -File | Unblock-File; Write-Host ('Fichiers debloques dans : ' + $root)"
if errorlevel 1 (
  echo Le deblocage a rencontre une erreur.
  pause
  exit /b 1
)
pause
endlocal
