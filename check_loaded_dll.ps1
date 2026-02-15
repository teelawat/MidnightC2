# Check for Admin privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[!] Warning: Not running as Administrator." -ForegroundColor Yellow
    Write-Host "    If the agent is running as SYSTEM, you won't see it."
    Write-Host "    Please run this script as Administrator.`n" -ForegroundColor Yellow
}

$TargetDlls = @("*WinSec*.dll", "*MidnightAgent.dll*")
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "   Checking for Loaded MidnightAgent DLLs    " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$found = $false
$processes = Get-Process powershell,pwsh -ErrorAction SilentlyContinue

$found = $false
# Get processes via WMI to read CommandLine
$procs = Get-WmiObject Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'"

foreach ($p in $procs) {
    $cmd = $p.CommandLine
    
    # Check if CommandLine contains signs of our agent
    if ($cmd -match "MidnightAgent" -or $cmd -match "WinSec" -or $cmd -match "SecurityHost") {
        $found = $true
        Write-Host "`n[+] FOUND SUSPICIOUS PROCESS!" -ForegroundColor Green
        Write-Host "    PID: $($p.ProcessId)" -ForegroundColor Yellow
        Write-Host "    Parent PID: $($p.ParentProcessId)"
        Write-Host "    Owner: $($p.GetOwner().User)" -ForegroundColor Gray
        Write-Host "    Command Line:" -ForegroundColor Cyan
        Write-Host "    $cmd" -ForegroundColor White
    }
}

if (-not $found) {
    Write-Host "`n[-] No agent command lines found." -ForegroundColor Red
}

Write-Host "`nPress any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
