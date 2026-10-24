@echo off
if not exist "%~dp0..\..\GLPI-Pilot.exe" (
  echo Placez ce dossier wad a cote de GLPI-Pilot.exe avant de lancer l'installation.
  pause
  exit /b 1
)
start "" "%~dp0..\..\GLPI-Pilot.exe"
