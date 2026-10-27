using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

/// <summary>
/// How a thumbnail paints an element: its texture times Multiply, then moved toward Override by OverrideAmount (0: not at all).
/// MaskColour is set when the material paints regions with colours of its own: a texture that turns out to be a channel mask
/// is then drawn as its intensity times that colour. Cut is set for a masked material whose opacity mask could be traced.
/// Colours are linear, as the material stores them.
/// </summary>
internal sealed record MaterialLook(ExportEntry? Texture, Vector3 Multiply, Vector3 Override, float OverrideAmount, Vector3? MaskColour, Cutout? Cut, string How);

/// <summary>A masked material's opacity mask: a pixel whose Texture channel (its alpha, else its red) is under Clip is not drawn.</summary>
internal sealed record Cutout(ExportEntry Texture, bool Alpha, float Clip);

/// <summary>
/// What a material looks like, guessed from its data for an offline thumbnail. LE1 cooked Materials keep no DiffuseColor graph
/// (Expression is 0) and LE material resources carry no named uniform expressions (they live in the shader map), so the diffuse
/// texture is picked by scoring every candidate texture by parameter and texture name, and the colour parameters are read by
/// the role their names and defaults give them (see <see cref="Colours"/>).
/// </summary>
internal static partial class MaterialDiffuse
{
    private static readonly string[] BadName = ["norm", "spec", "mask", "emis", "glow", "opac", "rough", "cube", "env", "refl", "noise", "lightmap", "alpha", "bump", "height", "disto"];

    /// <summary>Blend modes a prop thumbnail can't show as a surface: glass, glows, shadows, effects.</summary>
    private static readonly string[] SeeThrough = ["BLEND_Translucent", "BLEND_Additive", "BLEND_Modulate", "BLEND_AlphaComposite"];

    /// <summary>Material.OpacityMaskClipValue's default; LE1's masked materials keep it (none in the catalog sets another).</summary>
    private const float DefaultClip = 0.3333f;

