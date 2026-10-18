[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ConfigPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$word = $null
$document = $null
$oldUpdateLinks = $null
$owned = $false
$code = 'wordConversionFailed'
$message = 'Word could not export this document.'
$stage = 'readConfig'
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.jobName -notmatch '^Local\\QingConvert-[0-9]+-[0-9a-f]+-[0-9]+$') { throw 'Invalid job identity.' }
    if (-not (Test-Path -LiteralPath $config.inputPath -PathType Leaf) -or (Test-Path -LiteralPath $config.outputPath)) { throw 'Invalid conversion paths.' }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QingWordProcess {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
    $existing = @(Get-Process -Name WINWORD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    if ($existing.Count -gt 0) {
        $code = 'wordAlreadyRunning'
        throw 'Use LibreOffice or finish the existing Word session first.'
    }
    $stage = 'createWord'
    $word = New-Object -ComObject Word.Application
    # Word.Application has no portable Hwnd property. With no pre-existing
    # Word process, require exactly one new server before touching the app.
    $created = @(Get-Process -Name WINWORD -ErrorAction SilentlyContinue | Where-Object {$existing -notcontains $_.Id})
    if ($created.Count -ne 1) {
        $code = 'wordAlreadyRunning'
        $message = 'Word did not create an isolated conversion instance.'
        throw 'Refusing to change an existing Word instance.'
    }
    $wordPid = $created[0].Id
    $owned = $true
    $stage = 'isolateWord'
    $job = [QingWordProcess]::OpenJobObject(1, $false, $config.jobName)
    if ($job -eq [IntPtr]::Zero) { throw 'Conversion job unavailable.' }
    try {
        $process = Get-Process -Id $wordPid
        if (-not [QingWordProcess]::AssignProcessToJobObject($job, $process.Handle)) { throw 'Cannot isolate the Word conversion instance.' }
    } finally { [void][QingWordProcess]::CloseHandle($job) }
    $stage = 'configureWord'
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $word.AutomationSecurity = 3 # msoAutomationSecurityForceDisable
    $oldUpdateLinks = $word.Options.UpdateLinksAtOpen
    $word.Options.UpdateLinksAtOpen = $false
    $missing = [Type]::Missing
    # Never add source files to recent documents, enable macros or repair/save
    # the original document. Conversion only writes the requested temp output.
    $stage = 'openDocument'
    # Supplying Type.Missing through every COM by-ref slot fails with E_POINTER
    # on some Office builds. Omit the optional slots rather than passing null
    # pointers; format detection is native for DOC/DOCX (no encoding dialog).
    $documents = $word.Documents
    $document = $documents.OpenNoRepairDialog($config.inputPath, $false, $true, $false)
    $stage = 'exportPdf'
    $document.ExportAsFixedFormat($config.outputPath, 17, $false)
    if (-not (Test-Path -LiteralPath $config.outputPath -PathType Leaf)) { throw 'PDF output missing.' }
    [Console]::WriteLine('{"ok":true}')
} catch {
    [Console]::WriteLine((@{code=$code;message=$message;stage=$stage;hresult=$_.Exception.HResult} | ConvertTo-Json -Compress))
    exit 1
} finally {
    if ($null -ne $document) {
        try { $document.Close(0) } catch {}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document)
    }
    if ($null -ne $word) {
        if ($owned) {
            if ($null -ne $oldUpdateLinks) { try { $word.Options.UpdateLinksAtOpen = $oldUpdateLinks } catch {} }
            try { $word.Quit(0) } catch {}
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
