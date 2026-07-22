param(
    [string[]]$Runtime = @(
        "win-x64", "win-arm64",
        "linux-x64", "linux-arm64",
        "osx-x64", "osx-arm64"
    ),
    [string]$NuGetSource = "https://mirrors.huaweicloud.com/repository/nuget/v3/index.json",
    [string]$NuGetFallbackSource = "https://api.nuget.org/v3/index.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "AIHubRouter.slnx"
$configFile = Join-Path $repoRoot "NuGet.Config"
$cliProject = Join-Path $repoRoot "src/AIHubRouter.Cli/AIHubRouter.Cli.csproj"
$desktopProject = Join-Path $repoRoot "src/AIHubRouter.Desktop/AIHubRouter.Desktop.csproj"
$testProject = Join-Path $repoRoot "tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts"

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

$commonRestore = @(
    "--configfile", $configFile,
    "--source", $NuGetSource,
    "-p:NuGetAudit=false",
    "-m:1"
)

Invoke-Dotnet (@("restore", $solution) + $commonRestore)
Invoke-Dotnet @("build", $solution, "-c", "Release", "--no-restore", "-m:1")
Invoke-Dotnet @("run", "--project", $testProject, "-c", "Release", "--no-build")

foreach ($rid in $Runtime) {
    $ridRoot = Join-Path $artifactsRoot $rid
    $cliOutput = Join-Path $ridRoot "cli"
    $desktopOutput = Join-Path $ridRoot "desktop"
    Remove-Item -LiteralPath $ridRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $ridRoot -Force | Out-Null

    $runtimeRestore = $commonRestore + @("--source", $NuGetFallbackSource)
    Invoke-Dotnet (@("restore", $cliProject, "-r", $rid) + $runtimeRestore)
    Invoke-Dotnet (@("restore", $desktopProject, "-r", $rid) + $runtimeRestore)
    Invoke-Dotnet @(
        "publish", $cliProject, "-c", "Release", "-r", $rid,
        "--self-contained", "true", "--no-restore", "-o", $cliOutput,
        "-p:PublishSingleFile=true",
        "-p:DebugType=None", "-p:DebugSymbols=false"
    )
    Invoke-Dotnet @(
        "publish", $desktopProject, "-c", "Release", "-r", $rid,
        "--self-contained", "true", "--no-restore", "-o", $desktopOutput,
        "-p:PublishSingleFile=true",
        "-p:DebugType=None", "-p:DebugSymbols=false"
    )
    Get-ChildItem -LiteralPath $ridRoot -Recurse -File -Filter "*.pdb" |
        Remove-Item -Force
    Write-Host "Published $rid to $ridRoot"
}
