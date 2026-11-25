# Installs the signed clipboard history package for the current user. Read-only checks come first: the script does not
# import certificates, toggle Developer Mode or change any store. When the signing certificate is not trusted it prints
# the exact elevated command for the user to run and exits 1. Runs from any current directory.
# Release download: when a ClipboardHistory_*.msix lies next to this script, that folder supplies the package and
# waigoma.cer, and no path in the repository is computed. Otherwise the repository's artifacts folder is used.
# Among several packages, the one with the highest version (from its file name) is chosen.
param(
    [string]$Msix,
    [switch]$RemoveDevelopment
)

$ErrorActionPreference = 'Stop'

$packageName = 'waigoma.CmdPal.ClipboardHistory'

# Returns the package in the folder with the highest version parsed from its name, or $null when there is none.
function Select-ClipboardPackage {
    param([Parameter(Mandatory)][string]$Folder)
    $candidates = foreach ($file in @(Get-ChildItem -LiteralPath $Folder -Filter 'ClipboardHistory_*_x64.msix' -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -match '^ClipboardHistory_([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)_x64\.msix$') {
            [pscustomobject]@{ File = $file; Version = [version]$Matches[1] }
        }
    }
    $best = @($candidates) | Sort-Object -Property Version -Descending | Select-Object -First 1
    if ($null -eq $best) {
        return $null
    }
    return $best.File
}

try {
    $releasePackage = Select-ClipboardPackage -Folder $PSScriptRoot
    $releaseMode = $null -ne $releasePackage
    if ($releaseMode) {
        $baseDir = $PSScriptRoot
    }
    else {
        # A script at the top of a drive has no parent folder, so there is no repository to read from.
        $repoRoot = Split-Path -Parent $PSScriptRoot
        if ([string]::IsNullOrEmpty($repoRoot)) {
            throw 'ドライブのルートに置かれたスクリプトからは、リポジトリの artifacts を読めません。リポジトリ内の scripts フォルダーから実行してください。'
        }
        $baseDir = Join-Path $repoRoot 'artifacts'
    }
    $certPath = Join-Path $baseDir 'waigoma.cer'

    if ([string]::IsNullOrEmpty($Msix)) {
        $selected = Select-ClipboardPackage -Folder $baseDir
        if ($null -eq $selected) {
            if ($releaseMode) {
                throw '.msix が見つかりません。配布物を取得し直してください。'
            }
            throw 'artifacts に .msix がありません。先に scripts\package.ps1 を実行してください。'
        }
        $Msix = $selected.FullName
    }

    $msixPath = (Resolve-Path -LiteralPath $Msix).ProviderPath
    Write-Output ('対象: {0}' -f $msixPath)

    $signature = Get-AuthenticodeSignature -LiteralPath $msixPath
    if ($null -eq $signature.SignerCertificate) {
        if ($releaseMode) {
            throw '.msix が署名されていません。配布物を取得し直してください。'
        }
        throw '.msix が署名されていません。scripts\package.ps1 で作り直してください。'
    }
    $thumbprint = $signature.SignerCertificate.Thumbprint

    # The .cer next to the package is the one the user imports, so it must be the certificate that signed this package.
    $cerThumbprint = $null
    if (Test-Path -LiteralPath $certPath) {
        $cerThumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certPath).Thumbprint
    }
    if ($cerThumbprint -ne $thumbprint) {
        if ($releaseMode) {
            throw 'waigoma.cer が .msix の署名証明書と一致しません。配布物を取得し直してください。'
        }
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

# Explicit success code, see set-version.ps1.
exit 0
