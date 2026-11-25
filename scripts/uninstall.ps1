# Removes the clipboard history package for the current user. Without -Force it only lists what will be lost and exits 1,
# because removing the package deletes the history stored in its LocalState folder. Runs from any current directory.
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$packageName = 'waigoma.CmdPal.ClipboardHistory'

try {
    $packages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
    if ($packages.Count -eq 0) {
        Write-Output '導入されていません。削除するものはありません。'
        exit 0
    }

    if (-not $Force) {
        Write-Output '次のパッケージを削除します。'
        foreach ($package in $packages) {
            Write-Output ('  {0}' -f $package.PackageFullName)
        }
        Write-Output ''
        Write-Output '削除すると、既定の保存先の履歴 (LocalState\data) と設定が失われます。取り出したい内容があれば先に確認してください。'
        Write-Output '保存先を変更していた場合、変更先のフォルダーの履歴は削除されません。'
        Write-Output '削除を実行するには -Force を付けて、もう一度このスクリプトを実行してください。'
        exit 1
    }

    foreach ($package in $packages) {
        Write-Output ('削除しています: {0}' -f $package.PackageFullName)
        Remove-AppxPackage -Package $package.PackageFullName
    }
    Write-Output '削除が完了しました。'
}
catch {
    [Console]::Error.WriteLine(('削除に失敗しました: {0}' -f $_.Exception.Message))
    exit 1
}

# Explicit success code, see set-version.ps1.
exit 0
