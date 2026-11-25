# Builds the CmdPal clipboard history extension (Release, x64, Native AOT) and copies the loose layout to
# artifacts\layout. Prints the layout path as the last line. Runs from any current directory with Windows PowerShell 5.1
# and exits non-zero when the build or the copy fails.
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ClipboardHistory.Extension\ClipboardHistory.Extension.csproj'
$layout = Join-Path $repoRoot 'artifacts\layout'
$exeName = 'ClipboardHistory.Extension.exe'

# The Native AOT toolchain looks for the VS Installer tools on PATH before it looks anywhere else.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path -LiteralPath $installer) {
    $env:PATH = "$installer;$env:PATH"
}

$msbuildArgs = @(
    '-p:Configuration=Release',
    '-p:Platform=x64',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxPackageSigningEnabled=false'
)

& dotnet build $project @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("build failed: dotnet build exit code $LASTEXITCODE")
    exit $LASTEXITCODE
}

# The build output folder is the one the MSIX packager reads, so it is asked for rather than guessed.
$outDir = (& dotnet msbuild $project -nologo -getProperty:OutDir @msbuildArgs | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($outDir)) {
    [Console]::Error.WriteLine('build failed: could not read the output folder')
    exit 1
}
$outDir = Join-Path (Split-Path -Parent $project) $outDir

if (-not (Test-Path -LiteralPath (Join-Path $outDir $exeName))) {
    [Console]::Error.WriteLine('build failed: the native exe is missing from the build output')
    exit 1
}

# A fresh copy means a file removed from the project cannot linger in the layout and mask a packaging error.
if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
}
Copy-Item -LiteralPath $outDir -Destination $layout -Recurse -Force

Write-Output $layout
