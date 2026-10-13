@echo off
setlocal
set "CONFIG_ROOT=%~dp0"
cd /d "%CONFIG_ROOT%..\..\DotNet" || exit /b 1

if defined DOTNET_EXECUTABLE goto dotnet_ready
for /f "delims=" %%I in ('where dotnet 2^>nul') do if not defined DOTNET_EXECUTABLE set "DOTNET_EXECUTABLE=%%I"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET_EXECUTABLE=%DOTNET_ROOT%\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_EXECUTABLE=%ProgramFiles%\dotnet\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_EXECUTABLE=%USERPROFILE%\.dotnet\dotnet.exe"
if defined DOTNET_EXECUTABLE goto dotnet_ready
>&2 echo ConfigGenerator requires .NET SDK 10.0.401 and .NET 8 runtime. Install them or set DOTNET_EXECUTABLE to the absolute dotnet path.
exit /b 127

:dotnet_ready
"%DOTNET_EXECUTABLE%" run --project ConfigGenerator --no-launch-profile -- generate --target all
exit /b %errorlevel%
