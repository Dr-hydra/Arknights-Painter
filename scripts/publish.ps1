[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SingleFile,

    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\ArknightsPainter.App\ArknightsPainter.App.csproj'
$outputName = if ($SingleFile) {
    'ArknightsPainter-win-x64-single-file'
} else {
    'ArknightsPainter-win-x64'
}
$output = Join-Path $repositoryRoot "artifacts\$outputName"
$buildOutput = Join-Path $repositoryRoot "src\ArknightsPainter.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0"
$resolvedRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedOutput = [IO.Path]::GetFullPath($output)

if (-not $resolvedOutput.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must stay inside the repository: $resolvedOutput"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

if ($SingleFile) {
    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        -p:Platform=x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:EnableMsixTooling=true `
        -o $output

    Get-ChildItem -LiteralPath $output -File -Filter '*.pdb' | Remove-Item -Force
} else {
    dotnet build $project -c $Configuration -p:Platform=x64

    New-Item -ItemType Directory -Path $output | Out-Null
    Get-ChildItem -LiteralPath $buildOutput -Force |
        Where-Object { $_.Name -ne 'win-x64' } |
        Copy-Item -Destination $output -Recurse -Force

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $output
}

Write-Host "Published portable build to $output"

$publishedExe = Join-Path $output 'ArknightsPainter.App.exe'
if ($SkipSmoke) {
    Write-Host 'Startup smoke test skipped.'
    return
}

$smokeProcess = Start-Process -FilePath $publishedExe -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 5
$runningProcess = Get-Process -Id $smokeProcess.Id -ErrorAction SilentlyContinue
if (-not $runningProcess) {
    throw "Published application exited during startup with code $($smokeProcess.ExitCode)."
}

Stop-Process -Id $smokeProcess.Id
Write-Host 'Startup smoke test passed.'
