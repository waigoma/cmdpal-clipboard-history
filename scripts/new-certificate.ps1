# Creates (or reuses) the self-signed code signing certificate for the clipboard history package and exports only the
# public part to artifacts\Waigoma.cer. The private key stays non-exportable in Cert:\CurrentUser\My. This script never
# trusts the certificate: importing it into LocalMachine\TrustedPeople is left to the user in an elevated PowerShell.
# Prints the thumbprint as the last line. Runs from any current directory and exits non-zero on failure.
$ErrorActionPreference = 'Stop'

$subject = 'CN=Waigoma'
$friendlyName = 'Waigoma CmdPal extensions'
$store = 'Cert:\CurrentUser\My'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$cerPath = Join-Path $artifacts 'Waigoma.cer'

try {
    $now = Get-Date

    # Reusing the certificate keeps the package signer stable across builds, so an installed version stays updatable.
    # A certificate close to expiry is not reused, so a fresh one is made before the signer stops being valid.
    $existing = Get-ChildItem -LiteralPath $store |
        Where-Object { $_.Subject -eq $subject -and $_.FriendlyName -eq $friendlyName -and $_.NotAfter -gt $now.AddDays(30) } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1

    if ($null -ne $existing) {
        Write-Output ('既存の証明書を再利用します (有効期限 {0})。' -f $existing.NotAfter.ToString('yyyy-MM-dd'))
        $cert = $existing
    }
    else {
        Write-Output '証明書を作成します (有効期間 5 年、秘密鍵はエクスポート不可)。'
        # The code signing EKU is set by hand because -Type CodeSigningCert would also add extensions the package does not need.
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $subject `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy NonExportable `
            -HashAlgorithm SHA256 `
            -NotAfter $now.AddYears(5) `
            -CertStoreLocation $store `
            -FriendlyName $friendlyName `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }

    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    # The CERT type writes the public certificate only, so the .cer cannot carry the private key.
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    Write-Output ('公開証明書: {0}' -f $cerPath)
    Write-Output '信頼の登録は行っていません。導入には管理者の PowerShell での登録が必要です (README を参照)。'
}
catch {
    [Console]::Error.WriteLine(('証明書の作成に失敗しました: {0}' -f $_.Exception.Message))
    exit 1
}

Write-Output $cert.Thumbprint
