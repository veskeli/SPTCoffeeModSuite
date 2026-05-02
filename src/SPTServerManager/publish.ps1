# powershell
param(
    [string]$ProjectPath = ".",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [string]$Output = "publish",
    [switch]$Trim,
    [switch]$Verbose
)

function Write-Log {
    param($msg)
    if ($Verbose) { Write-Host $msg }
}

# Resolve project file
if (Test-Path $ProjectPath -PathType Container) {
    $proj = Get-ChildItem -Path $ProjectPath -Filter *.csproj -File | Select-Object -First 1
    if (-not $proj) {
        $rp = Resolve-Path $ProjectPath
        Write-Error "No .csproj found in $($rp.Path)"
        exit 1
    }
    $projPath = $proj.FullName
} elseif (Test-Path $ProjectPath -PathType Leaf) {
    $projPath = (Resolve-Path $ProjectPath).Path
} else {
    $rp = Resolve-Path $ProjectPath -ErrorAction SilentlyContinue
    $rpPath = if ($rp) { $rp.Path } else { $ProjectPath }
    Write-Error "Project path '$rpPath' not found."
    exit 1
}

Write-Log "Using project: $projPath"

# Build arguments
$publishArgs = @(
    "publish", $projPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--output", $Output,
    "--nologo",
    "--verbosity", "minimal"
)

# MSBuild properties for single-file
$msbuildProps = @(
    "/p:PublishSingleFile=true",
    "/p:SelfContained=$($SelfContained.ToString().ToLower())",
    "/p:IncludeAllContentForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=true"
)

if ($Trim) {
    $msbuildProps += "/p:PublishTrimmed=true"
} else {
    $msbuildProps += "/p:PublishTrimmed=false"
}

# Append MSBuild props to args
$publishArgs += $msbuildProps

Write-Log "Running: dotnet $($publishArgs -join ' ')"

# Run dotnet publish
$processInfo = & dotnet @publishArgs
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    Write-Error "dotnet publish failed with exit code $exit"
    exit $exit
}

$fullOutput = Resolve-Path $Output -ErrorAction SilentlyContinue
if ($fullOutput) {
    Write-Host "Published single-file to: $($fullOutput.Path)"
} else {
    Write-Host "Published single-file to: $Output"
}
exit 0