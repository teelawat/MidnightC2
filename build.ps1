# Build script for MidnightC2
# This will build both Agent and Builder and copy them to Output folder

Write-Host "Building MidnightC2..." -ForegroundColor Cyan

# Create Output folder
$outputDir = Join-Path $PSScriptRoot "Output"
if (!(Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Clean Output folder
Write-Host "Cleaning Output folder..." -ForegroundColor Yellow
Remove-Item "$outputDir\*" -Force -ErrorAction SilentlyContinue

# Build Agent (single file, .NET Framework 4.8)
Write-Host "`nBuilding MidnightAgent..." -ForegroundColor Green
dotnet build MidnightAgent -c Release
if ($LASTEXITCODE -eq 0) {
    Copy-Item "MidnightAgent\bin\Release\net48\MidnightAgent.exe" $outputDir
    Write-Host "✓ MidnightAgent.exe copied to Output" -ForegroundColor Green
}

# Publish Builder (single file, .NET 8.0 self-contained)
Write-Host "`nPublishing MidnightBuilder..." -ForegroundColor Green
dotnet publish MidnightBuilder -c Release -o $outputDir
if ($LASTEXITCODE -eq 0) {
    # Keep only the EXE, remove other files
    Get-ChildItem $outputDir -Exclude "*.exe" | Remove-Item -Force -Recurse
    Write-Host "✓ MidnightBuilder.exe published to Output" -ForegroundColor Green
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "Output files:" -ForegroundColor Yellow
Get-ChildItem $outputDir | ForEach-Object {
    $sizeKB = [math]::Round($_.Length / 1KB, 2)
    Write-Host "  $($_.Name) - $sizeKB KB" -ForegroundColor White
}
