@echo off
setlocal EnableExtensions

if "%~1"=="" goto :usage
if "%~2"=="" goto :usage

set "SOURCE_FOLDER=%~f1"
set "OUTPUT_PACKAGE=%~f2"
set "FORCE_ARG="
set "KEEP_ARG="
set "KEYSTONE_ARG="
set "REFERENCE_PACKAGE="
set "COMPRESSION_LEVEL=7"
set "CHUNK_COUNT=100"

:parse_options
if "%~3"=="" goto :build
set "CURRENT_OPTION=%~3"
if /I "%~3"=="force" goto :option_force
if /I "%~3"=="keep" goto :option_keep
if /I "%~3"=="keystone" goto :option_keystone
if /I "%~3"=="reference" goto :option_reference
if /I "%~3"=="compression" goto :option_compression
if /I "%~3"=="chunks" goto :option_chunks
if /I "%CURRENT_OPTION:~0,10%"=="reference=" goto :option_reference_value
if /I "%CURRENT_OPTION:~0,12%"=="compression=" goto :option_compression_value
if /I "%CURRENT_OPTION:~0,7%"=="chunks=" goto :option_chunks_value
set "COMPRESSION_LEVEL=%~3"
goto :next_option

:option_force
set "FORCE_ARG=-Force"
goto :next_option

:option_keep
set "KEEP_ARG=-KeepIntermediate"
goto :next_option

:option_keystone
set "KEYSTONE_ARG=-KeepKeystone"
goto :next_option

:option_reference
if "%~4"=="" goto :usage
set "REFERENCE_PACKAGE=%~4"
shift /3
goto :next_option

:option_reference_value
set "REFERENCE_PACKAGE=%CURRENT_OPTION:~10%"
if not defined REFERENCE_PACKAGE goto :usage
goto :next_option

:option_compression
if "%~4"=="" goto :usage
set "COMPRESSION_LEVEL=%~4"
shift /3
goto :next_option

:option_compression_value
set "COMPRESSION_LEVEL=%CURRENT_OPTION:~12%"
if not defined COMPRESSION_LEVEL goto :usage
goto :next_option

:option_chunks
if "%~4"=="" goto :usage
set "CHUNK_COUNT=%~4"
shift /3
goto :next_option

:option_chunks_value
set "CHUNK_COUNT=%CURRENT_OPTION:~7%"
if not defined CHUNK_COUNT goto :usage

:next_option
shift /3
goto :parse_options

:build
set "REFERENCE_ARG="
if defined REFERENCE_PACKAGE set REFERENCE_ARG=-ReferencePackage "%REFERENCE_PACKAGE%"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-from-folder.ps1" ^
  -SourceFolder "%SOURCE_FOLDER%" ^
  -OutputPackage "%OUTPUT_PACKAGE%" ^
  -CompressionLevel "%COMPRESSION_LEVEL%" ^
  -ChunkCount "%CHUNK_COUNT%" ^
  %REFERENCE_ARG% %FORCE_ARG% %KEEP_ARG% %KEYSTONE_ARG%
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build failed with exit code %RESULT%.
exit /b %RESULT%

:usage
echo Usage: %~nx0 "PROJECT_FOLDER" "OUTPUT.pkg" [compression_level] [chunks=COUNT] [reference=BASE.pkg] [force] [keep] [keystone]
echo Compression level: -4 through 9; default is 7. Also accepted: compression=LEVEL
echo PlayGo chunks: 1 through 800; default is 100. Use chunks=COUNT.
echo Set LIBPROSPERO_TEMP_DIR to place SDK/Python temporary workspace on another drive.
echo.
echo Examples:
echo   %~nx0 "C:\project\game" "D:\build\game.pkg"
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" 9 force keep
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" compression=-2 force
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" chunks=32 force
echo   %~nx0 "C:\project\game-patch" "D:\build\game-patch.pkg" "reference=D:\build\game.pkg" force
exit /b 2
