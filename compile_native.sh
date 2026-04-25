


mkdir -p Redist

echo "[Native] Compiling NativeHash.cpp..."
gcc -O3 -shared -o Redist/NativeHash.dll Native/NativeHash.cpp -lkernel32

if [ $? -eq 0 ]; then
    echo "[Native] ✅ Compilation successful: Redist/NativeHash.dll"
else
    echo "[Native] ❌ Compilation failed."
    exit 1
fi
