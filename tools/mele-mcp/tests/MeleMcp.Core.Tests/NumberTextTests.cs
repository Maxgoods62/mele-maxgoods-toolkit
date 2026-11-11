using System.Globalization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using YamlDotNet.RepresentationModel;

namespace MeleMcp.Core.Tests;

/// <summary>
/// Decimal numbers read from text. float.Parse(text, CultureInfo.InvariantCulture) allows thousands separators, so it read
/// "0,5" as 5: export_set, plot events, merge mod values and lectool's buildsound volume wrote a number 10 times too large
/// with no error, and YAML numbers, game_plot and save_set took NaN and Infinity. NumberText is now the one reader
/// (src/BannedSymbols.txt). The culture tests switch to French themselves, so they mean the same on every machine.
/// </summary>
[TestClass]
public class NumberTextTests
{
    [TestMethod]
    [DataRow("0.5", 0.5)]
    [DataRow("-12", -12d)]
    [DataRow("+3", 3d)]
    [DataRow(".5", 0.5)]
    [DataRow("1e-3", 0.001)]
    [DataRow(" 2.5 ", 2.5)]
    public void PlainNumbers_AreRead(string text, double expected)
    {
        Assert.AreEqual(expected, NumberText.Double(text), 1e-12, text);
        Assert.AreEqual((float)expected, NumberText.Float(text), 1e-6f, text);
        Assert.IsTrue(NumberText.TryDouble(text, out var d), text);
        Assert.IsTrue(NumberText.TryFloat(text, out var f), text);
        Assert.AreEqual(expected, d, 1e-12, text);
        Assert.AreEqual((float)expected, f, 1e-6f, text);
    }

    [TestMethod]
    [DataRow("0,5")]
    [DataRow("1,234.5")]
    [DataRow("1 234")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("1e999")]
    [DataRow("0x10")]
    [DataRow("1.2.3")]
    [DataRow("abc")]
    [DataRow("")]
    [DataRow(null)]
    public void EverythingElse_IsRefused_AndTheErrorNamesTheText(string? text)
    {
        Assert.IsFalse(NumberText.TryFloat(text, out _), text);
        Assert.IsFalse(NumberText.TryDouble(text, out _), text);
        var e = Assert.Throws<FormatException>(() => NumberText.Float(text));
        StringAssert.Contains(e.Message, $"'{text}'");
        Assert.Throws<FormatException>(() => NumberText.Double(text));
    }

    /// <summary>A float that overflows is Infinity for .NET, not an error: 1e39 fits a double and not a float.</summary>
    [TestMethod]
    public void AFloatThatOverflows_IsRefused()
    {
        Assert.IsFalse(NumberText.TryFloat("1e39", out _));
        Assert.IsTrue(NumberText.TryDouble("1e39", out _));
    }

