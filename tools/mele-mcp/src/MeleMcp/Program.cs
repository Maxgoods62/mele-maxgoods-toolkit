using System.Text.Json;
using LegendaryExplorerCore.Packages;
using MeleMcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// First: numbers print the same on every Windows language (a French one writes 2.5 as "2,5"), in this code and in LEC's.
NumberText.UseInvariantCulture();

// `MeleMcp --selftest [package]` checks the LEC stack without an MCP client; everything else runs the stdio server.
if (args.Length > 0 && args[0] == "--selftest")
{
    var json = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(LecHost.EnsureInitialized(), json));
    var package = args.Length > 1 ? args[1] : "BIOA_CRD00.pcc";
    Console.WriteLine(JsonSerializer.Serialize(PackageInspector.Summarize(MEGame.LE1, package), json));
    return;
}

// Empty builder: the default one watches appsettings.json in the working directory, and Claude Code starts us with a
// \\wsl.localhost\ working directory where that file watcher hangs startup.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout is reserved for JSON-RPC
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        MeleMcp.Tools.RequiredGame.Missing(context.Params) ?? await next(context, ct)))
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
