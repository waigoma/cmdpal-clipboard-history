# Run once by the owner. Creates the release signing certificate (CN=waigoma, 5 years, exportable), exports it to a
# password-protected PFX (-OutFile, default Documents\waigoma-signing\release.pfx), removes the certificate and its key
# from the current user store, and then hands the base64 of the PFX and its password to the clipboard one at a time
# for the GitHub Environment secrets. Clipboard entries are marked so Windows clipboard history, cloud clipboard and this
# extension do not record them. Nothing secret is printed or written anywhere except the PFX itself.
# -SkipClipboard is for a local trial: the clipboard is not touched and the PFX is deleted again at the end.
# Prints the thumbprint as the last line. Exits non-zero on failure.
param(
    [string]$OutFile,
    [switch]$SkipClipboard
)

$ErrorActionPreference = 'Stop'

$subject = 'CN=waigoma'
$friendlyName = 'waigoma CmdPal extensions (release)'
$store = 'Cert:\CurrentUser\My'
$repoRoot = Split-Path -Parent $PSScriptRoot
$alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$passwordLength = 40

$exitCode = 0
$success = $false
$cert = $null
$password = $null
$pfxWritten = $false
$clipboardSet = $false

# Writes text to the clipboard with formats that tell the clipboard history, cloud sync and this extension not to keep it.
function Set-SecretClipboard {
    param([Parameter(Mandatory)][string]$Text)

    $data = New-Object System.Windows.Forms.DataObject
    $data.SetText($Text, [System.Windows.Forms.TextDataFormat]::UnicodeText)
    $data.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream (, [byte[]]@(0))))
    $data.SetData('CanIncludeInClipboardHistory', (New-Object System.IO.MemoryStream (, [byte[]]@(0, 0, 0, 0))))
    $data.SetData('CanUploadToCloudClipboard', (New-Object System.IO.MemoryStream (, [byte[]]@(0, 0, 0, 0))))

    # The clipboard can be locked by another process for a moment, so the write is retried a few times.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            [System.Windows.Forms.Clipboard]::SetDataObject($data, $false)
            return
        }
        catch [System.Runtime.InteropServices.ExternalException] {
            Start-Sleep -Milliseconds 200
        }
    }
    throw 'クリップボードに書き込めませんでした。'
}

try {
    # The clipboard API needs a single-threaded apartment, so the script starts itself again in STA when needed.
    if (-not $SkipClipboard -and [System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
        $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', $PSCommandPath)
        if ($PSBoundParameters.ContainsKey('OutFile')) {
            $relaunchArgs += @('-OutFile', $OutFile)
        }
        & powershell.exe @relaunchArgs
        exit $LASTEXITCODE
    }

    if ([string]::IsNullOrEmpty($OutFile)) {
        $OutFile = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'waigoma-signing\release.pfx'
    }
    $OutFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)

    # The PFX holds the private key of the release signer, so it must never end up in the repository, and an existing file is not overwritten.
    $repoFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
    if ($OutFile.StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'リポジトリの中には PFX を出力できません。別のフォルダーを指定してください。'
    }
    if (Test-Path -LiteralPath $OutFile) {
        throw '出力先に同名のファイルがあります。上書きしません。別のパスを指定するか、そのファイルを移動してください。'
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null

    Write-Output '証明書を作成しています (有効期間 5 年、秘密鍵はエクスポート可能)...'
    # The code signing EKU is set by hand, matching the development certificate script.
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -CertStoreLocation $store `
        -FriendlyName $friendlyName `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

    # A cryptographic RNG is used; bytes at or above 248 (= 62 * 4) are rejected so every character is equally likely.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $builder = New-Object System.Text.StringBuilder
        $buffer = New-Object byte[] 1
        while ($builder.Length -lt $passwordLength) {
            $rng.GetBytes($buffer)
            if ($buffer[0] -lt 248) {
                [void]$builder.Append($alphabet[$buffer[0] % 62])
            }
        }
        $password = $builder.ToString()
    }
    finally {
        $rng.Dispose()
    }

    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $OutFile -Password $securePassword | Out-Null
    $pfxWritten = $true

    # Once the PFX is written, the store copy is no longer needed; it is removed together with its private key.
    Remove-Item -LiteralPath (Join-Path $store $cert.Thumbprint) -DeleteKey -Confirm:$false -ErrorAction Stop
    if (Test-Path -LiteralPath (Join-Path $store $cert.Thumbprint)) {
        throw '証明書をストアから削除できませんでした。'
    }
    Write-Output ('PFX を出力しました: {0}' -f $OutFile)

    if ($SkipClipboard) {
        Remove-Item -LiteralPath $OutFile -Force
        Write-Output 'テスト用のため、PFX は削除しました。クリップボードは使用していません。'
    }
    else {
        $pfxBytes = [System.IO.File]::ReadAllBytes($OutFile)
        $pfxBase64 = [System.Convert]::ToBase64String($pfxBytes)
        $pfxBytes = $null

        Add-Type -AssemblyName System.Windows.Forms
        Write-Output ''
        Write-Output 'PFX の base64 をクリップボードにコピーしました (履歴には残りません)。'
        Write-Output 'GitHub の SIGNING_PFX_BASE64 に貼り付けたら、このウィンドウで Enter を押してください。'
        Set-SecretClipboard -Text $pfxBase64
        $clipboardSet = $true
        $pfxBase64 = $null
        [void](Read-Host)

        Set-SecretClipboard -Text $password
        Write-Output 'パスワードをクリップボードにコピーしました。SIGNING_PFX_PASSWORD に貼り付けたら Enter を押してください。'
        [void](Read-Host)

        [System.Windows.Forms.Clipboard]::Clear()
        $clipboardSet = $false
        Write-Output 'クリップボードを消去しました。'
    }

    $success = $true
}
catch {
    [Console]::Error.WriteLine(('リリース用の証明書の作成に失敗しました: {0}' -f $_.Exception.Message))
    $exitCode = 1
}
finally {
    $password = $null
    if ($clipboardSet) {
        try {
            [System.Windows.Forms.Clipboard]::Clear()
        }
        catch {
            [Console]::Error.WriteLine('クリップボードを消去できませんでした。手動で消去してください。')
        }
    }

    if ($null -ne $cert -and (Test-Path -LiteralPath (Join-Path $store $cert.Thumbprint))) {
        try {
            Remove-Item -LiteralPath (Join-Path $store $cert.Thumbprint) -DeleteKey -Confirm:$false -ErrorAction Stop
        }
        catch {
            [Console]::Error.WriteLine(('ストアに残った証明書を削除できませんでした。手動で {0} を削除してください。' -f $cert.Thumbprint))
            $exitCode = 1
        }
    }

    # The password exists only in memory, so a PFX whose run did not finish could never be used: it is removed.
    if (-not $success -and $pfxWritten -and (Test-Path -LiteralPath $OutFile)) {
        Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
    }
}

if (-not $success -or $exitCode -ne 0) {
    exit 1
}

Write-Output ''
if (-not $SkipClipboard) {
    Write-Output 'PFX とパスワードは、パスワードマネージャーに保管してください。紛失すると、利用者は新しい証明書を信頼し直す必要があります。'
    Write-Output 'GitHub の Settings > Environments > release に、次の 2 つのシークレットを登録してください: SIGNING_PFX_BASE64, SIGNING_PFX_PASSWORD'
}
Write-Output ('証明書の指紋: {0}' -f $cert.Thumbprint)
