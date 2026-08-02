@echo off
setlocal
set "ROOT=%~dp0"

if /I "%~1"=="/Y" goto reset
echo.
echo ACHTUNG: Alle Daten, Einstellungen und importierten Dateien in
echo "%ROOT%"
echo werden entfernt.
echo.
choice /C JN /N /M "Instanz in den Auslieferungszustand zuruecksetzen? [J/N] "
if errorlevel 2 exit /b 1

:reset
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Tools\Reset-Instance.ps1" -Root "%ROOT%."
if errorlevel 1 (
    echo.
    echo Der Reset ist fehlgeschlagen.
    pause
    exit /b 1
)

echo.
echo Die Testinstanz befindet sich wieder im Auslieferungszustand.
pause
endlocal
