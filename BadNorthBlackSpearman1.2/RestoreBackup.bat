@echo off
setlocal enabledelayedexpansion
title BadNorth BlackSpearman v1.2 - Restore (Uninstall)

:: ============================================================
:: 游戏路径检测
:: ============================================================
if defined BADNORTH_DIR (
    set "GAME_DIR=%BADNORTH_DIR%"
    goto :path_set
)
set "STEAM_PATH=D:\Steam\steamapps\common\BadNorth"
if exist "%STEAM_PATH%\BadNorth.exe" set "GAME_DIR=%STEAM_PATH%" & goto :path_set
set "ALT_PATH=C:\Program Files (x86)\Steam\steamapps\common\BadNorth"
if exist "%ALT_PATH%\BadNorth.exe" set "GAME_DIR=%ALT_PATH%" & goto :path_set

echo [ERROR] Cannot find BadNorth.exe!
echo   Set BADNORTH_DIR or edit this .bat file.
pause
exit /b 1

:path_set
echo ============================================
echo  Bad North BlackSpearman v1.2 - RESTORE
echo ============================================
echo  Game: %GAME_DIR%
echo.
echo  This will restore ALL patched game files
echo  to their original state.
echo.
echo  Your SAVE FILES will NOT be affected.
echo.

choice /C YN /M "Proceed with restore"

if errorlevel 2 goto :cancel
if errorlevel 1 goto :restore

:cancel
echo Cancelled.
pause
exit /b 0

:restore
echo.

set "RESTORED=0"
set "FAILED=0"

:: === Assembly-CSharp.dll ===
set "FILE=%GAME_DIR%\BadNorth_Data\Managed\Assembly-CSharp.dll"
set "BAK=%FILE%.orig_backup"

if exist "%BAK%" (
    copy /Y "%BAK%" "%FILE%" >nul
    if !ERRORLEVEL! equ 0 (
        echo [OK] Restored: Assembly-CSharp.dll
        set /a RESTORED+=1
    ) else (
        echo [FAIL] Could not restore: Assembly-CSharp.dll
        set /a FAILED+=1
    )
) else (
    echo [SKIP] No backup found for Assembly-CSharp.dll
)

:: === data.unity3d ===
set "FILE=%GAME_DIR%\BadNorth_Data\data.unity3d"
set "BAK=%FILE%.orig_backup"

if exist "%BAK%" (
    copy /Y "%BAK%" "%FILE%" >nul
    if !ERRORLEVEL! equ 0 (
        echo [OK] Restored: data.unity3d
        set /a RESTORED+=1
    ) else (
        echo [FAIL] Could not restore: data.unity3d
        set /a FAILED+=1
    )
) else (
    echo [SKIP] No backup found for data.unity3d
)

:: === sharedassets1.resource ===
set "FILE=%GAME_DIR%\BadNorth_Data\sharedassets1.resource"
set "BAK=%FILE%.orig_backup"

if exist "%BAK%" (
    copy /Y "%BAK%" "%FILE%" >nul
    if !ERRORLEVEL! equ 0 (
        echo [OK] Restored: sharedassets1.resource
        set /a RESTORED+=1
    ) else (
        echo [FAIL] Could not restore: sharedassets1.resource
        set /a FAILED+=1
    )
)

echo.
echo ============================================
echo  Restore complete!
echo    !RESTORED! file(s) restored
echo    !FAILED! file(s) failed
echo ============================================
echo.
echo  Backups (.orig_backup) are preserved.
echo  You can re-apply the mod with LaunchModded.bat
echo  or delete the .orig_backup files to fully remove.

pause
endlocal
exit /b 0
