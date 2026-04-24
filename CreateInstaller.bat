@echo off
setlocal
set PROJECT_NAME=SteamRipApp
set OUTPUT_DIR=Dist\SteamRip

echo Starting build for %PROJECT_NAME%...
echo Incrementing Version...
powershell -Command "[xml]$manifest = Get-Content 'Package.appxmanifest'; $version = [version]$manifest.Package.Identity.Version; $newVersion = New-Object version($version.Major, $version.Minor, ($version.Build + 1), 0); $manifest.Package.Identity.Version = $newVersion.ToString(); $manifest.Save('Package.appxmanifest'); echo 'New Version: ' $newVersion.ToString()"

if exist "%OUTPUT_DIR%" (
    echo Cleaning previous build...
    rmdir /s /q "%OUTPUT_DIR%"
)

echo Building project (Release x64)...
dotnet build -c Release -p:Platform=x64

echo Generating Signed Production MSIX Package...
dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=8829DFD5386F422824A49A2CFCB78E1943E56A37

if %ERRORLEVEL% neq 0 (
    echo.
    echo Error: Packaging failed. 
    echo Ensure certificate is valid and Developer Mode is active.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Extracting MSIX and bundling assets...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

for /r bin\x64\Release %%f in (*.msix) do (
    copy "%%f" "%OUTPUT_DIR%\" >nul
    echo Extracted: %%~nxf
)

if exist "SteamRipApp.pfx" copy "SteamRipApp.pfx" "%OUTPUT_DIR%\" >nul
if exist "TrustCertificate.bat" copy "TrustCertificate.bat" "%OUTPUT_DIR%\" >nul
echo Bundled utilities.

echo.
echo Build complete.
echo Location: %cd%\%OUTPUT_DIR%
echo.
pause
