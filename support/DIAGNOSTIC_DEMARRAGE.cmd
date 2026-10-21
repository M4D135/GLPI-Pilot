@echo off
setlocal DisableDelayedExpansion
title GLPI Pilot - diagnostic de demarrage
echo GLPI Pilot : verification du pack et lancement observe pendant 20 secondes.
echo Aucune elevation ni modification de la politique de securite.
echo.

set "GLPI_DIAG_LOGDIR=%LOCALAPPDATA%\GLPI-Pilot\Logs"
if not defined LOCALAPPDATA goto diagnostic_temp
if not exist "%GLPI_DIAG_LOGDIR%" mkdir "%GLPI_DIAG_LOGDIR%" 2>nul
set "GLPI_DIAG_ENGINE_LOG=%GLPI_DIAG_LOGDIR%\lanceur-demarrage-dernier.log"
copy /Y NUL "%GLPI_DIAG_ENGINE_LOG%" >nul 2>&1
if errorlevel 1 goto diagnostic_temp
>>"%GLPI_DIAG_ENGINE_LOG%" echo GLPI Pilot - appel de Windows PowerShell
goto diagnostic_run

:diagnostic_temp
if not defined TEMP goto diagnostic_no_log
set "GLPI_DIAG_LOGDIR=%TEMP%\GLPI-Pilot\Logs"
if not exist "%GLPI_DIAG_LOGDIR%" mkdir "%GLPI_DIAG_LOGDIR%" 2>nul
set "GLPI_DIAG_ENGINE_LOG=%GLPI_DIAG_LOGDIR%\lanceur-demarrage-dernier.log"
copy /Y NUL "%GLPI_DIAG_ENGINE_LOG%" >nul 2>&1
if errorlevel 1 goto diagnostic_no_log
>>"%GLPI_DIAG_ENGINE_LOG%" echo GLPI Pilot - appel de Windows PowerShell

:diagnostic_run
if not exist "%~dp0Diagnostic-Demarrage.ps1" goto diagnostic_missing
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -File "%~dp0Diagnostic-Demarrage.ps1" >>"%GLPI_DIAG_ENGINE_LOG%" 2>&1
set "GLPI_DIAG_ENGINE_EXIT=%ERRORLEVEL%"
type "%GLPI_DIAG_ENGINE_LOG%"
if "%GLPI_DIAG_ENGINE_EXIT%"=="0" exit /b 0
>>"%GLPI_DIAG_ENGINE_LOG%" echo.
>>"%GLPI_DIAG_ENGINE_LOG%" echo Code de retour PowerShell : %GLPI_DIAG_ENGINE_EXIT%
>>"%GLPI_DIAG_ENGINE_LOG%" echo Si la politique du poste bloque ce script, transmettre cette erreur au support informatique.
echo.
echo Le diagnostic n'a pas pu terminer. Aucun reglage de securite n'a ete modifie.
echo Rapport : "%GLPI_DIAG_ENGINE_LOG%"
start "" "%SystemRoot%\System32\notepad.exe" "%GLPI_DIAG_ENGINE_LOG%"
pause
exit /b 1

:diagnostic_missing
>>"%GLPI_DIAG_ENGINE_LOG%" echo Diagnostic-Demarrage.ps1 est absent. Extraire le pack complet avec son dossier wad.
type "%GLPI_DIAG_ENGINE_LOG%"
start "" "%SystemRoot%\System32\notepad.exe" "%GLPI_DIAG_ENGINE_LOG%"
pause
exit /b 1

:diagnostic_no_log
echo Impossible de creer un rapport dans LOCALAPPDATA ou TEMP.
echo Aucun lancement effectue. Transmettre ce message au support informatique.
pause
exit /b 1
