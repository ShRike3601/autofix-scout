# Test runner script (section 16)
# Usage: .\test.ps1

Param(
    [switch]$Verbose
)

Write-Host "=== AutoFix Scout Unit Tests ===" -ForegroundColor Cyan

$exitCode = dotnet test "C:/my folder/projects/new project/AutoFix Scout/AutoFix Scout.sln" `
    --no-build `
    --filter "FullyQualifiedName~AutoFixScout.Tests" `
    --logger "console;verbosity=detailed"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed with exit code $LASTEXITCODE" -ForegroundColor Red
} else {
    Write-Host "All tests passed!" -ForegroundColor Green
}

Write-Host "=== Test run complete ===" -ForegroundColor Cyan
