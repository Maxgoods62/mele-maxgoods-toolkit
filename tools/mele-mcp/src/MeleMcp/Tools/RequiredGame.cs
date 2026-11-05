using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>
/// Every tool with a game parameter requires it. The SDK binds the arguments before <see cref="ToolErrors.Run"/>, so a
/// call without game would only get its generic "An error occurred invoking" text. This check answers that call with
/// what is missing, before the tool runs.
/// </summary>
internal static class RequiredGame
{
    /// <summary>Tool name to the Description of its game parameter, for each tool that takes game.</summary>
    private static readonly Dictionary<string, string> Tools = typeof(RequiredGame).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Select(m => (m.GetCustomAttribute<McpServerToolAttribute>()?.Name, Game: m.GetParameters().FirstOrDefault(p => p.Name == "game")))
        .Where(x => x.Name is not null && x.Game is not null)
        .ToDictionary(x => x.Name!, x => x.Game!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "Game: LE1, LE2 or LE3.");

    /// <summary>An error result when the call names a tool that takes game and gives no game; null otherwise.</summary>
    public static CallToolResult? Missing(CallToolRequestParams? call)
    {
        if (call?.Name is not { } name || !Tools.TryGetValue(name, out var description))
            return null;
        if (call.Arguments is { } args && args.TryGetValue("game", out var game)
            && game.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(game.GetString()))
            return null;
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{name} requires game, and no tool picks one for you. {description}" }],
        };
    }
}
