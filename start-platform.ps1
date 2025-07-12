# Signal9 Platform Startup Script
# This script starts all the required services for the Signal9 RMM platform

Write-Host "=== Starting Signal9 RMM Platform ===" -ForegroundColor Cyan
Write-Host ""

# Check if Azure Functions Core Tools are installed
if (-not (Get-Command "func" -ErrorAction SilentlyContinue)) {
    Write-Host "Azure Functions Core Tools not found. Please install them first:" -ForegroundColor Red
    Write-Host "npm install -g azure-functions-core-tools@4 --unsafe-perm true" -ForegroundColor Yellow
    exit 1
}

# Set working directory to solution root
$SolutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $SolutionRoot

Write-Host "Building solution..." -ForegroundColor Yellow
dotnet build --configuration Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed. Please fix compilation errors first." -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Start Web Functions (Port 7072)
Write-Host "Starting Signal9 Web Functions on port 7072..." -ForegroundColor Yellow
$WebFunctionsJob = Start-Job -ScriptBlock {
    Set-Location "$using:SolutionRoot\src\Signal9.Web.Functions"
    func start --port 7072
}
Start-Sleep 3

# Start Agent Functions (Port 7071) 
Write-Host "Starting Signal9 Agent Functions on port 7071..." -ForegroundColor Yellow
$AgentFunctionsJob = Start-Job -ScriptBlock {
    Set-Location "$using:SolutionRoot\src\Signal9.Agent.Functions"
    func start --port 7071
}
Start-Sleep 3

# Start Blazor Web App (Port 7001)
Write-Host "Starting Signal9 Web Portal on port 7001..." -ForegroundColor Yellow
$WebJob = Start-Job -ScriptBlock {
    Set-Location "$using:SolutionRoot\src\Signal9.Web"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ASPNETCORE_URLS = "https://localhost:7001;http://localhost:5001"
    $env:FunctionsBaseUrl = "http://localhost:7072/api"
    dotnet run
}

Write-Host ""
Write-Host "=== Signal9 Platform Started ===" -ForegroundColor Green
Write-Host "Web Portal: https://localhost:7001" -ForegroundColor Cyan
Write-Host "Web Functions: http://localhost:7072" -ForegroundColor Cyan  
Write-Host "Agent Functions: http://localhost:7071" -ForegroundColor Cyan
Write-Host ""
Write-Host "Press Ctrl+C to stop all services..." -ForegroundColor Yellow

# Wait for user to stop
try {
    while ($true) {
        Start-Sleep 1
        # Check if any jobs have failed
        $FailedJobs = Get-Job | Where-Object { $_.State -eq "Failed" }
        if ($FailedJobs) {
            Write-Host "One or more services failed to start:" -ForegroundColor Red
            $FailedJobs | ForEach-Object { 
                Write-Host "- $($_.Name): $($_.StatusMessage)" -ForegroundColor Red
                Receive-Job $_ -ErrorAction SilentlyContinue
            }
            break
        }
    }
} finally {
    Write-Host ""
    Write-Host "Stopping all services..." -ForegroundColor Yellow
    Get-Job | Stop-Job
    Get-Job | Remove-Job -Force
    Write-Host "All services stopped." -ForegroundColor Green
}
