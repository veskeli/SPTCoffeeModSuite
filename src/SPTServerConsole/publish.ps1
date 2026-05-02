param(
    [string]$Project = ".",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [bool]$SingleFile = $true,
    [string]$Configuration = "Release",
    [string]$Output = "publish"
)

# Build args safely
$scString = $SelfContained.ToString().ToLower()
$sfString = $SingleFile.ToString().ToLower()

$args = @(
    "publish",
    $Project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $scString,
    "-o", $Output,
    "/p:PublishSingleFile=$sfString"
)

Write-Host "Running: dotnet $($args -join ' ')" -ForegroundColor Cyan

# Execute
& dotnet @args
exit $LASTEXITCODE