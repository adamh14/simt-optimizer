@echo off
rem Spusti Simt Optimizer. Nic se nebuilduje - Optimizer.cs se zkompiluje az za behu.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -WindowStyle Hidden -File "%~dp0Optimizer.ps1"
if errorlevel 1 (
    echo.
    echo Optimalizace neprobehla spravne. Vice informaci najdete v souboru SimtOptimizer.log.
    echo.
    pause
)
