@echo off
setlocal
set "CONFIG_ROOT=%~dp0"
cd /d "%CONFIG_ROOT%" || exit /b 1

if defined DOTNET_EXECUTABLE goto dotnet_ready
for /f "delims=" %%I in ('where dotnet 2^>nul') do if not defined DOTNET_EXECUTABLE set "DOTNET_EXECUTABLE=%%I"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET_EXECUTABLE=%DOTNET_ROOT%\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_EXECUTABLE=%ProgramFiles%\dotnet\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_EXECUTABLE=%USERPROFILE%\.dotnet\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
>&2 echo Luban requires .NET 8. Install it or set DOTNET_EXECUTABLE to the absolute dotnet path.
exit /b 127

:dotnet_ready
if not defined LUBAN_OUTPUT_CODE_DIR set "LUBAN_OUTPUT_CODE_DIR=%CONFIG_ROOT%..\Assets\Scripts\Hotfix\TableConfig\Generated"
if not defined LUBAN_OUTPUT_DATA_DIR set "LUBAN_OUTPUT_DATA_DIR=%CONFIG_ROOT%..\Assets\Config\Excel\Bytes"
if not defined LUBAN_SERVER_OUTPUT_CODE_DIR set "LUBAN_SERVER_OUTPUT_CODE_DIR=%CONFIG_ROOT%..\..\DotNet\Game.Config\Generated"
if not defined LUBAN_SERVER_OUTPUT_DATA_DIR set "LUBAN_SERVER_OUTPUT_DATA_DIR=%CONFIG_ROOT%..\..\DotNet\Game.Config\Bytes"

"%DOTNET_EXECUTABLE%" "%CONFIG_ROOT%Luban\Luban.dll" ^
    -t client -c cs-bin -d bin --strict ^
    --conf "%CONFIG_ROOT%luban.conf" ^
    -x "outputCodeDir=%LUBAN_OUTPUT_CODE_DIR%" ^
    -x "outputDataDir=%LUBAN_OUTPUT_DATA_DIR%"
if not "%errorlevel%"=="0" exit /b %errorlevel%

"%DOTNET_EXECUTABLE%" "%CONFIG_ROOT%Luban\Luban.dll" ^
    -t server -c cs-bin -d bin --strict ^
    --conf "%CONFIG_ROOT%luban.conf" ^
    -x "outputCodeDir=%LUBAN_SERVER_OUTPUT_CODE_DIR%" ^
    -x "outputDataDir=%LUBAN_SERVER_OUTPUT_DATA_DIR%"
exit /b %errorlevel%
