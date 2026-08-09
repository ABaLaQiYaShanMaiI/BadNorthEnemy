@echo off
setlocal enabledelayedexpansion
title BadNorth BlackSpearman v1.2 - Launcher

:: ============================================================
:: 游戏路径检测（按优先级）
:: ============================================================
if defined BADNORTH_DIR (
    set "GAME_DIR=%BADNORTH_DIR%"
    goto :path_set
)

:: Steam 默认路径
set "STEAM_PATH=D:\Steam\steamapps\common\BadNorth"
if exist "%STEAM_PATH%\BadNorth.exe" (
    set "GAME_DIR=%STEAM_PATH%"
    goto :path_set
)

:: 常见备用路径
set "ALT_PATH=C:\Program Files (x86)\Steam\steamapps\common\BadNorth"
if exist "%ALT_PATH%\BadNorth.exe" (
    set "GAME_DIR=%ALT_PATH%"
    goto :path_set
)

echo [ERROR] Cannot find BadNorth.exe!
echo   Set BADNORTH_DIR environment variable or edit this .bat file.
echo   Example: set BADNORTH_DIR=D:\Games\BadNorth
pause
exit /b 1

:path_set
echo ============================================
echo  Bad North BlackSpearman v1.2 - D+F Toolchain
echo ============================================
echo  Game: %GAME_DIR%
echo.

:: ============================================================
:: 路径定义
:: ============================================================
set "DLL=%GAME_DIR%\BadNorth_Data\Managed\Assembly-CSharp.dll"
set "ASSET=%GAME_DIR%\BadNorth_Data\data.unity3d"
set "EXE=%GAME_DIR%\BadNorth.exe"

:: 备用 asset 文件
set "ASSET2=%GAME_DIR%\BadNorth_Data\sharedassets1.resource"

:: ============================================================
:: 工具路径（相对于本 .bat 所在目录）
:: ============================================================
set "SCRIPT_DIR=%~dp0"
set "ENUM_EXE=%SCRIPT_DIR%EnumPatcher\bin\Release\net472\EnumPatcher.exe"
set "ASSET_PY=%SCRIPT_DIR%AssetPatcher\asset_patcher.py"

:: ============================================================
:: 第1步: 检查 EnumPatcher 是否已编译
:: ============================================================
if not exist "%ENUM_EXE%" (
    echo [WARN] EnumPatcher.exe not found at:
    echo        %ENUM_EXE%
    echo.
    echo   Please build first:
    echo     cd EnumPatcher
    echo     dotnet build -c Release
    echo.
    pause
    exit /b 2
)

:: ============================================================
:: 第2步: Part D - 枚举补丁
:: ============================================================
echo [D] Enum Patch: Assembly-CSharp.dll
echo ------------------------------------------

if exist "%DLL%.orig_backup" (
    echo   Backup exists. Restoring from original...
    copy /Y "%DLL%.orig_backup" "%DLL%" >nul
    echo   Restored to original state.
)

echo   Running EnumPatcher...
"%ENUM_EXE%" "%DLL%"
if !ERRORLEVEL! neq 0 (
    echo [ERROR] EnumPatcher failed with code !ERRORLEVEL!
    echo   Restoring from backup...
    if exist "%DLL%.orig_backup" copy /Y "%DLL%.orig_backup" "%DLL%" >nul
    pause
    exit /b 3
)
echo   Enum patch complete.
echo.

:: ============================================================
:: 第3步: Part F - Asset 补丁（可选，视资源文件位置）
:: ============================================================
echo [F] Asset Patch: Prefab cloning
echo ------------------------------------------

:: 确定要 patch 的 asset 文件
set "TARGET_ASSET="
if exist "%ASSET%" set "TARGET_ASSET=%ASSET%"
if exist "%ASSET2%" (
    if not defined TARGET_ASSET set "TARGET_ASSET=%ASSET2%"
)

if not defined TARGET_ASSET (
    echo [WARN] No asset file found.
    echo   Expected: data.unity3d or sharedassets1.resource
    echo   Skipping asset patch — enemy will use cloned prefab at runtime.
    goto :skip_asset
)

if exist "%TARGET_ASSET%.orig_backup" (
    echo   Backup exists. Restoring from original...
    copy /Y "%TARGET_ASSET%.orig_backup" "%TARGET_ASSET%" >nul
    echo   Restored to original state.
)

:: 检查 Python 是否可用
python --version >nul 2>&1
if !ERRORLEVEL! neq 0 (
    echo [WARN] Python not found. Skipping asset patch.
    echo   Install Python 3.8+ and run: pip install -r AssetPatcher\requirements.txt
    goto :skip_asset
)

echo   Running AssetPatcher...
python "%ASSET_PY%" "%TARGET_ASSET%"
if !ERRORLEVEL! neq 0 (
    echo [WARN] AssetPatcher returned code !ERRORLEVEL!
    echo   This may be OK if prefab already exists in the asset.
    echo   See AssetPatcher/README for manual patching instructions.
)

:skip_asset
echo.

:: ============================================================
:: 第4步: 启动游戏
:: ============================================================
echo ============================================
echo  All patches applied. Launching Bad North...
echo ============================================
echo.
echo   To uninstall this mod, run: RestoreBackup.bat
echo.

start "" "%EXE%"

endlocal
exit /b 0
