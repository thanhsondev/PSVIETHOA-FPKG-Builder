@echo off
setlocal EnableExtensions

if "%~1"=="" goto :usage
if "%~2"=="" goto :usage

set "SOURCE_FOLDER=%~f1"
set "OUTPUT_PACKAGE=%~f2"
set "FORCE_ARG="
set "KEEP_ARG="
set "KEYSTONE_ARG="

if /I "%~3"=="force" set "FORCE_ARG=-Force"
if /I "%~3"=="keep" set "KEEP_ARG=-KeepIntermediate"
if /I "%~3"=="keystone" set "KEYSTONE_ARG=-KeepKeystone"
if /I "%~4"=="force" set "FORCE_ARG=-Force"
if /I "%~4"=="keep" set "KEEP_ARG=-KeepIntermediate"
if /I "%~4"=="keystone" set "KEYSTONE_ARG=-KeepKeystone"
if /I "%~5"=="force" set "FORCE_ARG=-Force"
if /I "%~5"=="keep" set "KEEP_ARG=-KeepIntermediate"
if /I "%~5"=="keystone" set "KEYSTONE_ARG=-KeepKeystone"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-from-folder.ps1" ^
  -SourceFolder "%SOURCE_FOLDER%" ^
  -OutputPackage "%OUTPUT_PACKAGE%" ^
  %FORCE_ARG% %KEEP_ARG% %KEYSTONE_ARG%
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build failed with exit code %RESULT%.
exit /b %RESULT%

:usage
echo Usage: %~nx0 "PROJECT_FOLDER" "OUTPUT.pkg" [force] [keep] [keystone]
echo.
echo Examples:
echo   %~nx0 "C:\project\game" "D:\build\game.pkg"
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" force keep
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" force keystone
exit /b 2
