# Run once by the owner. Creates the release signing certificate (CN=waigoma, 5 years, exportable), exports it to a
# password-protected PFX (-OutFile, default %LOCALAPPDATA%\waigoma-signing\release.pfx, readable by the current user only),
# removes the certificate and its key from the current user store, and then hands the password and the base64 of the PFX
# to the clipboard one at a time for the GitHub Environment secrets. The clipboard entries are marked so that Windows
# clipboard history and cloud clipboard do not keep them; third-party clipboard managers may ignore that marking.
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
$createdFolder = $false
$outFolder = $null
# $clipboardSet means the clipboard may hold a secret. It is set before the first clipboard write, so every path after that
# point (failure, Ctrl+C, a closed window) clears the clipboard in finally.
$clipboardSet = $false
$secretMayBeUsed = $false

# Resolves junctions, symbolic links and 8.3 names to the final path. The path must exist.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class PathResolver {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public static string Resolve(string path) {
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        IntPtr handle = CreateFileW(path, 0, 7, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1)) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try {
            StringBuilder buffer = new StringBuilder(4096);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            string result = buffer.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) {
                return @"\\" + result.Substring(8);
            }
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) {
                return result.Substring(4);
            }
            return result;
        }
        finally {
            CloseHandle(handle);
        }
    }
}
'@

# Returns the real path of a path that may not exist yet: the nearest existing ancestor is resolved, the rest is appended.
function Get-RealPath {
    param([Parameter(Mandatory)][string]$Path)
    $current = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $missing = New-Object System.Collections.Generic.List[string]
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            break
        }
        $missing.Insert(0, [System.IO.Path]::GetFileName($current))
        $current = $parent
    }
    $real = [PathResolver]::Resolve($current)
    foreach ($part in $missing) {
        $real = Join-Path $real $part
    }
    return $real.TrimEnd('\')
}

function Test-IsSameOrUnder {
    param([string]$Child, [string]$Parent)
    if ($Child -ieq $Parent) {
        return $true
    }
    return $Child.StartsWith($Parent.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

# Leaves a file or folder readable by the current user only: no inherited entries, one full control entry. A folder
# passes -Container so that files created inside it inherit the same restriction.
function Set-CurrentUserOnlyAcl {
    param([Parameter(Mandatory)][string]$Path, [switch]$Container)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRule($rule)
    }
    $inheritance = [System.Security.AccessControl.InheritanceFlags]::None
    if ($Container) {
        $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $sid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

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
        $OutFile = Join-Path $env:LOCALAPPDATA 'waigoma-signing\release.pfx'
    }
    $OutFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)

    # The real path (after junctions, links and 8.3 names) decides the checks, so a path that merely looks outside is refused too.
    $realOut = Get-RealPath -Path $OutFile
    $realRepo = Get-RealPath -Path $repoRoot
    if (Test-IsSameOrUnder -Child $realOut -Parent $realRepo) {
        throw 'リポジトリの中には PFX を出力できません。別のフォルダーを指定してください。'
    }
    foreach ($variable in 'OneDrive', 'OneDriveConsumer', 'OneDriveCommercial') {
        $root = [Environment]::GetEnvironmentVariable($variable)
        if (-not [string]::IsNullOrEmpty($root) -and (Test-Path -LiteralPath $root)) {
            if (Test-IsSameOrUnder -Child $realOut -Parent (Get-RealPath -Path $root)) {
                throw 'OneDrive の同期フォルダーには PFX を出力できません。別のフォルダーを指定してください。'
            }
        }
    }
    if (Test-Path -LiteralPath $OutFile) {
        throw '出力先に同名のファイルがあります。上書きしません。別のパスを指定するか、そのファイルを移動してください。'
    }
    # A folder this run creates gets the owner-only ACL at once, so nothing written into it is ever more widely readable.
    $outFolder = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $outFolder)) {
        New-Item -ItemType Directory -Path $outFolder | Out-Null
        $createdFolder = $true
        Set-CurrentUserOnlyAcl -Path $outFolder -Container
    }

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

    # The file exists with the owner-only ACL before the export writes anything into it, so its rights are never wider.
    New-Item -ItemType File -Path $OutFile | Out-Null
    $pfxWritten = $true
    Set-CurrentUserOnlyAcl -Path $OutFile

    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $OutFile -Password $securePassword -CryptoAlgorithmOption AES256_SHA256 -Force | Out-Null

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
        Add-Type -AssemblyName System.Windows.Forms
        $pfxBase64 = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($OutFile))

        # The password comes first: it exists only in memory, so it must be stored before anything else happens.
        # The flags are set before the first write, so a write that fails halfway is still cleared in finally.
        $secretMayBeUsed = $true
        $clipboardSet = $true
        $confirmed = $false
        while (-not $confirmed) {
            Set-SecretClipboard -Text $password
            Write-Output 'パスワードをクリップボードにコピーしました。'
            Write-Output 'パスワードマネージャーに今すぐ保存し、GitHub の SIGNING_PFX_PASSWORD にも貼り付けてから Enter を押してください。'
            [void](Read-Host)

            Set-SecretClipboard -Text $pfxBase64
            Write-Output 'PFX の base64 をクリップボードにコピーしました (履歴には残りません)。'
            Write-Output 'GitHub の SIGNING_PFX_BASE64 に貼り付けてから Enter を押してください。'
            [void](Read-Host)

            $answer = Read-Host 'パスワードマネージャーのエントリーと両方のシークレットを保存しましたか。保存済みなら y を入力してください'
            if ($answer -ceq 'y') {
                $confirmed = $true
            }
            else {
                Write-Output 'まだ保存が確認できないため、コピーを繰り返します。'
            }
        }

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
    if (-not $success -and $createdFolder -and (Test-Path -LiteralPath $outFolder) -and @(Get-ChildItem -LiteralPath $outFolder -Force).Count -eq 0) {
        Remove-Item -LiteralPath $outFolder -Force -ErrorAction SilentlyContinue
    }
    if (-not $success -and $secretMayBeUsed) {
        [Console]::Error.WriteLine('PFX を削除しました。GitHub のシークレットを新しい値に置き換える必要があるため、このスクリプトを再実行してください。')
    }
}

if (-not $success -or $exitCode -ne 0) {
    exit 1
}

Write-Output ''
if (-not $SkipClipboard) {
    Write-Output 'PFX とパスワードは、パスワードマネージャーに保管してください。紛失すると、利用者は新しい証明書を信頼し直す必要があります。'
    Write-Output 'GitHub の Settings > Environments > release に、次の 2 つのシークレットを登録してください: SIGNING_PFX_BASE64, SIGNING_PFX_PASSWORD'
    Write-Output 'ドキュメントの「リリース用証明書の拇印」欄に、下の拇印を記入してください。'
}
Write-Output ('証明書の拇印: {0}' -f $cert.Thumbprint)

# Explicit success code, see set-version.ps1.
exit 0
