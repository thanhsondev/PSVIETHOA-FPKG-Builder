@echo off
setlocal
start "PKG Build GUI" powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0build-gui.ps1"
