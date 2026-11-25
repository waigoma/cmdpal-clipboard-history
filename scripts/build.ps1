# Builds the CmdPal clipboard history extension (Release, x64, Native AOT), copies the loose layout to artifacts\layout and
# the freshly built unsigned MSIX to artifacts\unsigned. Prints the layout path as the last line. Runs from any current
# directory with Windows PowerShell 5.1 and exits non-zero when the build or a copy fails.
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ClipboardHistory.Extension\ClipboardHistory.Extension.csproj'
$projectDir = Split-Path -Parent $project
$layout = Join-Path $repoRoot 'artifacts\layout'
$unsignedDir = Join-Path $repoRoot 'artifacts\unsigned'
$exeName = 'ClipboardHistory.Extension.exe'

# Taken before the build, so an MSIX left over from an earlier build is never taken for this one.
$buildStarted = Get-Date

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
    [Console]::Error.WriteLine("ビルドに失敗しました: dotnet build の終了コード $LASTEXITCODE")
    exit $LASTEXITCODE
}

# The build output folder is the one the MSIX packager reads, so it is asked for rather than guessed.
$outDir = (& dotnet msbuild $project -nologo -getProperty:OutDir @msbuildArgs | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($outDir)) {
    [Console]::Error.WriteLine('ビルドに失敗しました: 出力フォルダーを取得できませんでした。')
    exit 1
}
$outDir = Join-Path $projectDir $outDir

# The AOT exe is the one the layout must ship; the managed apphost with the same name is only a placeholder.
$nativeExe = Join-Path $outDir (Join-Path 'native' $exeName)
if (-not (Test-Path -LiteralPath $nativeExe)) {
    [Console]::Error.WriteLine('ビルドに失敗しました: OutDir\native に実行ファイルがありません。')
    exit 1
}

# A fresh copy means a file removed from the project cannot linger in the layout and mask a packaging error.
if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
}
Copy-Item -LiteralPath $outDir -Destination $layout -Recurse -Force

# The layout exe must be the native build byte for byte, so a stale managed exe cannot be shipped by mistake.
$layoutExe = Join-Path $layout $exeName
$nativeInfo = Get-Item -LiteralPath $nativeExe
$layoutInfo = Get-Item -LiteralPath $layoutExe
if ($nativeInfo.Length -ne $layoutInfo.Length `
    -or (Get-FileHash -LiteralPath $nativeExe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $layoutExe -Algorithm SHA256).Hash) {
    [Console]::Error.WriteLine('ビルドに失敗しました: layout の実行ファイルが native のビルドと一致しません。')
    exit 1
}

# Only the Release x64 output or the x64 AppPackages folder counts, so a stale package from another configuration is never used.
$msix = Get-ChildItem -LiteralPath $projectDir -Recurse -Filter '*.msix' -File |
    Where-Object { $_.FullName -match '\\bin\\x64\\Release\\' -or $_.FullName -match '\\AppPackages\\[^\\]*_x64[^\\]*\\[^\\]*\.msix$' } |
    Sort-Object -Property LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $msix) {
    [Console]::Error.WriteLine('ビルドに失敗しました: ビルドの出力に未署名の .msix が見つかりません。')
    exit 1
}
if ($msix.LastWriteTime -lt $buildStarted) {
    [Console]::Error.WriteLine(('ビルドに失敗しました: 出力 {0} がこのビルドより前のものです。' -f $msix.Name))
    exit 1
}

$msixInfo = Read-MsixInfo -Path $msix.FullName
if ($null -eq $msixInfo) {
    [Console]::Error.WriteLine('ビルドに失敗しました: .msix に AppxManifest.xml または実行ファイルが含まれていません。')
    exit 1
}

# The folder is emptied first, so the signing step can never pick up an MSIX of another version.
if (Test-Path -LiteralPath $unsignedDir) {
    Remove-Item -LiteralPath $unsignedDir -Recurse -Force
}
New-Item -ItemType Directory -Path $unsignedDir | Out-Null
$unsignedPath = Join-Path $unsignedDir ('ClipboardHistory_{0}_x64.msix' -f $msixInfo.Version)
Copy-Item -LiteralPath $msix.FullName -Destination $unsignedPath -Force

Write-Output $layout

# Explicit success code, see set-version.ps1.
exit 0
