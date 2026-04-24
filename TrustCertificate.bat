@echo off
setlocal
set PFX_FILE=SteamRipApp.pfx
set PFX_PASS=steamrip

echo SteamRipApp Certificate Trust Utility
echo ------------------------------------------

cd /d "%~dp0"

net session >nul 2>&1
if %ERRORLEVEL% equ 0 (
    goto :RunTrust
) else (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

:RunTrust
if not exist "%PFX_FILE%" (
    echo Error: '%PFX_FILE%' not found.
    echo Path: "%cd%\%PFX_FILE%"
    pause
    exit /b
)

echo Importing '%PFX_FILE%' into Trusted Root Certification Authorities...

certutil -f -p %PFX_PASS% -importpfx Root "%PFX_FILE%"

if %ERRORLEVEL% equ 0 (
    echo.
    echo Success: The certificate is now trusted on this machine.
    echo You can now run the 'SteamRipApp.msix' installer.
) else (
    echo.
    echo Failed to import certificate. Code: %ERRORLEVEL%
)

echo.
pause
