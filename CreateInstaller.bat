@echo off
setlocal EnableDelayedExpansion
set PROJECT_NAME=SteamRipApp
set OUTPUT_DIR=Dist\SteamRip

cd /d "%~dp0"

echo Starting Production Build for %PROJECT_NAME%...

set "NEW_VER="
set /p NEW_VER="Enter new version (e.g. 1.6.0.5) or leave blank to keep current: "
if "%NEW_VER%"=="" (
    echo Keeping existing version...
) else (
    echo Updating version to %NEW_VER%...
    powershell -ExecutionPolicy Bypass -Command "$v = '%NEW_VER%'; if ($v) { $err = $false; $q = [char]34; Write-Host '  [1/5] Package.appxmanifest ...'; try { [xml]$m = Get-Content 'Package.appxmanifest'; $m.Package.Identity.Version = $v; $m.Save((Resolve-Path 'Package.appxmanifest')); Write-Host '        OK' } catch { Write-Host ('        FAILED: ' + $_.Exception.Message); $err = $true }; Write-Host '  [2/5] MainWindow.xaml (title) ...'; try { (Get-Content 'MainWindow.xaml') -replace ('Title=' + $q + '.*?SteamRIP Engine v[\d.]+' + $q), ('Title=' + $q + 'SteamRIP Engine v' + $v + $q) | Set-Content 'MainWindow.xaml'; Write-Host '        OK' } catch { Write-Host ('        FAILED: ' + $_.Exception.Message); $err = $true }; Write-Host '  [3/5] MainWindow.xaml.cs (CurrentAppVersion const) ...'; try { (Get-Content 'MainWindow.xaml.cs') -replace ('CurrentAppVersion = ' + $q + '[\d.]+' + $q + ';'), ('CurrentAppVersion = ' + $q + $v + $q + ';') | Set-Content 'MainWindow.xaml.cs'; Write-Host '        OK' } catch { Write-Host ('        FAILED: ' + $_.Exception.Message); $err = $true }; Write-Host '  [4/5] Core\GlobalSettings.cs (AppVersion) ...'; try { (Get-Content 'Core\GlobalSettings.cs') -replace ('AppVersion \{ get; set; \} = ' + $q + '[\d.]+' + $q + ';'), ('AppVersion { get; set; } = ' + $q + $v + $q + ';') -replace ('AppVersion = data\.AppVersion \?\? ' + $q + '[\d.]+' + $q + ';'), ('AppVersion = data.AppVersion ?? ' + $q + $v + $q + ';') | Set-Content 'Core\GlobalSettings.cs'; Write-Host '        OK' } catch { Write-Host ('        FAILED: ' + $_.Exception.Message); $err = $true }; Write-Host '  [5/5] CHANGELOG.md ...'; try { if (Test-Path 'CHANGELOG.md') { (Get-Content 'CHANGELOG.md') -replace 'v\d+\.\d+\.\d+\.\d+', ('v' + $v) | Set-Content 'CHANGELOG.md'; Write-Host '        OK' } } catch { }; if ($err) { Write-Host 'WARNING: Errors occurred.' } else { Write-Host ('Successfully updated to: ' + $v) } }"
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