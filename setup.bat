@echo off
setlocal enabledelayedexpansion

set "SERVICE_NAME=AppZone"
set "DISPLAY_NAME=AppZone"
set "INSTALL_DIR=C:\Program Files\AppZone"
set "EXE_PATH=%INSTALL_DIR%\AppZone.exe"
set "TMP_PATH=%INSTALL_DIR%\AppZone.exe.tmp"
set "BACKUP_PATH=%INSTALL_DIR%\AppZone.exe.bak"
set "DOWNLOAD_URL=https://github.com/JustinTagarda/AppZone/releases/latest/download/AppZone.exe"

net session >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "This installer must be run as Administrator."
  exit /b 1
)

echo Processing...
if not exist "%INSTALL_DIR%" (
  mkdir "%INSTALL_DIR%" >nul 2>&1
  if !ERRORLEVEL! NEQ 0 (
    call :fail "Failed to create install folder."
    exit /b 1
  )
)
if not exist "%INSTALL_DIR%" (
  call :fail "Install folder does not exist."
  exit /b 1
)

powershell -NoProfile -Command ^
  "$ProgressPreference='SilentlyContinue'; try { Invoke-WebRequest -Uri '%DOWNLOAD_URL%' -OutFile '%TMP_PATH%' -UseBasicParsing; exit 0 } catch { exit 1 }" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "Failed to download Control."
  exit /b 1
)
if not exist "%TMP_PATH%" (
  call :fail "Downloaded file not found."
  exit /b 1
)

sc query "%SERVICE_NAME%" >nul 2>&1
if !ERRORLEVEL! EQU 0 (
  sc stop "%SERVICE_NAME%" >nul 2>&1
  sc delete "%SERVICE_NAME%" >nul 2>&1
  timeout /t 2 >nul
)

set "REMOVED_OK=0"
for /L %%i in (1,1,4) do (
  timeout /t 5 >nul
  sc query "%SERVICE_NAME%" >nul 2>&1
  if !ERRORLEVEL! NEQ 0 (
    set "REMOVED_OK=1"
    goto :after_delete_wait
  )
)
:after_delete_wait
if "!REMOVED_OK!" NEQ "1" (
  del /f /q "%TMP_PATH%" >nul 2>&1
  call :fail "Existing task could not be removed."
  exit /b 1
)

:install
set "HAS_BACKUP=0"
set "TASK_CREATED=0"
if exist "%EXE_PATH%" (
  copy /y "%EXE_PATH%" "%BACKUP_PATH%" >nul 2>&1
  if !ERRORLEVEL! NEQ 0 (
    del /f /q "%TMP_PATH%" >nul 2>&1
    call :fail "Failed to backup existing Control."
    exit /b 1
  )
  set "HAS_BACKUP=1"
)
copy /y "%TMP_PATH%" "%EXE_PATH%" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :restore_and_fail "Failed to replace the Control file."
  exit /b 1
)
del /f /q "%TMP_PATH%" >nul 2>&1

sc create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto DisplayName= "%DISPLAY_NAME%" obj= "LocalSystem" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :restore_and_fail "Failed to create the task."
  exit /b 1
)
set "TASK_CREATED=1"

sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/5000/restart/5000 >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :restore_and_fail "Failed to configure service recovery actions."
  exit /b 1
)

sc failureflag "%SERVICE_NAME%" 1 >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :restore_and_fail "Failed to enable service recovery actions."
  exit /b 1
)

sc start "%SERVICE_NAME%" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :restore_and_fail "Task created but failed to start."
  exit /b 1
)

set "RUNNING_OK=0"
for /L %%i in (1,1,4) do (
  timeout /t 5 >nul
  sc query "%SERVICE_NAME%" | findstr /R /C:"STATE *: *4" >nul 2>&1
  if !ERRORLEVEL! EQU 0 (
    set "RUNNING_OK=1"
    goto :after_running_check
  )
)
:after_running_check
if "!RUNNING_OK!" NEQ "1" (
  set "STATE_TEXT=UNKNOWN"
  for /f "tokens=4" %%S in ('sc query "%SERVICE_NAME%" ^| findstr /R /C:"STATE"') do set "STATE_TEXT=%%S"
  call :restore_and_fail "Task is not running. Current status: !STATE_TEXT!."
  exit /b 1
)

if "!HAS_BACKUP!" EQU "1" del /f /q "%BACKUP_PATH%" >nul 2>&1

cls
echo Setup is complete, you can safely delete this file.
echo Press any key to exit.
pause >nul
endlocal
exit /b 0

:restore_and_fail
if "%TASK_CREATED%"=="1" (
  sc stop "%SERVICE_NAME%" >nul 2>&1
  sc delete "%SERVICE_NAME%" >nul 2>&1
)
if "%HAS_BACKUP%"=="1" (
  copy /y "%BACKUP_PATH%" "%EXE_PATH%" >nul 2>&1
)
del /f /q "%TMP_PATH%" >nul 2>&1
call :fail "%~1"
exit /b 1

:fail
cls
echo %~1
echo Press any key to continue.
pause >nul
exit /b 1
