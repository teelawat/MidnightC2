@echo off
setlocal EnableDelayedExpansion

echo [!] Midnight C2 Build System
echo ==========================================

:: 1. Find .NET SDK (Build C#)
echo [*] Building MidnightAgent (C# DLL)...

:: Use dotnet CLI directly (should be in PATH)
dotnet build "MidnightAgent\MidnightAgent.csproj" -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [!] ERROR: Failed to build C# Agent with dotnet CLI.
    echo     Trying MSBuild fallback...
    
    :: Fallback to specific MSBuild if needed
    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set "MSBUILD_PATH=%%i"
    )
    
    if "!MSBUILD_PATH!"=="" (
        echo [!] ERROR: Could not find MSBuild or dotnet CLI.
        pause
        exit /b 1
    )
    
    "!MSBUILD_PATH!" "MidnightAgent\MidnightAgent.csproj" /p:Configuration=Release /t:Rebuild /v:m
    if %ERRORLEVEL% NEQ 0 (
        echo [!] ERROR: Build failed.
        pause
        exit /b 1
    )
)

echo [OK] C# Build Successful.

:: 2. Setup C++ Environment for Rust
echo.
echo [*] Setting up Rust Environment...

set "VS_PATH="
:: Try standard paths first
if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" (
    set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
) else if exist "D:\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" (
    set "VS_PATH=D:\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
) else (
    :: Try finding via vswhere if installed
    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
        if exist "%%i\VC\Auxiliary\Build\vcvars64.bat" (
            set "VS_PATH=%%i\VC\Auxiliary\Build\vcvars64.bat"
        )
    )
)

if "!VS_PATH!"=="" (
    echo [!] ERROR: Visual Studio C++ Build Tools not found!
    echo     Please install "Desktop development with C++" workload.
    pause
    exit /b 1
)

echo [*] Initializing C++ Tools from: "!VS_PATH!"
call "!VS_PATH!" >nul 2>&1

:: 3. Build Rust Loader
echo.
echo [*] Building MidnightLoader (Rust)...
cd MidnightLoader

:: Clean only if needed
if not exist target mkdir target

cargo build --release
if %ERRORLEVEL% NEQ 0 (
    echo [!] ERROR: Failed to build Rust Loader.
    echo     Log above for details.
    pause
    exit /b 1
)

echo.
echo ==========================================
echo [SUCCESS] ALL BUILDS COMPLETE!
echo [OUTPUT] MidnightLoader\target\release\midnight_loader.exe
echo ==========================================
pause
