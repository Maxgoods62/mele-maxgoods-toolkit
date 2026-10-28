using System.Text.RegularExpressions;

namespace MeleMcp.Core;

/// <summary>
/// Which StaticMeshes the in-game editor's prop menu lists by default, and under which category (docs/design/in-game-editor.md §3.5). Name rules,
/// matched without case and in order; the 2026-10-27 catalog study tuned them on every unique LE1 mesh (5,226).
/// </summary>
public static class CatalogRules
{
    /// <summary>Menu order. A mesh no rule names is 'Other' in the APL library and left out elsewhere.</summary>
    public static readonly string[] Categories = ["Crates", "Furniture", "Medical", "Consoles", "Lights", "Industrial", "Debris", "Alien", "Small items", "Signs", "Plants", "Other"];

    /// <summary>What a rule made of a mesh. apl, apl-large and levelprop are the default list.</summary>
    public static readonly string[] Curations = ["apl", "apl-large", "levelprop", "unknown", "arch-size", "arch-flat", "arch-name", "excluded-helper", "excluded-pkg"];

    /// <summary>Bigger than this (2 × the largest bounds extent) is architecture outside the APL library, and gets the 'large' badge in it.</summary>
    public const float LargeSize = 600;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Top-level packages that hold no props: effects, UI, skies, creature parts, engine and editor meshes, cinematics.</summary>
    private static readonly Regex ExcludedPackage = new(@"^(biog_v_|vfx_|enginemeshes|editormeshes|nodebuddies|biog__skies__|biog__suns__|gui_|bioa_galaxymap|bioa_gxm|bioengineresources|biobaseresources|biog_cbt_|biog_hm[fm]_|biog_(asa|kro|tur|qrn)_|pro[a-z]+$|bioa_.*(flyby|arrival|departure|_cin|opening|sovereign|outrun|nebulae|_seq))", Options);

    /// <summary>Invisible helpers, collision, holograms, light shafts, cables.</summary>
    private static readonly Regex Helper = new(@"invisible|cinematic_helper|_dup$|^ice_\d|collision|blocker|hologram|holo$|_heat$|^ring$|^path$|^planet$|lightshaft|lookat|conduit|cable|scatter|^object\d|staticmesh$|^box\d", Options);

    /// <summary>The first that matches names the category; the order settles overlaps (a medical lamp is Medical, a lamp crate Lights).</summary>
    private static readonly (string Category, Regex Rule)[] CategoryRules =
    [
        ("Medical", new(@"medical|first_?aid|lab_?equip|labtable|stretcher|medbay|mobile_med|meddiag|medbed|medbox|med_stuff|syringe|health", Options)),
        ("Lights", new(@"(?<!c)lamp|light(?!ning|shaft)|lantern|bulb|spotlite|lite\d|torch", Options)),
        ("Consoles", new(@"console|consol|computer|comp(?:con|uter)?\d|terminal|monitor|screen|keyb(oard)?|mainframe|holo|bank_machine|server|datapad|vi_[a-z]+|tablescreen|kiosk|display|interface", Options)),
        ("Crates", new(@"crate|container(?!connector)|locker|foot_?locker|ammo_?case|canister|barrel|pallet|safe(?!ty)|chest|(?<!plant)(?<!planter)(?<!sloppy)(?<!plain)(?<!long)(?<!look_at_)(?<!lookat_)box(?!light|slope)|(?<![a-z])bin(?![a-z])|recycling|cargo_?pod|case_\d|storage", Options)),
        ("Furniture", new(@"chair|couch|sofa|seat|bench|table|desk|(?<![a-z])beds?(?![a-z])|medbed|bunk|gambling|cot(?!ton)|shelf|shelves|cabinet|stool|cushion|planter|plantbox|bar_?table|tablebar|bookcase|dresser|counter|pod_?bed|pleasure_pod|lifepod", Options)),
        ("Industrial", new(@"generator|drill|fuel|tank(?!_?trap)|pipe(?!s?_?wall)|power_?junction|junction|mechanical|arm\d|containment|cells|transmit|tower|barricade|tank_?trap|tire|concrete|sludge|pump|vent|engine|jack|mining|diag_bench|tool|heater|fan|gun|turret|sensor|satellite|solar|warhead|bomb|cable|conduit|valve|machine|crane|antenna|scaffold|forklift|truck|car_|car\d|vehicle|mako|freighter|frieghter|escape_?pod|tent", Options)),
        ("Debris", new(@"debris|rubble|rock|stone|boulder|corpse|skull|bone|wreck|scrap|dmgd|damaged|broken|pile", Options)),
        ("Alien", new(@"geth_(pike|pile|pod|wall|tower|bomb|battle)|pike|rachni|thorian|harvester|anthid|prothean|statue|beacon|hexagon|keeper", Options)),
        ("Plants", new(@"plant|grass|fern|bush|tree|leaf|foliage|flower|vine|anemone|weed|shrub|coral", Options)),
        ("Small items", new(@"bottle|cup|glasses|military_maps|drink|meal|protein|snack|paper|binder|book|pad\b|mug|plate_\d|food|dispenser|cushion", Options)),
        ("Signs", new(@"sign|poster|banner|flag", Options)),
    ];

