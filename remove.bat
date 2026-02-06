@echo off
setlocal enabledelayedexpansion

set "SERVICE_NAME=AppZone"
set "INSTALL_DIR=C:\Program Files\AppZone"

net session >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "This tool must be run as Administrator."
  exit /b 1
)

echo Processing...

set "SERVICE_PRESENT=0"
sc query "%SERVICE_NAME%" >nul 2>&1
if !ERRORLEVEL! EQU 0 (
  set "SERVICE_PRESENT=1"
  sc stop "%SERVICE_NAME%" >nul 2>&1
  sc delete "%SERVICE_NAME%" >nul 2>&1

  set "REMOVED_OK=0"
  for /L %%i in (1,1,6) do (
    timeout /t 2 >nul
    sc query "%SERVICE_NAME%" >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
      set "REMOVED_OK=1"
      goto :after_delete_wait
    )
  )
  :after_delete_wait
  if "!REMOVED_OK!" NEQ "1" (
    call :fail "Service could not be removed."
    exit /b 1
  )
)

if exist "%INSTALL_DIR%" (
  rmdir /s /q "%INSTALL_DIR%" >nul 2>&1
  if exist "%INSTALL_DIR%" (
    call :fail "Failed to remove install folder."
    exit /b 1
  )
)

cls
echo Removal is complete, you can safely delete this file.
echo Press any key to exit.
pause >nul
endlocal
exit /b 0

:fail
cls
echo %~1
echo Press any key to continue.
pause >nul
exit /b 1
