param(
    [string]$ArtifactsPath = (Join-Path $PSScriptRoot '..\artifacts\verification')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)

Push-Location $root
try {
    & dotnet build CalibrationForge.sln --configuration Release --artifacts-path $ArtifactsPath --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

    & dotnet test tests\CalibrationForge.Core.Tests --configuration Release --artifacts-path $ArtifactsPath --no-build --no-restore --nologo --verbosity minimal --logger trx --results-directory (Join-Path $ArtifactsPath 'TestResults')
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed with exit code $LASTEXITCODE." }

    $smoke = Join-Path $ArtifactsPath 'bin\CalibrationForge.Desktop.SmokeTests\release\CalibrationForge.Desktop.SmokeTests.dll'
    if (-not (Test-Path -LiteralPath $smoke)) { throw "Desktop smoke-check executable is missing: $smoke" }
    & dotnet $smoke
    if ($LASTEXITCODE -ne 0) { throw "Desktop smoke checks failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
