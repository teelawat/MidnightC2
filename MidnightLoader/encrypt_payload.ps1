$source = "..\MidnightAgent\bin\Release\net48\MidnightAgent.dll"
$dest = "src\agent.bin"
$key = 0xAA

if (-not (Test-Path $source)) {
    Write-Error "Source file not found: $source"
    exit 1
}

$bytes = [System.IO.File]::ReadAllBytes($source)
for ($i = 0; $i -lt $bytes.Length; $i++) {
    $bytes[$i] = $bytes[$i] -bxor $key
}

[System.IO.File]::WriteAllBytes($dest, $bytes)
Write-Host "Encrypted payload written to $dest"
