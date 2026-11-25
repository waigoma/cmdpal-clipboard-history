# Signs an unsigned MSIX with the release certificate, which is taken from the environment variables SIGNING_PFX_BASE64 and
# SIGNING_PFX_PASSWORD (set as GitHub Environment secrets in the release workflow). Writes the signed package
# ClipboardHistory_<version>_x64.msix and the public certificate waigoma.cer to -OutDir. The certificate and its key are
# removed from the current user store when this run imported them. Nothing secret is printed or written to disk.
# Prints the signed package path as the last line. Exits non-zero on failure and leaves no partial files behind.
# -NoTimestamp is for a local trial only: a release is always timestamped.
param(
    [Parameter(Mandatory)][string]$Msix,
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$exeName = 'ClipboardHistory.Extension.exe'
$minExeBytes = 5MB
$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$timestampUrl = 'http://timestamp.digicert.com'
$store = 'Cert:\CurrentUser\My'

$exitCode = 0
$pfxPath = $null
$workPath = $null
$importedThumbprint = $null
$cert = $null
$createdOutDir = $false
$finalMsix = $null
$cerPath = $null
$cerCreated = $false
$result = $null

try {
    $pfxBase64 = $env:SIGNING_PFX_BASE64
    $pfxPassword = $env:SIGNING_PFX_PASSWORD
    if ([string]::IsNullOrEmpty($pfxBase64) -or [string]::IsNullOrEmpty($pfxPassword)) {
        throw '環境変数 SIGNING_PFX_BASE64 と SIGNING_PFX_PASSWORD を設定してください。値は表示しません。'
    }

    $msixPath = (Resolve-Path -LiteralPath $Msix).ProviderPath
    $info = Read-MsixInfo -Path $msixPath
    if ($null -eq $info) {
        throw '.msix に AppxManifest.xml または実行ファイルが含まれていません。'
    }

    # The certificate is taken as a SecureString and the plain password variable is cleared straight away.
    $securePassword = ConvertTo-SecureString -String $pfxPassword -AsPlainText -Force
    $pfxPassword = $null

    $thumbprintsBefore = @(Get-ChildItem -LiteralPath $store | ForEach-Object { $_.Thumbprint })
    $pfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ('cmdpal-release-{0}.pfx' -f ([guid]::NewGuid().ToString('N')))
    [System.IO.File]::WriteAllBytes($pfxPath, [System.Convert]::FromBase64String($pfxBase64))
    $pfxBase64 = $null

    # The key is imported without -Exportable, so it cannot be read back out of the store.
    try {
        $imported = @(Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation $store -Password $securePassword)
    }
    finally {
        Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
        $pfxPath = $null
    }

    $cert = @($imported | Where-Object { $_.HasPrivateKey })[0]
    if ($null -eq $cert) {
        throw '取り込んだ証明書に秘密鍵がありません。'
    }
    # Only a certificate that this run added may be removed again; a thumbprint that was already there is left alone.
    if ($thumbprintsBefore -notcontains $cert.Thumbprint) {
        $importedThumbprint = $cert.Thumbprint
    }

    if ($cert.Subject -cne $info.Publisher) {
        throw ('証明書の Subject が .msix の Publisher と一致しません (Publisher: {0}, Subject: {1})。' -f $info.Publisher, $cert.Subject)
    }

    $ekuExtension = @($cert.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
    $ekuValues = @()
    if ($ekuExtension.Count -gt 0) {
        $ekuValues = @($ekuExtension[0].EnhancedKeyUsages | ForEach-Object { $_.Value })
    }
    if ($ekuValues -notcontains $codeSigningEku) {
        throw '証明書にコード署名の用途 (EKU 1.3.6.1.5.5.7.3.3) がありません。'
    }

    $now = Get-Date
    if ($now -lt $cert.NotBefore -or $now -gt $cert.NotAfter) {
        throw ('証明書の有効期間外です (有効期限: {0})。' -f $cert.NotAfter.ToString('yyyy-MM-dd'))
    }

    $signtool = Find-SignTool
    if ($null -eq $signtool) {
        throw 'signtool.exe が見つかりません。NuGet の microsoft.windows.sdk.buildtools か Windows SDK を導入してください。'
    }

    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir | Out-Null
        $createdOutDir = $true
    }
    $OutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath

    # Signing happens on a copy under a temp name, so the input and any failed run never leave a half-signed package behind.
    $workPath = Join-Path $OutDir ('signing-{0}.msix' -f ([guid]::NewGuid().ToString('N')))
    Copy-Item -LiteralPath $msixPath -Destination $workPath

    $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint)
    $attempts = 1
    if (-not $NoTimestamp) {
        $signArgs += @('/tr', $timestampUrl, '/td', 'SHA256')
        $attempts = 3
    }
    $signed = $false
    for ($attempt = 1; $attempt -le $attempts -and -not $signed; $attempt++) {
        & $signtool @signArgs $workPath | Out-Host
        if ($LASTEXITCODE -eq 0) {
            $signed = $true
        }
        elseif ($attempt -lt $attempts) {
            Write-Output ('署名に失敗しました。タイムスタンプ サーバーの再試行します ({0}/{1})。' -f ($attempt + 1), $attempts)
            Start-Sleep -Seconds 5
        }
    }
    if (-not $signed) {
        throw '署名に失敗しました。'
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $workPath
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        throw '署名者の証明書が使用した証明書と一致しません。'
    }
    if (-not $NoTimestamp -and $null -eq $signature.TimeStamperCertificate) {
        throw 'タイムスタンプが付いていません。'
    }

    $signedInfo = Read-MsixInfo -Path $workPath
    if ($null -eq $signedInfo -or $signedInfo.ExeBytes -le $minExeBytes) {
        throw ('署名済みの .msix の {0} が 5 MB を超えていません。AOT のビルドを確認してください。' -f $exeName)
    }

    # Every check has passed, so the previous outputs are replaced only now.
    $finalMsix = Join-Path $OutDir ('ClipboardHistory_{0}_x64.msix' -f $signedInfo.Version)
    Get-ChildItem -LiteralPath $OutDir -Filter 'ClipboardHistory_*.msix' -File | Remove-Item -Force
    Move-Item -LiteralPath $workPath -Destination $finalMsix -Force
    $workPath = $null

    # The CERT type writes the public certificate only, so the exported file never carries the key.
    $cerPath = Join-Path $OutDir 'waigoma.cer'
    $cerCreated = $true
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

    $result = $finalMsix
    Write-Output ('署名者の証明書の指紋: {0}' -f $cert.Thumbprint)
    Write-Output ('公開証明書: {0}' -f $cerPath)
}
catch {
    [Console]::Error.WriteLine(('リリース署名に失敗しました: {0}' -f $_.Exception.Message))
    $exitCode = 1
}
finally {
    if ($null -ne $workPath -and (Test-Path -LiteralPath $workPath)) {
        Remove-Item -LiteralPath $workPath -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $pfxPath -and (Test-Path -LiteralPath $pfxPath)) {
        Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $importedThumbprint) {
        try {
            Remove-Item -LiteralPath (Join-Path $store $importedThumbprint) -DeleteKey -Confirm:$false -ErrorAction Stop
            if (Test-Path -LiteralPath (Join-Path $store $importedThumbprint)) {
                throw '証明書がまだ残っています。'
            }
        }
        catch {
            [Console]::Error.WriteLine(('この実行で取り込んだ証明書を削除できませんでした。手動で {0} の証明書と秘密鍵を削除してください。' -f $importedThumbprint))
            $exitCode = 1
        }
    }

    # A failed run must not leave its outputs behind; a successful run keeps them.
    if ($exitCode -ne 0) {
        if ($null -ne $finalMsix -and (Test-Path -LiteralPath $finalMsix)) {
            Remove-Item -LiteralPath $finalMsix -Force -ErrorAction SilentlyContinue
        }
        if ($cerCreated -and (Test-Path -LiteralPath $cerPath)) {
            Remove-Item -LiteralPath $cerPath -Force -ErrorAction SilentlyContinue
        }
        if ($createdOutDir -and (Test-Path -LiteralPath $OutDir) -and @(Get-ChildItem -LiteralPath $OutDir -Force).Count -eq 0) {
            Remove-Item -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}

Write-Output $result
