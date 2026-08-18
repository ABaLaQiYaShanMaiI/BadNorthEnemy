#Requires -Version 5.1
<#
.SYNOPSIS
    编译 + 复制到 BepInEx/plugins + 哈希校验（工程卫生：把 README 里的手工步骤自动化）。

.DESCRIPTION
    1. 解析游戏目录 BadNorthDir（命令行 > 环境变量 > csproj 默认值）；
    2. dotnet build（-p:BadNorthDir 传入）；
    3. 把 bin\<Configuration>\net472\BadNorthBlackSpearman1.3.dll 复制到 <BadNorthDir>\BepInEx\plugins\
       （旧 DLL 先备份为 .bak_<时间戳>，不覆盖 .bak_* 备份）；
    4. SHA256 校验源/目标一致，输出摘要。

.EXAMPLE
    .\build.ps1                          # Release，用环境变量或 csproj 默认游戏目录
    .\build.ps1 -BadNorthDir "D:\Games\Bad North" -Configuration Debug
    .\build.ps1 -SkipDeploy               # 只编译，不复制不校验
#>
param(
    [string]$BadNorthDir = $env:BadNorthDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'BadNorthBlackSpearman1.3\BadNorthBlackSpearman1.3.csproj'
$dllName = 'BadNorthBlackSpearman1.3.dll'

# ---------- 1. 解析游戏目录 ----------
if (-not $BadNorthDir) {
    # 从 csproj 读取 <BadNorthDir> 默认值（SDK 风格项目无命名空间）
    try {
        $xml = [xml](Get-Content $proj -Raw)
        $node = $xml.SelectSingleNode('//BadNorthDir')
        if ($node -and $node.'#text') { $BadNorthDir = $node.'#text'.Trim() }
    } catch { }
}
if (-not $BadNorthDir) { $BadNorthDir = 'D:\Steam\steamapps\common\BadNorth' }

if (-not (Test-Path $BadNorthDir)) {
    Write-Error "游戏目录不存在: $BadNorthDir`n  请用 -BadNorthDir <游戏根目录> 或设置环境变量 BadNorthDir。"
}
$managed = Join-Path $BadNorthDir 'BadNorth_Data\Managed'
$plugins = Join-Path $BadNorthDir 'BepInEx\plugins'
foreach ($need in @("$managed\Assembly-CSharp.dll", "$managed\UnityEngine.CoreModule.dll", "$plugins\MMHOOK-Assembly-CSharp.dll", "$plugins\..\core\BepInEx.dll")) {
    if (-not (Test-Path $need)) { Write-Error "缺少游戏引用，请确认 BadNorthDir 指向完整游戏安装: $need" }
}
Write-Host "[build] BadNorthDir = $BadNorthDir"
Write-Host "[build] Configuration = $Configuration"

# ---------- 2. 编译 ----------
Push-Location $root
try {
    $output = & dotnet build $proj -c $Configuration -nologo -v minimal -p:BadNorthDir="$BadNorthDir" 2>&1
    $output | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（exit=$LASTEXITCODE）" }
} finally { Pop-Location }

$builtDll = Join-Path $root "BadNorthBlackSpearman1.3\bin\$Configuration\net472\$dllName"
if (-not (Test-Path $builtDll)) { Write-Error "找不到编译产物: $builtDll" }

if ($SkipDeploy) {
    Write-Host "[build] SkipDeploy=true，仅编译。产物: $builtDll"
    exit 0
}

# ---------- 3. 部署到 plugins ----------
if (-not (Test-Path $plugins)) { Write-Error "BepInEx/plugins 不存在: $plugins" }
$target = Join-Path $plugins $dllName

# 旧 DLL 备份为 .bak_<时间戳>（不覆盖既有 .bak_*）
if (Test-Path $target) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $bak = "$target.bak_$stamp"
    Copy-Item $target $bak -Force
    Write-Host "[build] 旧 DLL 已备份: $bak"
}
Copy-Item $builtDll $target -Force

# 可选：外部头像/长矛皮肤覆盖文件（已内嵌 DLL，此处仅当 bin 里有才带，供热替换免重编译）
$binRes = Join-Path (Split-Path -Parent $builtDll) 'Resources'
$icon = Join-Path $binRes 'black_spearman_icon.png'
if (-not (Test-Path $icon)) { $icon = Join-Path (Split-Path -Parent $builtDll) 'black_spearman_icon.png' }
if (Test-Path $icon) { Copy-Item $icon (Join-Path $plugins 'black_spearman_icon.png') -Force }
foreach ($skin in @('spear_skin_0.png', 'spear_skin_1.png', 'spear_skin_2.png')) {
    $p = Join-Path $binRes $skin
    if (Test-Path $p) { Copy-Item $p (Join-Path $plugins $skin) -Force }
}

# ---------- 4. SHA256 校验 ----------
$hashSrc = (Get-FileHash $builtDll -Algorithm SHA256).Hash
$hashDst = (Get-FileHash $target -Algorithm SHA256).Hash
if ($hashSrc -ne $hashDst) { Write-Error "哈希校验失败: $builtDll != $target" }

$bytes = (Get-Item $target).Length
Write-Host "[build] 已部署: $target"
Write-Host "[build] SHA256 = $hashSrc ($bytes bytes) 哈希 MATCH ✓"
