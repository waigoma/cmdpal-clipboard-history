# Builds the extension, signs a copy of the unsigned MSIX from artifacts\unsigned with the self-signed development certificate
# (SHA256, no timestamp server, so nothing is sent over the network) and prints the signed MSIX path as the last line.
# Runs from any current directory and exits non-zero on failure. Trust is not established here; install.ps1 explains what
# the user must do.
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$unsignedDir = Join-Path $artifacts 'unsigned'
$buildScript = Join-Path $PSScriptRoot 'build.ps1'
$certScript = Join-Path $PSScriptRoot 'new-certificate.ps1'
$exeName = 'ClipboardHistory.Extension.exe'
$minExeBytes = 5MB
# Set once the signing copy exists, so the catch block can remove it when a step fails.
$workPath = $null

try {
    Write-Output 'ビルドしています (Release, x64, Native AOT)...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "ビルドに失敗しました (終了コード $LASTEXITCODE)。"
    }

    # build.ps1 has just replaced this folder with the MSIX of the build that ran, so exactly one file is expected.
    $unsignedFiles = @(Get-ChildItem -LiteralPath $unsignedDir -Filter 'ClipboardHistory_*.msix' -File -ErrorAction SilentlyContinue)
    if ($unsignedFiles.Count -ne 1) {
        throw ('artifacts\unsigned に署名対象の .msix が 1 つだけありません (現在 {0} 個)。' -f $unsignedFiles.Count)
    }
    $unsigned = $unsignedFiles[0]

    $unsignedInfo = Read-MsixInfo -Path $unsigned.FullName
    if ($null -eq $unsignedInfo) {
        throw '.msix に AppxManifest.xml または実行ファイルが含まれていません。'
    }

    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    $msixPath = Join-Path $artifacts ('ClipboardHistory_{0}_x64.msix' -f $unsignedInfo.Version)

    # Signing happens under a temp name, so a failure never leaves an unsigned or half-signed package under the final name.
    $workPath = Join-Path $artifacts ('signing-{0}.msix' -f ([guid]::NewGuid().ToString('N')))
    Copy-Item -LiteralPath $unsigned.FullName -Destination $workPath -Force

    Write-Output '署名用の証明書を準備しています...'
    $certOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $certScript)
    if ($LASTEXITCODE -ne 0) {
        throw "証明書の準備に失敗しました (終了コード $LASTEXITCODE)。"
    }
    $certOutput | Out-Host
    $thumbprint = ([string]$certOutput[-1]).Trim()

    $cert = Get-Item -LiteralPath ('Cert:\CurrentUser\My\{0}' -f $thumbprint)
    if (-not $cert.HasPrivateKey) {
        throw '証明書に秘密鍵がありません。new-certificate.ps1 で作り直してください。'
    }

    $signtool = Find-SignTool
    if ($null -eq $signtool) {
        throw 'signtool.exe が見つかりません。NuGet の microsoft.windows.sdk.buildtools か Windows SDK を導入してください。'
    }
    Write-Output ('署名ツール: {0}' -f $signtool)

    # No /tr or /td: a timestamp server would be a network call, and the package does not need one to install.
    & $signtool sign /fd SHA256 /sha1 $thumbprint $workPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "署名に失敗しました (終了コード $LASTEXITCODE)。"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $workPath
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw '署名者の証明書が指定したものと一致しません。'
    }
    Write-Output ('署名の状態: {0} (証明書をまだ信頼していないため、NotTrusted や UnknownError は想定どおりです)' -f $signature.Status)

    $signedInfo = Read-MsixInfo -Path $workPath
    if ($null -eq $signedInfo -or $signedInfo.ExeBytes -le $minExeBytes) {
        throw ('署名済みの .msix の {0} が 5 MB を超えていません。AOT のビルドを確認してください。' -f $exeName)
    }

    # Every check has passed, so the previous packages are replaced only now and the failed runs never touch them.
    Get-ChildItem -LiteralPath $artifacts -Filter 'ClipboardHistory_*.msix' -File | Remove-Item -Force
    Move-Item -LiteralPath $workPath -Destination $msixPath -Force
    $workPath = $null
}
catch {
    if ($null -ne $workPath -and (Test-Path -LiteralPath $workPath)) {
        Remove-Item -LiteralPath $workPath -Force
    }
    [Console]::Error.WriteLine(('パッケージの作成に失敗しました: {0}' -f $_.Exception.Message))
    exit 1
}

Write-Output $msixPath