    [GeneratedRegex(@"disto|refract|heat_?haze|_heat(_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Distortion();

    [GeneratedRegex("(_mat|_mic|_inst|_mi|_ma|_m)$")]
    private static partial Regex MaterialSuffix();

    public static ExportEntry? Resolve(IEntry? entry, PackageCache cache) =>
        entry switch { ExportEntry e => e, ImportEntry i => EntryImporter.ResolveImport(i, cache), _ => null };

    /// <summary>
    /// The material's look, or null with the reason when a thumbnail should not draw it: a see-through blend mode (glass, glow,
    /// decal) or a distortion effect (heat haze).
    /// </summary>
    public static (MaterialLook? Look, string? Skip) Read(ExportEntry material, PackageCache cache)
    {
        var chain = Chain(material, cache);
        var top = chain[^1];
        var blend = top.ClassName == "Material" ? top.GetProperty<EnumProperty>("BlendMode")?.Value.Instanced ?? "BLEND_Opaque" : null;
        if (blend is not null && SeeThrough.Contains(blend)) return (null, $"{material.ObjectName.Instanced}: {blend}");
        if (chain.FirstOrDefault(m => Distortion().IsMatch(m.ObjectName.Name)) is { } distortion) return (null, $"{distortion.ObjectName.Instanced}: a distortion effect");

        var (multiply, overrideColour, amount, maskColour, colours) = Colours(chain);
        var (texture, how) = Pick(chain, cache);
        var (cut, cutHow) = blend == "BLEND_Masked" ? OpacityMask(chain, texture, cache) : (null, "");
        return (new MaterialLook(texture, multiply, overrideColour, amount, maskColour, cut, $"{material.ObjectName.Instanced}: {how}{colours}{cutHow}"), null);
    }

    /// <summary>
    /// Where a masked material's OpacityMask input reads from (checked on the catalog's foliage, grates and APL props):
    /// - a texture sample expression (the APL masters' 'OpacityMask' parameter, whose default EngineResources.DecoPaint is white):
    ///   the chain's texture for that parameter (child first), else the expression's own texture;
    /// - an expression cooked away (0) whose channel mask is the alpha alone (the foliage cards): the diffuse texture's alpha;
    /// - no input: nothing is cut. Anything else (a whole output of a graph that is gone) is not guessed: drawn solid.
    /// The channel is the alpha when the input's mask picks it, else red (a scalar input takes the first channel).
    /// </summary>
    private static (Cutout? Cut, string How) OpacityMask(List<ExportEntry> chain, ExportEntry? diffuse, PackageCache cache)
    {
        var top = chain[^1];
        if (top.GetProperty<StructProperty>("OpacityMask") is not { } input) return (null, "");
        var clip = top.GetProperty<FloatProperty>("OpacityMaskClipValue")?.Value ?? DefaultClip;
        bool On(string mask) => input.GetProp<IntProperty>(mask)?.Value is > 0;
        var alpha = On("Mask") && On("MaskA") && !On("MaskR");
        var expression = input.GetProp<ObjectProperty>("Expression")?.Value ?? 0;
        if (expression == 0)
        {
            return alpha && diffuse is not null
                ? (new Cutout(diffuse, true, clip), $", cut where {diffuse.ObjectName.Instanced}'s alpha is under {clip:F2}")
                : (null, ", masked by a graph cooked away: drawn solid");
        }

        var sample = top.FileRef.TryGetUExport(expression, out var x) ? x : null;
        var name = sample?.GetProperty<NameProperty>("ParameterName")?.Value.Instanced;
        IEntry? entry = null;
        foreach (var m in chain.Where(m => name is not null && m.IsA("MaterialInstance")))
        {
            entry ??= (m.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")?.AsEnumerable() ?? [])
                .Where(p => string.Equals(p.GetProp<NameProperty>("ParameterName")?.Value.Instanced, name, StringComparison.OrdinalIgnoreCase))
                .Select(p => m.FileRef.GetEntry(p.GetProp<ObjectProperty>("ParameterValue")?.Value ?? 0))
                .FirstOrDefault(e => e is not null);
        }
        entry ??= top.FileRef.GetEntry(sample?.GetProperty<ObjectProperty>("Texture")?.Value ?? 0);
        if (Resolve(entry, cache) is not { ClassName: "Texture2D" } texture) return (null, $", masked by {sample?.ClassName ?? "an expression"} with no texture: drawn solid");
        return (new Cutout(texture, alpha, clip), $", cut where {texture.ObjectName.Instanced}'s {(alpha ? "alpha" : "red")} is under {clip:F2}");
    }

    /// <summary>The material and its parents, child first, ending with the base Material (or what could not be followed).</summary>
    private static List<ExportEntry> Chain(ExportEntry material, PackageCache cache)
    {
        var chain = new List<ExportEntry>();
        for (var current = material; current is not null && chain.Count < 16; )
        {
            chain.Add(current);
            if (!current.IsA("MaterialInstance")) break;
            var parent = current.GetProperty<ObjectProperty>("Parent");
            current = parent is null || parent.Value == 0 ? null : Resolve(current.FileRef.GetEntry(parent.Value), cache);
        }
        return chain;
    }

    /// <summary>
    /// The colours the material puts on its diffuse, by the role BioWare's materials give them (checked against the game on the
    /// cargo container, the XLarge crates, pallets and chairs):
    /// - override: a scalar named *Override*Opacity (or Amount, Alpha, Blend) moves the diffuse toward a colour named *Override*
    ///   or *Diff*Colo(u)r by that much: the cargo container's master sets 1 (flat brown in game), the crates leave it at 0;
    /// - multiplier: a colour named *Mult* or *Tint*, or one whose base-material default is white, multiplies the texture;
    /// - any other colour paints a region a mask texture picks (TMissionColour with its TMission mask, *Stripe*) and is not drawn;
    ///   with such colours, MaskColour is the multiplier, else the first region colour (a colour name first, *Body*, *Base* or *Main* first).
    /// Child values win over parents', then the base material's defaults. Specular, emissive, glow, rim and reflection colours never count.
    /// </summary>
    private static (Vector3 Multiply, Vector3 Override, float Amount, Vector3? MaskColour, string How) Colours(List<ExportEntry> chain)
    {
        var vectors = new List<(string Name, Vector3 Value)>();
        var defaults = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var scalars = new List<(string Name, float Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in chain)
        {
            if (m.IsA("MaterialInstance"))
            {
                foreach (var p in m.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")?.AsEnumerable() ?? [])
                {
                    var n = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? "";
                    if (p.GetProp<StructProperty>("ParameterValue") is { } c && seen.Add("v:" + n)) vectors.Add((n, Color(c)));
                }
                foreach (var p in m.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")?.AsEnumerable() ?? [])
                {
                    var n = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? "";
                    if (seen.Add("s:" + n)) scalars.Add((n, p.GetProp<FloatProperty>("ParameterValue")?.Value ?? 0));
                }
            }
            else if (m.ClassName == "Material")
            {
                foreach (var e in m.GetProperty<ArrayProperty<ObjectProperty>>("Expressions")?.AsEnumerable() ?? [])
                {
                    if (e.Value <= 0 || m.FileRef.GetUExport(e.Value) is not { } expression) continue;
                    var props = expression.GetProperties();
                    var n = props.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? "";
                    if (expression.ClassName == "MaterialExpressionVectorParameter")
                    {
                        // An unset default is the struct's zero.
                        var value = props.GetProp<StructProperty>("DefaultValue") is { } c ? Color(c) : Vector3.Zero;
                        defaults.TryAdd(n, value);
                        if (seen.Add("v:" + n)) vectors.Add((n, value));
                    }
                    else if (expression.ClassName == "MaterialExpressionScalarParameter" && seen.Add("s:" + n))
                    {
                        scalars.Add((n, props.GetProp<FloatProperty>("DefaultValue")?.Value ?? 0));
                    }
                }
            }
        }

        var colours = vectors.Where(v => IsColour(v.Name)).OrderByDescending(v => Has(v.Name, "diff")).ToList();
        var multipliers = colours.Where(v => Has(v.Name, "mult") || Has(v.Name, "tint") || defaults.TryGetValue(v.Name, out var d) && d.X >= 0.99f && d.Y >= 0.99f && d.Z >= 0.99f).ToList();
        var how = new StringBuilder();
        var multiply = Vector3.One;
        if (multipliers.Count > 0)
        {
            multiply = multipliers[0].Value;
            how.Append($", times {multipliers[0].Name} ({multiply.X:F2}, {multiply.Y:F2}, {multiply.Z:F2})");
        }
        var opacity = scalars.FirstOrDefault(s => Has(s.Name, "override") && (Has(s.Name, "opac") || Has(s.Name, "amount") || Has(s.Name, "alpha") || Has(s.Name, "blend")));
        var target = colours.Except(multipliers).Where(v => Has(v.Name, "override") || Has(v.Name, "diff")).OrderByDescending(v => Has(v.Name, "override")).FirstOrDefault();
        var amount = 0f;
        if (opacity.Name is not null && target.Name is not null) amount = Math.Clamp(opacity.Value, 0, 1);
        if (amount > 0) how.Append($", {target.Name} ({target.Value.X:F2}, {target.Value.Y:F2}, {target.Value.Z:F2}) at {opacity.Name} {amount:F2}");
        // Any other vector may paint a region (Chair_Stripe_Vector has no colour word); only a mask-like texture makes it count.
        var regions = vectors.Where(v => v.Name.Length > 0 && !Excluded(v.Name) && !multipliers.Contains(v) && (amount == 0 || v != target))
            .OrderByDescending(v => IsColour(v.Name)).ThenByDescending(v => Has(v.Name, "body") || Has(v.Name, "base") || Has(v.Name, "main")).ToList();
        Vector3? maskColour = null;
        if (regions.Count > 0)
        {
            how.Append($", regions: {string.Join(", ", regions.Select(v => v.Name))}");
            maskColour = multipliers.Count > 0 ? multiply : regions[0].Value;
        }
        return (multiply, target.Value, amount, maskColour, how.ToString());
    }

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>A colour the diffuse may take: diff, colo(u)r, tint or mult in the name, and none of the other channels'.</summary>
    private static bool IsColour(string name) =>
        (Has(name, "diff") || Has(name, "color") || Has(name, "colour") || Has(name, "tint") || Has(name, "mult")) && !Excluded(name);

    /// <summary>A vector of another channel: specular, emissive (also misspelled), glow, rim light, reflection, fresnel.</summary>
    private static bool Excluded(string name) => new[] { "spec", "emis", "emmis", "glow", "rim", "refl", "fresnel" }.Any(bad => Has(name, bad));

    private static Vector3 Color(StructProperty c) =>
        new(c.GetProp<FloatProperty>("R")?.Value ?? 0, c.GetProp<FloatProperty>("G")?.Value ?? 0, c.GetProp<FloatProperty>("B")?.Value ?? 0);

    /// <summary>
    /// The best-scoring Texture2D of the chain's texture parameters (child first), the base Material's expressions and its
    /// uniform expression textures; null when the material has none.
    /// </summary>
    private static (ExportEntry? Texture, string How) Pick(List<ExportEntry> chain, PackageCache cache)
    {
        var candidates = new List<(string Param, IEntry Texture, string From)>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = chain.Select(m => MaterialSuffix().Replace(m.ObjectName.Name.ToLowerInvariant(), "")).ToList();
        foreach (var m in chain)
        {
            if (m.IsA("MaterialInstance"))
            {
                foreach (var p in m.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")?.AsEnumerable() ?? [])
                {
                    var name = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? "";
                    var value = m.FileRef.GetEntry(p.GetProp<ObjectProperty>("ParameterValue")?.Value ?? 0);
                    if (value is not null && named.Add(name)) candidates.Add((name, value, "MIC"));
                }
            }
            else if (m.ClassName == "Material")
            {
                foreach (var e in m.GetProperty<ArrayProperty<ObjectProperty>>("Expressions")?.AsEnumerable() ?? [])
                {
                    var props = e.Value > 0 ? m.FileRef.GetUExport(e.Value).GetProperties() : null;
                    var name = props?.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                    var texture = m.FileRef.GetEntry(props?.GetProp<ObjectProperty>("Texture")?.Value ?? 0);
                    if (texture is not null && (name is null || named.Add(name))) candidates.Add((name ?? "", texture, "expression"));
                }
                try
                {
                    foreach (int u in ObjectBinary.From<Material>(m).SM3MaterialResource.UniformExpressionTextures)
                    {
                        if (m.FileRef.GetEntry(u) is { } t && !candidates.Any(c => c.Texture.InstancedFullPath == t.InstancedFullPath)) candidates.Add(("", t, "uniform"));
                    }
                }
                catch (Exception)
                {
                    // A material whose binary LEC can't read still has its parameters above.
                }
            }
        }

        ExportEntry? best = null;
        var bestScore = int.MinValue;
        var how = $"no texture among {candidates.Count} candidates";
        foreach (var (param, entry, from) in candidates)
        {
            var texture = Resolve(entry, cache);
            if (texture is not { ClassName: "Texture2D" }) continue;
            var score = Score(param, texture, stems);
            if (score <= bestScore) continue;
            (best, bestScore) = (texture, score);
            how = $"{texture.ObjectName.Instanced} ({from} '{param}', score {score})";
        }
        return (best, how);
    }

    /// <summary>How much a texture looks like the diffuse: by parameter name, texture name, the material's own name, and format.</summary>
    private static int Score(string param, ExportEntry texture, List<string> stems)
    {
        string p = param.ToLowerInvariant(), t = texture.ObjectName.Instanced.ToLowerInvariant();
        var props = texture.GetProperties();
        var lod = props.GetProp<EnumProperty>("LODGroup")?.Value.Instanced ?? "";
        var compression = props.GetProp<EnumProperty>("CompressionSettings")?.Value.Instanced ?? "";
        var format = props.GetProp<EnumProperty>("Format")?.Value.Instanced ?? "";
        var s = 0;
        if (p.Contains("diff")) s += 20;
        if (p == "texture" || p.Contains("color") || p.Contains("albedo")) s += 6;
        if (t.Contains("diff")) s += 12;
        if (t.EndsWith("_d") || t.Contains("_d_") || t.EndsWith("_dif")) s += 6;
        foreach (var bad in BadName)
        {
            if (p.Contains(bad)) s -= 15;
            if (t.Contains(bad)) s -= 10;
        }
        if (t.EndsWith("_n") || t.Contains("_n_") || t.EndsWith("_s") || t.EndsWith("_m") || t.EndsWith("_e")) s -= 8;
        // Detail textures are tiled greys over the real diffuse (the freighter's trims picked FRE10_Detail01_DIFF without this).
        if (p.Contains("detail") || t.Contains("detail")) s -= 12;
        if (stems.Any(m => m.Length >= 5 && t.Contains(m))) s += 8;
        if (compression.Contains("Normal") || lod.Contains("Normal") || format is "PF_BC5" or "PF_ATI2") s -= 40;
        if (lod.Contains("Specular")) s -= 20;
        if (lod.Contains("Diffuse") || lod is "TEXTUREGROUP_World" or "TEXTUREGROUP_Character" or "TEXTUREGROUP_Vehicle" or "TEXTUREGROUP_Weapon") s += 4;
        if (compression.Contains("Grayscale") || compression.Contains("Displacement") || format == "PF_G8") s -= 10;
        return s;
    }
}
