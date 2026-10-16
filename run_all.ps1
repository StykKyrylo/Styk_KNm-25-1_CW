param(
    [ValidateSet("setup", "test", "reproduce-small", "reproduce", "clean")]
    [string]$Target = "reproduce-small",
    [int]$StudentNumber = 1
)

$ErrorActionPreference = "Stop"

$seedVal = 1000 * $StudentNumber + 8
Write-Host ">>> Target: $Target (StudentNumber = $StudentNumber, seed = $seedVal)" -ForegroundColor Cyan

function Invoke-Setup {
    Write-Host ">>> [setup] Restoring packages and building Release..." -ForegroundColor Yellow
    dotnet restore KursovaKP08.sln
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet restore failed! Please check your network/NuGet settings." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    dotnet build KursovaKP08.sln -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Invoke-Test {
    Write-Host ">>> [test] Running xUnit correctness tests..." -ForegroundColor Yellow
    dotnet test tests/KursovaKP08.Tests.csproj -c Release --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Unit tests failed!" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

switch ($Target) {
    "clean" {
        Write-Host ">>> Cleaning build artifacts..." -ForegroundColor Yellow
        dotnet clean KursovaKP08.sln -c Release
        Get-ChildItem -Path . -Recurse -Include bin,obj | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    "setup" {
        Invoke-Setup
    }
    "test" {
        Invoke-Setup
        Invoke-Test
    }
    "reproduce-small" {
        Invoke-Setup
        Invoke-Test
        Write-Host ">>> [reproduce-small] Starting small experiment mode (~1-2 min)..." -ForegroundColor Green
        dotnet run --project src/KursovaKP08.csproj -c Release --no-build -- --profile small --student-number $StudentNumber
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    "reproduce" {
        Invoke-Setup
        Invoke-Test
        Write-Host ">>> [reproduce] Starting full computational experiment..." -ForegroundColor Green
        dotnet run --project src/KursovaKP08.csproj -c Release --no-build -- --profile full --student-number $StudentNumber
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
