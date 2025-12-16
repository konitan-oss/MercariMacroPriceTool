Param()

$ErrorActionPreference = 'Stop'

$solutionDir = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
Set-Location $solutionDir
$appProj = Join-Path $solutionDir 'src\MercariMacroPriceTool.App\MercariMacroPriceTool.App.csproj'
$msPlaywrightDir = Join-Path $solutionDir 'src\MercariMacroPriceTool.App\ms-playwright'
if (-not (Test-Path $msPlaywrightDir)) {
    New-Item -ItemType Directory -Path $msPlaywrightDir -Force | Out-Null
}

Write-Host "[1/6] Release build" -ForegroundColor Cyan
& dotnet build $appProj -c Release

Write-Host "[2/6] Set PLAYWRIGHT_BROWSERS_PATH for install" -ForegroundColor Cyan
$env:PLAYWRIGHT_BROWSERS_PATH = (Resolve-Path $msPlaywrightDir).Path

$playwrightPs1 = Join-Path $solutionDir 'src\MercariMacroPriceTool.App\bin\Release\net8.0-windows\playwright.ps1'
if (-not (Test-Path $playwrightPs1)) {
    throw "playwright.ps1 not found at $playwrightPs1"
}

Write-Host "[3/6] Install chromium via playwright.ps1" -ForegroundColor Cyan
& $playwrightPs1 install chromium

Write-Host "[4/6] Publish self-contained single file" -ForegroundColor Cyan
& dotnet publish $appProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

$publishDir = Join-Path $solutionDir 'src\MercariMacroPriceTool.App\bin\Release\net8.0-windows\win-x64\publish'
$publishMsPlaywright = Join-Path $publishDir 'ms-playwright'

Write-Host "[5/6] Verify bundled ms-playwright" -ForegroundColor Cyan
if (-not (Test-Path $publishMsPlaywright)) {
    throw "publish output missing ms-playwright at $publishMsPlaywright"
}

Write-Host "[6/6] Completed. Publish folder ready at $publishDir" -ForegroundColor Green
