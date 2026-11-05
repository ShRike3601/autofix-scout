# Build script for AutoFix Scout v1
# Prerequisites: .NET 8 SDK installed
# Usage: .\build.ps1 -Build -Publish -Sign (optional)

Param(
    [switch]$Build,
    [switch]$Publish,
    [switch]$Pack,
    [switch]$Test,
    [switch]$Clean,
    [string]$Output = "bin\Release"
)

$ErrorActionPreference = "Stop"

Write-Host "=== AutoFix Scout Build Script v1.0 ===" -ForegroundColor Cyan

function Build {
    Write-Host "Building solution..." -ForegroundColor Yellow
    
    $exitCode = dotnet build "C:/my folder/projects/new project/AutoFix Scout/AutoFix Scout.sln" `
        -c Release `
        -f net8.0-windows `
        --no-incremental
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        return
    }
    
    Write-Host "Build successful!" -ForegroundColor Green
}

function Publish {
    Write-Host "Publishing portable EXE..." -ForegroundColor Yellow
    
    dotnet publish "C:/my folder/projects/new project/AutoFix Scout/src/AutoFixScout.UI/AutoFixScout.UI.csproj" `
        -c Release `
        -f net8.0-windows `
        -r win-x64 `
        --self-contained true `
        -o "$Output/publish"
    
    Write-Host "Publish complete: $Output/publish/AutoFixScout.exe" -ForegroundColor Green
}

function Pack {
    Write-Host "Packaging MSI (requires WiX Toolset)..." -ForegroundColor Yellow
    
    dotnet pack "C:/my folder/projects/new project/AutoFix Scout/AutoFix Scout.sln" `
        -c Release `
        -f net8.0-windows `
        -o "packages"
    
    Write-Host "Pack complete: packages/*.msi" -ForegroundColor Green
}

function Test {
    Write-Host "Running tests..." -ForegroundColor Yellow
    
    dotnet test "C:/my folder/projects/new project/AutoFix Scout/AutoFix Scout.sln" `
        --no-build `
        --logger "console;verbosity=detailed"
    
    Write-Host "Tests complete." -ForegroundColor Green
}

function Clean {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow
    
    dotnet clean "C:/my folder/projects/new project/AutoFix Scout/AutoFix Scout.sln" -c Release
    Remove-Item "$Output" -Recurse -Force -ErrorAction SilentlyContinue
}

function Sign {
    Write-Host "Code signing (placeholder)..." -ForegroundColor Yellow
    
    # Placeholder: replace with actual signing command once certificate is available
    # e.g.: dotnet sign --cert "path/to/cert.pfx" --key password --timestamp server "AutoFixScout.exe"
    
    Write-Host "Signing placeholder — add your certificate path when ready." -ForegroundColor Cyan
}

# Run requested operations
if ($Build) { Build }
if ($Publish) { Publish }
if ($Pack) { Pack }
if ($Test) { Test }
if ($Clean) { Clean }
if (Sign) { Sign }

Write-Host "=== Build complete ===" -ForegroundColor Cyan
