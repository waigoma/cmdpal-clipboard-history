# Helpers shared by build.ps1, package.ps1 and sign-release.ps1. Dot-source this file; it only defines functions.
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Reads the identity and the size of the app exe from an MSIX (a zip). Returns $null when a required part is missing.
function Read-MsixInfo {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExeName = 'ClipboardHistory.Extension.exe'
    )
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $zip.GetEntry('AppxManifest.xml')
        $exeEntry = $zip.GetEntry($ExeName)
        if ($null -eq $manifestEntry -or $null -eq $exeEntry) {
            return $null
        }

        $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        return [pscustomobject]@{
            Version = [string]$manifest.Package.Identity.Version
            Publisher = [string]$manifest.Package.Identity.Publisher
            ExeBytes = [long]$exeEntry.Length
        }
    }
    finally {
        $zip.Dispose()
    }
}

# The SDK tools ship as bin\<version>\x64\signtool.exe. The highest version wins, so a newer SDK is preferred.
function Find-SignTool {
    $nugetRoot = if ([string]::IsNullOrEmpty($env:NUGET_PACKAGES)) { Join-Path $env:USERPROFILE '.nuget\packages' } else { $env:NUGET_PACKAGES }
    $candidates = @(
        (Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')
    )

    foreach ($root in $candidates) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        $tool = Get-ChildItem -LiteralPath $root -Recurse -Filter 'signtool.exe' -File |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' -and $_.Directory.Parent.Name -match '^\d+(\.\d+){1,3}$' } |
            Sort-Object -Property @{ Expression = { [version]$_.Directory.Parent.Name } } -Descending |
            Select-Object -First 1
        if ($null -ne $tool) {
            return $tool.FullName
        }
    }

    return $null
}
