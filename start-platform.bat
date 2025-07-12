@echo off
echo === Starting Signal9 RMM Platform ===
echo.

REM Check if in solution directory
if not exist "Signal9.sln" (
    echo Error: Please run this script from the solution root directory
    pause
    exit /b 1
)

echo Building solution...
dotnet build --configuration Debug
if errorlevel 1 (
    echo Build failed. Please fix compilation errors first.
    pause
    exit /b 1
)

echo Build successful!
echo.

echo Starting services...
echo Web Portal will be available at: https://localhost:7001
echo Web Functions will be available at: http://localhost:7072  
echo Agent Functions will be available at: http://localhost:7071
echo.

REM Start all services in parallel
start "Signal9 Web Functions" cmd /k "cd src\Signal9.Web.Functions && func start --port 7072"
timeout /t 3 /nobreak > nul

start "Signal9 Agent Functions" cmd /k "cd src\Signal9.Agent.Functions && func start --port 7071" 
timeout /t 3 /nobreak > nul

start "Signal9 Web Portal" cmd /k "cd src\Signal9.Web && set ASPNETCORE_ENVIRONMENT=Development && set ASPNETCORE_URLS=https://localhost:7001;http://localhost:5001 && set FunctionsBaseUrl=http://localhost:7072/api && dotnet run"

echo.
echo All services are starting in separate windows...
echo Close the individual command windows to stop each service.
echo.
pause
