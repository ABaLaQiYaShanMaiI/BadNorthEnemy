# Build.ps1 — BadNorthBlackSpearman v1.2 构建脚本
# 运行此脚本生成最终的单个 Setup.exe
# 用法: .\Build.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " BadNorth BlackSpearman v1.2 Build" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. 构建 BlackSpearmanPlugin.dll
Write-Host "[1/2] Building BlackSpearmanPlugin.dll..." -ForegroundColor Yellow
Push-Location BlackSpearmanPlugin
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }
Pop-Location
Write-Host "  -> OK" -ForegroundColor Green
Write-Host ""

# 2. 构建并发布 Setup.exe（单文件自包含）
Write-Host "[2/2] Publishing Setup.exe (self-contained)..." -ForegroundColor Yellow
Push-Location Setup
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish
if ($LASTEXITCODE -ne 0) { throw "Setup publish failed" }
Pop-Location
Write-Host "  -> OK" -ForegroundColor Green
Write-Host ""

# 完成
$exe = Join-Path $PSScriptRoot "publish\BlackSpearmanSetup.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " BUILD SUCCESS!" -ForegroundColor Green
    Write-Host " Output: publish\BlackSpearmanSetup.exe" -ForegroundColor Green
    Write-Host " Size:   $size MB (self-contained)" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "将此 .exe 发送给玩家即可。玩家只需:"
    Write-Host "  1. 双击 BlackSpearmanSetup.exe"
    Write-Host "  2. 按 [1] 安装"
    Write-Host "  3. 按 [3] 启动游戏"
} else {
    Write-Host "BUILD FAILED" -ForegroundColor Red
}
