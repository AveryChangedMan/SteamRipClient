@echo off
setlocal
set PROJECT_NAME=SteamRipApp
set OUTPUT_DIR=Dist\SteamRip

cd /d "%~dp0"

echo Starting Production Build for %PROJECT_NAME%...

set "NEW_VER="
set /p NEW_VER="Enter new version (e.g. 1.5.2.8) or leave blank to keep current: "
if "%NEW_VER%"=="" (
    echo Keeping existing version...
) else (
    echo Updating version to %NEW_VER%...
    powershell -Command "$manifest = [xml](Get-Content 'Package.appxmanifest'); $manifest.Package.Identity.Version = '%NEW_VER%'; $manifest.Save('Package.appxmanifest'); (Get-Content 'Core\GlobalSettings.cs') -replace 'AppVersion \{ get; set; \} = \".*?\"', ('AppVersion { get; set; } = \"%NEW_VER%\"') | Set-Content 'Core\GlobalSettings.cs'; if (Test-Path 'CHANGELOG.md') { (Get-Content 'CHANGELOG.md') -replace 'v\d+\.\d+\.\d+\.\d+', 'v%NEW_VER%' -replace 'version \d+\.\d+\.\d+\.\d+', 'version %NEW_VER%' | Set-Content 'CHANGELOG.md' }; (Get-Content 'MainWindow.xaml.cs') -replace 'ParseVersion\(\"\d+\.\d+\..*?\"\)', 'ParseVersion(\"%NEW_VER%\")' -replace 'AppVersion = \"\d+\.\d+\..*?\"', 'AppVersion = \"%NEW_VER%\"' | Set-Content 'MainWindow.xaml.cs'; echo 'Successfully Synchronized all metadata and manifests to version: %NEW_VER%'"
)

set "CLEAN_BUILD="
set /p CLEAN_BUILD="Do you want to clean previous artifacts before building? (Y/N) [Default: Y]: "
if /I "%CLEAN_BUILD%"=="N" (
    echo Skipping clean step...
) else (
    if exist "%OUTPUT_DIR%" (
        echo Cleaning previous distribution folder...
        rmdir /s /q "%OUTPUT_DIR%"
    )
    echo Cleaning project artifacts...
    if exist "bin" rmdir /s /q "bin"
    if exist "obj" rmdir /s /q "obj"
    dotnet clean -c Release
)

echo Building project (Release x64)...
dotnet build -c Release -p:Platform=x64

echo Generating Signed Production MSIX Package...
dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint="8829DFD5386F422824A49A2CFCB78E1943E56A37"

if %ERRORLEVEL% neq 0 (
    echo.
    echo Packaging FAILED.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Extracting MSIX and bundling assets...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

for /r bin\x64\Release %%f in (*.msix) do (
    copy /Y "%%f" "%OUTPUT_DIR%\" >nul
    echo Bundled MSIX: %%~nxf
)

echo Creating Portable Binary Folder...
mkdir "%OUTPUT_DIR%\Portable"
xcopy /E /Y /I bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\* "%OUTPUT_DIR%\Portable\" >nul

if exist "Redist" (
    echo Bundling Redist folder...
    mkdir "%OUTPUT_DIR%\Redist"
    xcopy /E /Y /I Redist\* "%OUTPUT_DIR%\Redist\" >nul
)

echo.
echo Bundling Certificate and Trust Utility...
if exist "SteamRipApp.pfx" (
    copy /Y "SteamRipApp.pfx" "%OUTPUT_DIR%\" >nul
    echo Bundled: SteamRipApp.pfx
)

if exist "TrustCertificate.bat" (
    copy /Y "TrustCertificate.bat" "%OUTPUT_DIR%\" >nul
    echo Bundled: TrustCertificate.bat
)

echo.
echo BUILD COMPLETE!
echo Location: %cd%\%OUTPUT_DIR%
echo.
pause