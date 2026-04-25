@echo off
setlocal
set PROJECT_NAME=SteamRipApp
set OUTPUT_DIR=Dist\SteamRip

echo Starting Production Build for %PROJECT_NAME%...

echo.
echo [1/4] Compiling Native Dependencies (NativeHash.dll)...
call compile_native.bat
if %ERRORLEVEL% neq 0 (
    echo.
    echo [Native] Compilation FAILED. Build aborted.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/4] Incrementing Version...
powershell -Command "[xml]$manifest = Get-Content 'Package.appxmanifest'; $version = [version]$manifest.Package.Identity.Version; $newVersion = New-Object version($version.Major, $version.Minor, ($version.Build + 1), 0); $manifest.Package.Identity.Version = $newVersion.ToString(); $manifest.Save('Package.appxmanifest'); echo 'New Version: ' $newVersion.ToString()"

if exist "%OUTPUT_DIR%" (
    echo Cleaning previous build...
    rmdir /s /q "%OUTPUT_DIR%"
)

echo.
echo [3/4] Building Portable Binary Folder...
dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:PublishUnpackaged=true -o "%OUTPUT_DIR%\Portable"

echo.
echo [4/4] Generating Signed Production MSIX Package...
dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:PublishUnpackaged=false -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=8829DFD5386F422824A49A2CFCB78E1943E56A37

if %ERRORLEVEL% neq 0 (
    echo.
    echo Packaging FAILED. 
    echo Tip: Ensure your certificate is valid and Developer Mode is ON.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Collecting MSIX package...
for /r bin\x64\Release %%f in (*.msix) do (
    copy "%%f" "%OUTPUT_DIR%\" >nul
    echo Bundled MSIX: %%~nxf
)

if exist "Redist" (
    echo Bundling Redist folder to package...
    mkdir "%OUTPUT_DIR%\Redist"
    xcopy /E /Y /I Redist\* "%OUTPUT_DIR%\Redist\" >nul
)

if exist "SteamRipApp.pfx" copy "SteamRipApp.pfx" "%OUTPUT_DIR%\" >nul
if exist "TrustCertificate.bat" copy "TrustCertificate.bat" "%OUTPUT_DIR%\" >nul
echo Bundled Certificate utilities.

echo.
echo BUILD COMPLETE! 
echo MSIX and Portable binaries are in: %cd%\%OUTPUT_DIR%
echo.
pause
