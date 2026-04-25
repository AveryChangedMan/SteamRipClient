@echo off
SETLOCAL EnableDelayedExpansion

SET "SOURCE_DIR=%~dp0"
SET "DEST_DIR=C:\Users\sam23\C#\Pirate\Release"

echo Setting up Release directory at: %DEST_DIR%
if not exist "%DEST_DIR%" mkdir "%DEST_DIR%"

echo.
echo [1/3] Copying Root Source Files (*.xaml, *.cs, *.csproj, *.appxmanifest, *.md, *.bat, .cert_thumbprint)...

xcopy "%SOURCE_DIR%*.xaml" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%*.cs" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%*.csproj" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%*.appxmanifest" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%*.pfx" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%*.md" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%.cert_thumbprint" "%DEST_DIR%\" /Y /I

xcopy "%SOURCE_DIR%CollectRelease.bat" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%CreateInstaller.bat" "%DEST_DIR%\" /Y /I
xcopy "%SOURCE_DIR%TrustCertificate.bat" "%DEST_DIR%\" /Y /I

echo.
echo [2/3] Copying Source Folders (Core, Native, Assets, JS, Redist, SteamRipInjector)...


robocopy "%SOURCE_DIR%Core" "%DEST_DIR%\Core" /E /IS /XD bin obj
robocopy "%SOURCE_DIR%Native" "%DEST_DIR%\Native" /E /IS /XD bin obj
robocopy "%SOURCE_DIR%Assets" "%DEST_DIR%\Assets" /E /IS /XD bin obj
robocopy "%SOURCE_DIR%JS" "%DEST_DIR%\JS" /E /IS /XD bin obj
robocopy "%SOURCE_DIR%Redist" "%DEST_DIR%\Redist" /E /IS /XD bin obj
robocopy "%SOURCE_DIR%SteamRipInjector" "%DEST_DIR%\SteamRipInjector" /E /IS /XD bin obj

echo.
echo [3/3] Done! Files are ready in %DEST_DIR%
pause
