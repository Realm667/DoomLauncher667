@echo off
setlocal
set "ROOT=%~dp0"
if not exist "%ROOT%Data\UserState" mkdir "%ROOT%Data\UserState"
set "DOOMLAUNCHER_DATABASE=%ROOT%DoomLauncher.sqlite"
set "DOOMLAUNCHER_USER_STATE=%ROOT%Data\UserState\DoomLauncher.WinUI.state.json"
set "DOOMLAUNCHER_DIAGNOSTIC_LOG=%ROOT%Data\UserState\DoomLauncher.WinUI.crash.log"
start "" /d "%ROOT%WinUI" "%ROOT%WinUI\DoomLauncher.WinUI.exe"
endlocal
exit /b 0