    [TestMethod]
    public void TheMachinesCulture_ChangesNothing()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.AreEqual(0.5f, NumberText.Float("0.5"));
            Assert.IsFalse(NumberText.TryFloat("0,5", out _), "a French decimal comma was read");
            Assert.AreEqual(1234.5, NumberText.Double("1234.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>What the server and lectool call first: after it a number prints with a decimal point on every thread.</summary>
    [TestMethod]
    public void UseInvariantCulture_MakesEveryThreadPrintADecimalPoint()
    {
        var (current, standard) = (CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture);
        try
        {
            var french = new CultureInfo("fr-FR");
            CultureInfo.DefaultThreadCurrentCulture = french;
            CultureInfo.CurrentCulture = french;
            Assert.AreEqual("2,5", $"{2.5}", "the test's own setup: French prints a decimal comma");

            NumberText.UseInvariantCulture();

            Assert.AreEqual("2.5", $"{2.5}");
            var onThread = "";
            var thread = new Thread(() => onThread = $"{2.5}");
            // Without this thread's own culture (it flows to a thread it starts), so the new thread shows the process default.
            using (ExecutionContext.SuppressFlow()) thread.Start();
            thread.Join();
            Assert.AreEqual("2.5", onThread);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = standard;
            CultureInfo.CurrentCulture = current;
        }
    }

    /// <summary>export_set and export_set_batch: PropertyEditor.Assign.</summary>
    [TestMethod]
    public void ExportSet_AFloatWithAComma_IsRefused()
    {
        var scale = new FloatProperty(1f, "DrawScale");
        Assert.Throws<FormatException>(() => PropertyEditor.Assign(null!, scale, "0,5"));
        Assert.AreEqual(1f, scale.Value, "the refused value was stored");
        PropertyEditor.Assign(null!, scale, "0.5");
        Assert.AreEqual(0.5f, scale.Value);
    }

    /// <summary>plot_event_set and plot_build: PlotMaps.Parse.</summary>
    [TestMethod]
    public void PlotElement_AFloatWithAComma_IsRefused()
    {
        Assert.Throws<FormatException>(() => PlotMaps.Parse("float 3 = 0,5"));
        var element = (BioStateEventMap.BioStateEventElementFloat)PlotMaps.Parse("float 3 = 0.5");
        Assert.AreEqual(0.5f, element.NewValue);
    }

    /// <summary>mergemod_build's check of a FloatProperty value: M3 would read the text its own way at install.</summary>
    [TestMethod]
    public void MergeValue_AFloatWithAComma_IsRefused()
    {
        Assert.Throws<FormatException>(() => MergeMods.CheckValue(null!, "FloatProperty", "0,5"));
        MergeMods.CheckValue(null!, "FloatProperty", "0.5");
    }

    /// <summary>Every number of a level, conversation or plot YAML goes through YamlFiles.Number or YamlFiles.Float.</summary>
    [TestMethod]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("1e39")]
    [DataRow("2,5")]
    public void Yaml_OnlyAFiniteNumberIsANumber(string text)
    {
        var e = Assert.Throws<ArgumentException>(() => YamlFiles.Number(new YamlScalarNode(text)));
        StringAssert.Contains(e.Message, "expected a number");
        Assert.Throws<ArgumentException>(() => YamlFiles.Float(new YamlScalarNode(text)));
        Assert.AreEqual(2.5f, YamlFiles.Number(new YamlScalarNode("2.5")));
    }

    /// <summary>game_plot: the value sent to the game.</summary>
    [TestMethod]
    public void GamePlot_NaNAndInfinity_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => LivePlot.Number("float", "NaN"));
        Assert.Throws<ArgumentException>(() => LivePlot.Number("float", "Infinity"));
        Assert.AreEqual("0.5", LivePlot.Number("float", "0.5"));
        Assert.AreEqual("1", LivePlot.Number("bool", "True"));
    }

    /// <summary>save_set: a float field or plot float of an LE2 or LE3 save.</summary>
    [TestMethod]
    public void SaveField_NaNAndInfinity_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => SaveGame.Encode("NaN", isFloat: true));
        Assert.Throws<ArgumentException>(() => SaveGame.Encode("Infinity", isFloat: true));
        CollectionAssert.AreEqual(BitConverter.GetBytes(0.5f), SaveGame.Encode("0.5", isFloat: true));
    }

    /// <summary>
    /// An NPC's light took any radius and brightness, where a light of the lights: section is checked. The file is refused when it
    /// is read, before any level is opened or created.
    /// </summary>
    [TestMethod]
    [DataRow("radius: 0, brightness: 1", "'radius' must be over 0")]
    [DataRow("radius: -5, brightness: 1", "'radius' must be over 0")]
    [DataRow("radius: 100, brightness: -1", "'brightness' can't be negative")]
    [DataRow("radius: NaN, brightness: 1", "expected a number")]
    public void NpcLight_ABadRadiusOrBrightness_IsRefusedWhenTheFileIsRead(string values, string expected)
    {
        LecHost.EnsureInitialized();
        var map = LevelBuildStateTests.MapPath("npc-light-" + Guid.NewGuid().ToString("N")[..6]);
        var yaml = LevelBuildStateTests.Yaml(map, $$"""
            npcs:
              nurse:
                tag: t_nurse
                lights:
                  - { tag: t_lamp, offset: [0, 0, 100], {{values}} }
            """);

        var e = Assert.Throws<ArgumentException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        StringAssert.Contains(e.Message, expected);
        Assert.IsFalse(File.Exists(map), "a refused file created the level");
    }
}
