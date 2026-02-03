@echo off
setlocal enabledelayedexpansion

set "OUT_DIR=C:\Program Files\AppZone"
set "OUT_PATH=%OUT_DIR%\proinfo.txt"

net session >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "This tool must be run as Administrator."
  exit /b 1
)

echo Processing...

if not exist "%OUT_DIR%" (
  mkdir "%OUT_DIR%" >nul 2>&1
  if !ERRORLEVEL! NEQ 0 (
    call :fail "Failed to create output folder."
    exit /b 1
  )
)
if not exist "%OUT_DIR%" (
  call :fail "Output folder does not exist."
  exit /b 1
)

powershell -NoProfile -Command "try { $ErrorActionPreference = 'SilentlyContinue'; $user = $env:USERNAME; $domain = $env:USERDOMAIN; $current = $domain + '\' + $user; $p = Get-Process -IncludeUserName | Where-Object { $_.UserName -eq $current -or $_.UserName -match '\\' + [regex]::Escape($user) + '$' } | Sort-Object ProcessName; if (-not $p) { 'No processes were detected for current user.' } else { 'Computer: %COMPUTERNAME%'; 'Captured: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'); ''; $p | Select-Object @{N='Name';E={$_.ProcessName}}, @{N='Description';E={$_.Description}} | Format-Table -AutoSize | Out-String -Width 300 } } catch { exit 1 }" > "%OUT_PATH%"
if !ERRORLEVEL! NEQ 0 (
  call :fail "Failed to capture running applications."
  exit /b 1
)
if not exist "%OUT_PATH%" (
  call :fail "Capture file not found."
  exit /b 1
)

powershell -NoProfile -Command "try { $ErrorActionPreference = 'SilentlyContinue'; '' | Out-File -FilePath '%OUT_PATH%' -Append -Encoding utf8; 'Services: (Local Machine)' | Out-File -FilePath '%OUT_PATH%' -Append -Encoding utf8; 'Captured: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Out-File -FilePath '%OUT_PATH%' -Append -Encoding utf8; '' | Out-File -FilePath '%OUT_PATH%' -Append -Encoding utf8; Get-Service | Sort-Object DisplayName | Select-Object @{N='Name';E={$_.Name}}, @{N='DisplayName';E={$_.DisplayName}}, @{N='Status';E={$_.Status}}, @{N='StartType';E={$_.StartType}} | Format-Table -AutoSize | Out-String -Width 300 | Out-File -FilePath '%OUT_PATH%' -Append -Encoding utf8 } catch { exit 1 }" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "Failed to capture services."
  exit /b 1
)

powershell -NoProfile -Command "try { Set-Clipboard -Path '%OUT_PATH%'; exit 0 } catch { exit 1 }" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
  call :fail "Failed to copy the file to clipboard."
  exit /b 1
)

cls
echo The file was copied to your clipboard.
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
