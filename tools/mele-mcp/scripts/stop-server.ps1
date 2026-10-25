# Stops running mele-mcp servers so a publish can overwrite MeleMcp.dll (Windows locks loaded DLLs).
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*MeleMcp.dll*' } |
    ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force
        "stopped mele-mcp process $($_.ProcessId)"
    }
