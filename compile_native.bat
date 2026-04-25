@echo off
setlocal

if not exist "Redist" mkdir "Redist"

echo [Native] Compiling NativeHash.cpp...
gcc -O3 -shared -o Redist/NativeHash.dll Native/NativeHash.cpp -lkernel32

if %ERRORLEVEL% equ 0 (
    echo [Native] ✅ Compilation successful: Redist\NativeHash.dll
) else (
    echo [Native] ❌ Compilation failed.
    exit /b 1
)
