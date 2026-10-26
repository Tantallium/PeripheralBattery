@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo Install the .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
dotnet publish "src\PeripheralBattery\PeripheralBattery.csproj" -c Release -r win-x64 --self-contained true -o "out"
if errorlevel 1 (
  echo Build failed. See the error above.
  pause
  exit /b 1
)
echo Ready: out\PeripheralBattery.exe
pause