    /// <summary>Words of architecture: a mesh that has one, no category and few placements is level art.</summary>
    private static readonly Regex Architecture = new(@"wall|floor|ceiling|ledge|walkway|catwalk|rail|stair|step|pillar|beam|door|frame|trim|corridor|hall|room|bridge|elev|shaft|plane|backdrop|vista|building|roof|section|segment|plaza|cave|terrain|island|sky|ocean|water|wtrpln|road|path|ramp|tunnel|fence|struct|cliff|mountain|ground|filler|gap|hole|corner|curve|connector|entr|lobby|hangar|hanger|ext\d*$|exterior|facade|window|glass(?!es)|arch|column|dome|gate|lining|shell|hull|panel|plate|strip|cover(?!_\d)|block|piece|part|detail|bit|cap|brace|clamp|support|supp|base|mesh|object|temp|dummy|fake|card|cinpart|blocker|visibility|collision|sand|lava|ice\d|snow|dirt|mound|hill|crater|canyon|valley|dam|silo|house|office|store|tube|bend|opeing|opening|deck|platform|landing|dock", Options);

    /// <summary>Words that make a mesh architecture whatever its category, unless it also has a prop noun.</summary>
    private static readonly Regex StrongArchitecture = new(@"wall(?!_?(lamp|light|screen|safe))|floor(?!_?(lamp|light|plant))|ceiling|ledge|walkway|catwalk|railing|rail(?!_?post)|stair(?!s_?portable)|pillar|beam|doorframe|frame|trim|corridor|hall|room|bridge|elev|shaft|plane|backdrop|vista|building|roof|section|segment|plaza|terrain|island|ocean|water|road|ramp|tunnel|fence|struct|cliff|filler|gap|hole|curve|connector|facade|window|column|lining|hull|plate|strip|piece|part\d|cinpart|tube", Options);

    private static readonly Regex StrongProp = new(@"chair|crate|console|computer|terminal|monitor|(?<!c)lamp|locker|table|desk|couch|bench|bunk|cot_|shelf|medical|lifepod|seat|cushion|planter|container(?!connector)|tire|corpse|bottle|drink|cup_", Options);

    /// <summary>The first category rule the mesh's name matches; null when none does.</summary>
    public static string? CategoryOf(string name) => CategoryRules.FirstOrDefault(r => r.Rule.IsMatch(name)).Category;

    /// <summary>
    /// What the rules make of a mesh, and its category. In order: excluded packages, helpers; the APL prop library (biog_apl_*
    /// and kin) is always in, 'apl-large' over <see cref="LargeSize"/>, 'Other' when no category matches; elsewhere a mesh over
    /// that size, flat (a sign, glass), or with an architecture word and no prop noun is architecture; a mesh no category
    /// names is 'unknown' (or architecture by a weaker word); a categorised mesh with a weaker architecture word that vanilla
    /// places fewer than twice is architecture too. The rest is 'levelprop'.
    /// </summary>
    /// <param name="path">The mesh's InstancedFullPath: its top package and its name are matched.</param>
    /// <param name="dims">The bounds box's full size (2 × extent) on X, Y, Z.</param>
    /// <param name="placed">StaticMeshComponents of the game's levels that show it.</param>
    public static (string Curation, string? Category) Classify(string path, float[] dims, int placed)
    {
        var parts = path.Split('.');
        var top = parts[0];
        var name = parts[^1];
        if (ExcludedPackage.IsMatch(top)) return ("excluded-pkg", null);
        if (Helper.IsMatch(name)) return ("excluded-helper", null);

        var category = CategoryOf(name);
        var size = dims.Max();
        var apl = top.Contains("apl", StringComparison.OrdinalIgnoreCase);
        if (apl) return (size > LargeSize ? "apl-large" : "apl", category ?? "Other");
        if (size > LargeSize) return ("arch-size", category);
        if (Flat(dims)) return ("arch-flat", category);
        if (StrongArchitecture.IsMatch(name) && !StrongProp.IsMatch(name)) return ("arch-name", category);
        var architectureWord = Architecture.IsMatch(name);
        if (category is null) return (architectureWord ? "arch-name" : "unknown", null);
        if (architectureWord && placed < 2) return ("arch-name", category);
        return ("levelprop", category);
    }

    /// <summary>In the default list (the menu's categories); search reaches the rest.</summary>
    public static bool IsCurated(string curation) => curation is "apl" or "apl-large" or "levelprop";

    /// <summary>Thinnest side under 2% of the longest, and longer than 50 units: signs, glass panes, strips.</summary>
    private static bool Flat(float[] dims)
    {
        var sorted = dims.Order().ToArray();
        return sorted[2] > 50 && sorted[0] < 0.02f * sorted[2];
    }
}
