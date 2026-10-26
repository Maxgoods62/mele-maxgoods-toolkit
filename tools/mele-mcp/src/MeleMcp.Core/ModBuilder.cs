using LegendaryExplorerCore.Packages;
using YamlDotNet.RepresentationModel;

namespace MeleMcp.Core;

public sealed record ModBuildResult(string Folder, IReadOnlyList<PlotBuildResult> Plots, IReadOnlyList<LevelBuildResult> Levels, IReadOnlyList<ConvBuildResult> Conversations,
    IReadOnlyList<string> Skipped);

/// <summary>Builds every YAML file of a mod's source folder: plot files (transitions, consequences, quests), levels (npcs), then conversations.</summary>
public static class ModBuilder
{
    private static readonly string[] PlotKeys = ["transitions", "consequences", "quests"];

    public static ModBuildResult Build(MEGame game, string folder)
    {
        var dir = Path.GetFullPath(GamePaths.ToWindowsPath(folder));
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"No folder {dir}.");

        var plots = new List<string>();
        var levels = new List<string>();
        var conversations = new List<string>();
        var skipped = new List<string>();
        var files = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var root = Named(file, () => YamlFiles.Root(File.ReadAllText(file)));
            var keys = root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).ToHashSet();
            if (keys.Contains("conversation")) conversations.Add(file);
            else if (keys.Contains("npcs")) levels.Add(file);
            else if (keys.Overlaps(PlotKeys)) plots.Add(file);
            else skipped.Add($"{Path.GetFileName(file)}: no 'conversation', 'npcs' or {string.Join("/", PlotKeys)}.");
        }

        // Plot files first (one can create the plot package whose transitions the conversations run), then levels (one can
        // create the level a conversation lives in), then conversations.
        var plotResults = plots.Select(f => Named(f, () => PlotBuilder.Build(game, f, null))).ToList();
        var levelResults = levels.Select(f => Named(f, () => LevelBuilder.Build(game, f, null))).ToList();
        var conversationResults = conversations.Select(f => Named(f, () => ConversationBuilder.Build(game, f, null))).ToList();
        return new ModBuildResult(dir, plotResults, levelResults, conversationResults, skipped);
    }

    /// <summary>Errors name the file they come from; files built before it stay saved.</summary>
    private static T Named<T>(string file, Func<T> build)
    {
        try
        {
            return build();
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(file)}: {e.Message}", e);
        }
    }
}
