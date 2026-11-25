# Sets the package version in src\ClipboardHistory.Extension\Package.appxmanifest to x.y.z.0. Only the Version attribute of
# the Identity element changes; the encoding, the byte order mark, the line endings and everything else are kept as they
# are. Prints the version as the last line. Runs from any current directory and exits non-zero on failure.
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\ClipboardHistory.Extension\Package.appxmanifest'

try {
    # No leading zeros (0 itself is fine), so each tag has exactly one spelling; 0.0.0 is not a release.
    if (-not ($Version -match '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') -or $Version -eq '0.0.0') {
        throw 'バージョンは 0.0.0 以外の x.y.z の形式で指定してください (先頭に 0 は付けません。例: 0.2.0)。'
    }
    $parts = @($Matches[1], $Matches[2], $Matches[3])
    foreach ($part in $parts) {
        if ([long]$part -gt 65535) {
            throw 'バージョンの各部分は 0 から 65535 の範囲にしてください。'
        }
    }
    $packageVersion = '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]

    $bytes = [System.IO.File]::ReadAllBytes($manifestPath)

    # Decode with the encoding the file actually uses, and write it back with the same preamble, so nothing else changes.
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = New-Object System.Text.UTF8Encoding($true)
        $offset = 3
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = New-Object System.Text.UnicodeEncoding($false, $true)
        $offset = 2
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = New-Object System.Text.UnicodeEncoding($true, $true)
        $offset = 2
    }

    $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)

    # The Identity start tag is matched on its own, so the Version attributes of TargetDeviceFamily are never touched.
    $identityPattern = '(<Identity\b[^>]*?\sVersion=")([^"]*)(")'
    $found = [regex]::Matches($text, $identityPattern)
    if ($found.Count -ne 1) {
        throw ('Package.appxmanifest の Identity の Version を一意に特定できませんでした (見つかった数: {0})。' -f $found.Count)
    }
    $newText = [regex]::Replace($text, $identityPattern, ('${1}' + $packageVersion + '${3}'), 1)

    $preamble = $encoding.GetPreamble()
    $body = $encoding.GetBytes($newText)
    $output = New-Object byte[] ($preamble.Length + $body.Length)
    [Array]::Copy($preamble, 0, $output, 0, $preamble.Length)
    [Array]::Copy($body, 0, $output, $preamble.Length, $body.Length)
    [System.IO.File]::WriteAllBytes($manifestPath, $output)
}
catch {
    [Console]::Error.WriteLine(('バージョンの設定に失敗しました: {0}' -f $_.Exception.Message))
    exit 1
}

Write-Output ('パッケージのバージョンを {0} に設定しました。' -f $packageVersion)
Write-Output $Version
