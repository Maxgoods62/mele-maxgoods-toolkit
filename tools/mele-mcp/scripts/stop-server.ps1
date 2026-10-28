# Stops the mele-mcp servers that run <OutDir>\MeleMcp.dll, as dotnet.exe MeleMcp.dll or as the publish's MeleMcp.exe, so
# a publish can overwrite it (Windows locks loaded DLLs). Servers started from any other folder keep running.
# -ListOnly prints the matches and stops nothing.
#   stop-server.ps1 -OutDir C:\mele-mcp [-ListOnly]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$ListOnly
)

if ($OutDir -notmatch '^([A-Za-z]:\\|\\\\)') { throw "OutDir must be an absolute Windows path (got '$OutDir')" }
$dll = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($OutDir, 'MeleMcp.dll'))
$exe = [System.IO.Path]::ChangeExtension($dll, '.exe')

# True when $path is $want (both absolute; case does not matter).
function Test-SamePath([string]$path, [string]$want) {
    try { $full = [System.IO.Path]::GetFullPath($path) } catch { return $false }
    return [string]::Equals($full, $want, [System.StringComparison]::OrdinalIgnoreCase)
}

# True when one argument of the command line is $dll itself.
function Test-RunsDll([string]$commandLine) {
    foreach ($m in [regex]::Matches($commandLine, '"([^"]*)"|(\S+)')) {
        $arg = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
        if ($arg -notmatch '^([A-Za-z]:[\\/]|\\\\).*MeleMcp\.dll$') { continue }
        if (Test-SamePath $arg $dll) { return $true }
    }
    return $false
}

# dotnet.exe with $dll on its command line, or $exe itself. ExecutablePath is empty for a process we may not inspect.
function Test-IsServer($p) {
    if ($p.Name -ieq 'MeleMcp.exe') { return [bool]$p.ExecutablePath -and (Test-SamePath $p.ExecutablePath $exe) }
    return [bool]$p.CommandLine -and (Test-RunsDll $p.CommandLine)
}

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='MeleMcp.exe'" |
    Where-Object { Test-IsServer $_ } |
    ForEach-Object {
        $p = $_
        # The apphost's command line may hold no path.
        $what = if ($p.Name -ieq 'MeleMcp.exe') { $p.ExecutablePath } else { $p.CommandLine }
        if ($ListOnly) {
            "mele-mcp process $($p.ProcessId): $what"
            return
        }
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            "stopped mele-mcp process $($p.ProcessId): $what"
        } catch {
            "could not stop mele-mcp process $($p.ProcessId): $($_.Exception.Message)"
        }
    }
