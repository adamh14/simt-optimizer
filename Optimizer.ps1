# Spousteci skript pro Simt Optimizer.
# Zkompiluje Optimizer.cs kompilatorem, ktery je soucasti Windows,
# a spusti okno programu. Nic se predem nebuilduje.

$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'Optimizer.cs'

function Show-Error([string]$text) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            $text, 'Simt Optimizer - chyba',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } catch {
        Write-Host $text -ForegroundColor Red
        Read-Host 'Stisknete Enter'
    }
}

try {
    if (-not (Test-Path -LiteralPath $src)) {
        throw "Vedle tohoto skriptu chybi soubor Optimizer.cs."
    }

    Add-Type -Path $src -ReferencedAssemblies @(
        'System',
        'System.Core',
        'System.Drawing',
        'System.Windows.Forms'
    )

    [SimtOptimizer.Program]::Run($dir)
}
catch {
    $msg = "Program se nepodarilo spustit.`r`n`r`n" + $_.Exception.Message
    if ($_.Exception.InnerException) {
        $msg += "`r`n`r`n" + $_.Exception.InnerException.Message
    }
    $msg += "`r`n`r`nBezny duvod: chybi .NET Framework 4, nebo antivirus zablokoval " +
            "kompilaci za behu. Zkuste program spustit jako spravce, pripadne ho " +
            "docasne povolte v antiviru."
    Show-Error $msg
    exit 1
}
