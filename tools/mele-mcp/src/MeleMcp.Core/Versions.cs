using System.Reflection;

namespace MeleMcp.Core;

/// <summary>What mods and MeleBridge are checked against: this server's version and the pipe protocol it speaks.</summary>
public static class Versions
{
    /// <summary>
    /// This server's version, the toolkit's one semver: the Version of MeleMcp.csproj, read from the running server (the entry
    /// assembly). A mod YAML's 'requires: mele-mcp &gt;= X.Y.Z' is compared with it.
    /// </summary>
    public static readonly Version Server = ThreeParts(Assembly.GetEntryAssembly()?.GetName().Version);

    /// <summary>The MeleBridge pipe protocol this server speaks: the bridge's MELE_PROTOCOL_VERSION (tools/mele-bridge/src/SharedVersion.h).</summary>
    public const int BridgeProtocol = 1;

    /// <summary>0.1.0 for 0.1, 0.1.0 and 0.1.0.0, so versions compare and print the same.</summary>
    internal static Version ThreeParts(Version? version) =>
        version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
}
