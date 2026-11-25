# Installs the signed clipboard history package for the current user. Read-only checks come first: the script does not
# import certificates, toggle Developer Mode or change any store. When the signing certificate is not trusted it prints
# the exact elevated command for the user to run and exits 1. Runs from any current directory.
param(
    [string]$Msix,
    [switch]$RemoveDevelopment
)

$ErrorActionPreference = 'Stop'

$packageName = 'waigoma.CmdPal.ClipboardHistory'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$certPath = Join-Path $artifacts 'waigoma.cer'

try {
    if ([string]::IsNullOrEmpty($Msix)) {
        $newest = Get-ChildItem -LiteralPath $artifacts -Filter 'ClipboardHistory_*.msix' -File -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -eq $newest) {
            throw 'artifacts に .msix がありません。先に scripts\package.ps1 を実行してください。'
        }
        $Msix = $newest.FullName
    }

    $msixPath = (Resolve-Path -LiteralPath $Msix).ProviderPath
    Write-Output ('対象: {0}' -f $msixPath)

    $signature = Get-AuthenticodeSignature -LiteralPath $msixPath
    if ($null -eq $signature.SignerCertificate) {
        throw '.msix が署名されていません。scripts\package.ps1 で作り直してください。'
    }
    $thumbprint = $signature.SignerCertificate.Thumbprint

    # The .cer in artifacts is the one the user imports, so it must be the certificate that signed this package.
    $cerThumbprint = $null
    if (Test-Path -LiteralPath $certPath) {
        $cerThumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certPath).Thumbprint
    }
    if ($cerThumbprint -ne $thumbprint) {
        throw 'artifacts\waigoma.cer が署名に使われた証明書と一致しません。scripts\new-certificate.ps1 を実行してから scripts\package.ps1 で作り直してください。'
    }

    $trusted = @(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumbprint })
    if ($trusted.Count -eq 0) {
        Write-Output ''
        Write-Output '署名証明書がまだ信頼されていません。管理者として開いた PowerShell で次のコマンドを実行してから、このスクリプトをもう一度実行してください。'
        Write-Output ''
        Write-Output ('Import-Certificate -FilePath "{0}" -CertStoreLocation Cert:\LocalMachine\TrustedPeople' -f $certPath)
        Write-Output ''
        Write-Output 'この操作は、その PC で waigoma 名義の署名を信頼することを意味します。'
        exit 1
    }

    # A development registration of the same name conflicts with the signed package, so it has to go first.
    $development = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
        Where-Object { $_.IsDevelopmentMode })
    if ($development.Count -gt 0) {
        if (-not $RemoveDevelopment) {
            Write-Output '開発用に登録されたパッケージが残っています。導入の前に削除する必要があります。'
            Write-Output '-RemoveDevelopment を付けて実行すると、開発用の登録を削除してから導入します。対応している環境では -PreserveApplicationData で履歴 (LocalState) を残します。'
            exit 1
        }

        $supportsPreserve = (Get-Command -Name Remove-AppxPackage).Parameters.ContainsKey('PreserveApplicationData')
        if (-not $supportsPreserve) {
            # Without the switch, removing the development registration also deletes its LocalState, which holds the history.
            Write-Warning 'この環境の Remove-AppxPackage は -PreserveApplicationData に対応していません。開発用の登録を削除すると、保存されている履歴 (LocalState) も消えます。'
        }
        foreach ($package in $development) {
            Write-Output ('開発用の登録を削除しています: {0}' -f $package.PackageFullName)
            if ($supportsPreserve) {
                Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
            }
            else {
                Remove-AppxPackage -Package $package.PackageFullName
            }
        }
    }

    Write-Output 'パッケージを導入しています...'
    Add-AppxPackage -Path $msixPath -ForceApplicationShutdown
    Write-Output '導入が完了しました。'
    Write-Output 'CmdPal で「Reload Command Palette extensions」を実行するか、CmdPal を再起動してください。'
}
catch {
    [Console]::Error.WriteLine(('導入に失敗しました: {0}' -f $_.Exception.Message))
    exit 1
}
