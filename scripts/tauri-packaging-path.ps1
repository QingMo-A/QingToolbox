# Windows PowerShell 5.1/.NET Framework does not expose
# [System.IO.Path]::GetRelativePath.  Use Uri's well-tested path relation so
# the Tauri packaging scripts work from both powershell.exe and pwsh.exe.
function Get-TauriRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $pathFull = [IO.Path]::GetFullPath($Path)
    $rootUri = [Uri]::new($rootFull + [IO.Path]::DirectorySeparatorChar)
    $pathUri = [Uri]::new($pathFull)
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}
