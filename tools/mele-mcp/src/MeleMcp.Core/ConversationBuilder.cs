using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Action">added (copied from its template), found (a node with its strref already exists) or existing (named E&lt;n&gt;/R&lt;n&gt;).</param>
public sealed record BuiltNode(string Name, string Label, string Action, int? StrRef, IReadOnlyList<string> Changes);

/// <param name="Created">The copy of the 'from' template, when the package had no conversation of that name.</param>
public sealed record ConvBuildResult(string Path, ExportRow Conversation, ConvCreated? Created, IReadOnlyList<BuiltNode> Nodes, IReadOnlyList<string> Start,
    int StringCount, IReadOnlyList<string> TlkFiles, IReadOnlyList<string> Notes, IReadOnlyList<string> BackupPaths);

/// <summary>
/// Builds a Game 1 conversation from a YAML file: lines (copied from template nodes), text in every language, plot
/// conditions and transitions, wheels, replies' next lines and the starting list. A missing conversation is copied from
/// the file's 'from' template. Running it again changes nothing new: the conversation is found by its name, a line by
/// its strref, and every list given in the file replaces the one in the conversation.
/// </summary>
public static partial class ConversationBuilder
{
    [GeneratedRegex(@"^(?:(?<not>not)\s+)?bool\s+(?<id>\d+)(?:\s+(?<value>true|false))?$", RegexOptions.IgnoreCase)]
    private static partial Regex BoolCondition();

    /// <summary>"conditional &lt;id&gt; [param &lt;n&gt;]": also a level's <c>goTo</c> step (<see cref="LevelBuilder"/>).</summary>
    [GeneratedRegex(@"^conditional\s+(?<id>\d+)(?:\s+param\s+(?<param>-?\d+))?$", RegexOptions.IgnoreCase)]
    internal static partial Regex FunctionCondition();

    [GeneratedRegex(@"^[ER]\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex NodeLabel();

    private sealed record Ref(string Name, int Line);

    private sealed record Condition(int Func, int Param, bool IsFunction);

    private sealed record WheelSpec(int? StrRef, string? Category, Dictionary<string, string>? Text, int Line);

    private sealed record Choice(Ref To, WheelSpec? Override);

    private sealed record FromSpec(string Package, string Conversation, int ResRef, int NodeIds);

    private sealed class NodeSpec
    {
        public required string Name;
        public required int Line;
        public bool Existing;
        public bool IsReply;
        public int? StrRef;
        public Ref? Template;
        public float? Length;
        public Dictionary<string, string>? Text;
        public Condition? If;
        public int? Transition;
        public WheelSpec? Wheel;
        public List<Choice>? Replies;
        public List<Ref>? Then;
    }

    private sealed class Spec
    {
        public string? Package;
        public string? Conversation;
        public FromSpec? From;
        public string DefaultLanguage = "INT";
        public List<string> GlobalTlk = [];
        public List<Ref>? Start;
        public List<NodeSpec> Nodes = [];
    }

    /// <param name="outRoot">mod_build out=: the mod root to build into instead (see <see cref="YamlFiles.Target"/>).</param>
    public static ConvBuildResult Build(MEGame game, string yamlPath, string? packageOverride, string? outRoot = null)
    {
        if (!game.IsGame1()) throw new ArgumentException("conv_build handles Game 1 (LE1) conversations.");
        LecHost.EnsureInitialized();
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var spec = Parse(File.ReadAllText(file));
        var dir = Path.GetDirectoryName(file)!;
        var notes = new List<string>();
        var (package, missing) = Target(game, packageOverride ?? spec.Package ?? throw new ArgumentException("The file has no 'package'."), dir, outRoot, notes);
        if (missing) throw new FileNotFoundException($"No package {package}: a conversation goes into an existing level (level_build or level_new creates one).");
        var conversation = spec.Conversation ?? throw new ArgumentException("The file has no 'conversation'.");
        // GlobalTlk files usually sit next to the level; resolve and check every target before writing any.
        var globals = spec.GlobalTlk.Select(g => InOut(Locate(game, g, Path.GetDirectoryName(package)!, dir), outRoot, "globalTlk")).ToList();
        foreach (var target in globals.Prepend(package)) PackageWriter.EnsureWritable(game, target);

        var strings = new Dictionary<int, Dictionary<string, string>>();
        var backups = new List<string>();
        var tlkFiles = new List<string>();

        var ((row, created, built, start, path), backup) = PackageWriter.Edit(game, package, pcc =>
        {
            var conv = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioConversation" && (e.ObjectName.Instanced.Equals(conversation, StringComparison.OrdinalIgnoreCase)
                                                                                           || e.InstancedFullPath.Equals(conversation, StringComparison.OrdinalIgnoreCase)));
            ConvCreated? created = null;
            if (conv is null && spec.From is { } from)
            {
                (conv, created) = ConversationEditor.CreateIn(game, pcc, from.Package, from.Conversation, conversation, from.ResRef, from.NodeIds);
                notes.Add($"Created {conv.InstancedFullPath} from {from.Conversation}: {created.NewExports} exports, resref {from.ResRef}, node ids from {from.NodeIds}, plot removed from {created.PlotCleared} nodes.");
            }
            if (conv is null && !conversation.StartsWith('#'))
            {
                throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no BioConversation '{conversation}'. Give 'from' (a conversation to copy, with resref and nodeIds) to create it.");
            }
            conv ??= ExportInspector.Resolve(pcc, conversation);
            if (conv.ClassName != "BioConversation") throw new ArgumentException($"#{conv.UIndex} is a {conv.ClassName}, not a BioConversation.");

            var labels = PlaceNodes(pcc, conv, spec, notes, out var placed);
            var built = ApplyFields(game, conv, spec, labels, placed, strings, notes);
            var start = ApplyStart(conv, spec, labels);
            tlkFiles.AddRange(WriteLocalStrings(pcc, conv, strings, spec.DefaultLanguage));
            return (ExportInspector.Row(conv), created, built, start, pcc.FilePath);
        });
        backups.Add(backup);

        foreach (var globalPath in globals)
        {
            var language = LanguageOfFile(Path.GetFileNameWithoutExtension(globalPath));
            var edit = TalkFiles.Set(game, globalPath, StringsFor(strings, language, spec.DefaultLanguage), null);
            backups.Add(edit.BackupPath);
            tlkFiles.Add($"{Path.GetFileName(globalPath)} ({language})");
        }

        return new ConvBuildResult(path, row, created, built, start, strings.Count, tlkFiles, notes, backups);
    }

    private sealed record Placed(string Action, List<string> Changes);

    /// <summary>
    /// Pass 1: gives every node a label, adding the ones whose strref is not in the conversation yet, and moves an
    /// existing node to its 'strref' (with its voice track and lip sync).
    /// </summary>
    private static Dictionary<string, string> PlaceNodes(IMEPackage pcc, ExportEntry conv, Spec spec, List<string> notes, out Dictionary<string, Placed> placed)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        placed = new Dictionary<string, Placed>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in spec.Nodes)
        {
            var props = conv.GetProperties();
            if (node.Existing)
            {
                var (isReply, index) = ConversationEditor.ParseNode(node.Name);
                if (index >= ConversationEditor.NodeList(props, isReply).Count) throw new ArgumentException($"line {node.Line}: {node.Name} does not exist.");
                var label = node.Name.ToUpperInvariant();
                var changes = new List<string>();
                if (node.StrRef is { } strRef && ConversationEditor.SetTextIn(pcc, conv, label, strRef) is { } retext)
                {
                    changes.Add($"strref ${retext.OldStrRef} → ${strRef}");
                    if (retext.FaceFxLines.Count > 0) changes.Add($"lip sync {string.Join(", ", retext.FaceFxLines)}");
                    notes.AddRange(retext.Notes.Select(n => $"{node.Name}: {n}"));
                }
                labels[node.Name] = label;
                placed[node.Name] = new Placed("existing", changes);
                continue;
            }

            var list = ConversationEditor.NodeList(props, node.IsReply);
            var found = list.ToList().FindIndex(n => n.GetProp<StringRefProperty>("srText")?.Value == node.StrRef);
            if (found >= 0)
            {
                labels[node.Name] = $"{(node.IsReply ? 'R' : 'E')}{found}";
                placed[node.Name] = new Placed("found", []);
                continue;
            }

            var template = node.Template ?? throw new ArgumentException($"line {node.Line}: new line '{node.Name}' needs a template (E<n> or R<n> to copy).");
            var templateLabel = Label(labels, template);
            if (ConversationEditor.ParseNode(templateLabel).IsReply != node.IsReply)
            {
                throw new ArgumentException($"line {template.Line}: '{node.Name}' is {(node.IsReply ? "a reply" : "an entry")}; copy a template of the same kind.");
            }
            var added = ConversationEditor.AddNodeIn(pcc, conv, templateLabel, node.StrRef!.Value, node.Length, keepPlot: false);
            labels[node.Name] = added.Label;
            placed[node.Name] = new Placed("added", []);
            notes.AddRange(added.Notes.Select(n => $"{node.Name}: {n}"));
        }
        return labels;
    }

    /// <summary>Pass 2: plot fields, wheels, next lines and lengths; collects the strings to write.</summary>
    private static List<BuiltNode> ApplyFields(MEGame game, ExportEntry conv, Spec spec, Dictionary<string, string> labels, Dictionary<string, Placed> placed,
                                               Dictionary<int, Dictionary<string, string>> strings, List<string> notes)
    {
        // Wheel text and category already used for each reply, for choices that do not give them.
        var known = new Dictionary<int, (int Paraphrase, NameReference Category)>();
        foreach (var entry in ConversationEditor.NodeList(conv.GetProperties(), replies: false))
        {
            foreach (var choice in entry.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")?.ToList() ?? [])
            {
                var index = choice.GetProp<IntProperty>("nIndex")?.Value ?? -1;
                if (index >= 0 && !known.ContainsKey(index) && choice.GetProp<EnumProperty>("Category") is { } category)
                {
                    known[index] = (choice.GetProp<StringRefProperty>("srParaphrase")?.Value ?? 0, category.Value);
                }
            }
        }
        var byLabel = spec.Nodes.ToDictionary(n => labels[n.Name], n => n, StringComparer.OrdinalIgnoreCase);

        var built = new List<BuiltNode>();
        foreach (var node in spec.Nodes)
        {
            var label = labels[node.Name];
            var (isReply, index) = ConversationEditor.ParseNode(label);
            var props = conv.GetProperties();
            var n = ConversationEditor.NodeList(props, isReply)[index].Properties;
            var changes = placed[node.Name].Changes;
            var strRef = n.GetProp<StringRefProperty>("srText")?.Value;

            if (node.Text is { } text)
            {
                if (strRef is not > 0) throw new ArgumentException($"line {node.Line}: {node.Name} has no text strref to write to.");
                Collect(strings, strRef.Value, text, node.Line);
                changes.Add($"text ${strRef}");
            }
            if (node.If is { } cond)
            {
                n.AddOrReplaceProp(new IntProperty(cond.Func, "nConditionalFunc"));
                n.AddOrReplaceProp(new IntProperty(cond.Param, "nConditionalParam"));
                n.AddOrReplaceProp(new BoolProperty(cond.IsFunction, "bFireConditional"));
                changes.Add(cond.Func < 0 ? "no condition" : cond.IsFunction ? $"if conditional {cond.Func}({cond.Param})" : $"if bool {cond.Func}({cond.Param})");
            }
            if (node.Transition is { } transition)
            {
                n.AddOrReplaceProp(new IntProperty(transition, "nStateTransition"));
                changes.Add(transition < 0 ? "no transition" : $"transition {transition}");
            }
            if (node.Wheel is { Text: { } wheelText, StrRef: { } wheelStrRef } wheel) Collect(strings, wheelStrRef, wheelText, wheel.Line);

            if (node.Replies is { } replies)
            {
                if (isReply) throw new ArgumentException($"line {node.Line}: {node.Name} is a reply; a reply lists its next lines in 'then'.");
                var list = new ArrayProperty<StructProperty>("ReplyListNew");
                foreach (var choice in replies)
                {
                    var to = Label(labels, choice.To);
                    var (toReply, toIndex) = ConversationEditor.ParseNode(to);
                    if (!toReply) throw new ArgumentException($"line {choice.To.Line}: a wheel choice leads to a reply (R<n>), not {to}.");
                    var target = byLabel.GetValueOrDefault(to)?.Wheel;
                    var hasUsed = known.TryGetValue(toIndex, out var used);
                    var paraphrase = choice.Override?.StrRef ?? target?.StrRef ?? (hasUsed ? used.Paraphrase : (int?)null)
                                     ?? throw new ArgumentException($"line {choice.To.Line}: no wheel text for {choice.To.Name}: give the reply a 'wheel' or the choice a 'paraphrase'.");
                    var categoryName = choice.Override?.Category ?? target?.Category;
                    var category = categoryName is not null ? ConversationEditor.Category(game, categoryName)
                                   : hasUsed ? used.Category
                                   : throw new ArgumentException($"line {choice.To.Line}: no wheel category for {choice.To.Name}: give the reply a 'wheel' or the choice a 'category'.");
                    if (choice.Override is { Text: { } overrideText, StrRef: { } overrideStrRef }) Collect(strings, overrideStrRef, overrideText, choice.Override.Line);
                    list.Add(new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(toIndex, "nIndex"),
                        new StringRefProperty(paraphrase, "srParaphrase"),
                        new StrProperty("", "sParaphrase"),
                        new EnumProperty(category, "EReplyCategory", game, "Category"),
                        new NoneProperty(),
                    }));
                }
                n.AddOrReplaceProp(list);
                changes.Add($"wheel {string.Join(", ", replies.Select(r => Label(labels, r.To)))}");
            }
            if (node.Then is { } then)
            {
                if (!isReply) throw new ArgumentException($"line {node.Line}: {node.Name} is an entry; an entry lists its wheel in 'replies'.");
                var next = new ArrayProperty<IntProperty>("EntryList");
                foreach (var target in then)
                {
                    var (toReply, toIndex) = ConversationEditor.ParseNode(Label(labels, target));
                    if (toReply) throw new ArgumentException($"line {target.Line}: a reply leads to an entry (E<n>), not {target.Name}.");
                    next.Add(new IntProperty(toIndex));
                }
                n.AddOrReplaceProp(next);
                changes.Add(then.Count == 0 ? "ends the conversation" : $"then {string.Join(", ", then.Select(t => Label(labels, t)))}");
            }
            conv.WriteProperties(props);

            if (node.Length is { } length && placed[node.Name].Action != "added")
            {
                if (ConversationEditor.SetLengthIn(conv, isReply, index, length)) changes.Add($"length {length.ToString(CultureInfo.InvariantCulture)} s");
                else notes.Add($"{node.Name}: no node data, length not set.");
            }
            built.Add(new BuiltNode(node.Name, label, placed[node.Name].Action, strRef, changes));
        }
        return built;
    }

    private static List<string> ApplyStart(ExportEntry conv, Spec spec, Dictionary<string, string> labels)
    {
        var props = conv.GetProperties();
        if (spec.Start is null) return props.GetProp<ArrayProperty<IntProperty>>("m_StartingList")?.Select(i => $"E{i.Value}").ToList() ?? [];

        var starts = new ArrayProperty<IntProperty>("m_StartingList");
        foreach (var start in spec.Start)
        {
            var (isReply, index) = ConversationEditor.ParseNode(Label(labels, start));
            if (isReply) throw new ArgumentException($"line {start.Line}: a conversation starts on an entry, not {start.Name}.");
            starts.Add(new IntProperty(index));
        }
        props.AddOrReplaceProp(starts);
        conv.WriteProperties(props);
        return starts.Select(i => $"E{i.Value}").ToList();
    }

    /// <summary>Writes the strings to the conversation's TLK set: one BioTlkFile per language and Shepard gender (tlk, tlk_M, tlk_FR, tlk_M_FR...).</summary>
    private static List<string> WriteLocalStrings(IMEPackage pcc, ExportEntry conv, Dictionary<int, Dictionary<string, string>> strings, string defaultLanguage)
    {
        if (strings.Count == 0) return [];
        var set = conv.GetProperty<ObjectProperty>("m_oTlkFileSet")?.ResolveToEntry(pcc)
                  ?? throw new ArgumentException($"#{conv.UIndex} has no TLK set (m_oTlkFileSet).");
        var written = new List<string>();
        foreach (var tlk in pcc.Exports.Where(x => x.ClassName == "BioTlkFile" && x.Parent == set.Parent))
        {
            var language = LanguageOfTlk(tlk.ObjectName.Instanced);
            TalkFiles.WriteIn(tlk, StringsFor(strings, language, defaultLanguage));
            written.Add($"{tlk.InstancedFullPath} ({language})");
        }
        return written;
    }

    private static string LanguageOfTlk(string name)
    {
        var rest = name.StartsWith("tlk", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
        if (rest.StartsWith("_M", StringComparison.OrdinalIgnoreCase) && (rest.Length == 2 || rest[2] == '_')) rest = rest[2..];
        return rest.TrimStart('_') is { Length: > 0 } language ? language.ToUpperInvariant() : "INT";
    }

    private static string Label(Dictionary<string, string> labels, Ref r)
    {
        if (labels.TryGetValue(r.Name, out var label)) return label;
        if (NodeLabel().IsMatch(r.Name)) return r.Name.ToUpperInvariant();
        throw new ArgumentException($"line {r.Line}: unknown node '{r.Name}'. Name a node of the file (defined above for a template) or E<n>/R<n>.");
    }

    // ---- YAML ----

    private static Spec Parse(string yaml)
    {
        var root = Root(yaml);
        var spec = new Spec();
        foreach (var (key, value) in TopLevel(root, "package", "conversation", "from", "defaultLanguage", "globalTlk", "start", "nodes"))
        {
            switch (key)
            {
                case "package": spec.Package = Scalar(value); break;
                case "conversation": spec.Conversation = Scalar(value); break;
                case "from": spec.From = FromOf(value); break;
                case "defaultLanguage": spec.DefaultLanguage = Language(Scalar(value)); break;
                case "globalTlk": spec.GlobalTlk = Sequence(value).Select(Scalar).ToList(); break;
                case "start": spec.Start = Sequence(value).Select(RefOf).ToList(); break;
                case "nodes":
                    if (value is not YamlMappingNode nodes) throw Fail(value, "'nodes' maps node names to nodes.");
                    foreach (var (name, body) in nodes.Children) spec.Nodes.Add(ParseNode(Scalar(name), body));
                    break;
            }
        }
        var duplicate = spec.Nodes.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Node '{duplicate.Key}' is defined twice.");
        return spec;
    }

    private static NodeSpec ParseNode(string name, YamlNode body)
    {
        var node = new NodeSpec { Name = name, Line = (int)body.Start.Line, Existing = NodeLabel().IsMatch(name) };
        var entries = body is YamlMappingNode m ? Entries(m, "entry", "reply", "strref", "template", "length", "text", "if", "transition", "wheel", "replies", "then").ToList()
                      : throw Fail(body, $"node '{name}' must be a mapping.");
        foreach (var (key, value) in entries)
        {
            switch (key)
            {
                case "entry" or "reply":
                    if (node.Existing) throw Fail(value, $"{name} already exists; '{key}' (strref of a new line) is for new nodes.");
                    if (node.StrRef is not null) throw Fail(value, "a node is either an entry or a reply.");
                    node.IsReply = key == "reply";
                    node.StrRef = Int(value);
                    break;
                case "strref":
                    if (!node.Existing) throw Fail(value, $"'strref' gives an existing E<n>/R<n> node new text; a new line gives its strref in 'entry' or 'reply'.");
                    node.StrRef = Int(value);
                    break;
                case "template": node.Template = RefOf(value); break;
                case "length": node.Length = Float(value); break;
                case "text": node.Text = Text(value); break;
                case "if": node.If = ConditionOf(value); break;
                case "transition": node.Transition = Scalar(value).Equals("none", StringComparison.OrdinalIgnoreCase) ? -1 : Int(value); break;
                case "wheel": node.Wheel = WheelOf(value, "strref"); break;
                case "replies": node.Replies = Sequence(value).Select(ChoiceOf).ToList(); break;
                case "then": node.Then = Sequence(value).Select(RefOf).ToList(); break;
            }
        }
        if (node.Existing) node.IsReply = char.ToUpperInvariant(name[0]) == 'R';
        else if (node.StrRef is null) throw Fail(body, $"new node '{name}' needs 'entry: <strref>' (NPC line) or 'reply: <strref>' (Shepard line).");
        if (node.Wheel is not null && !node.IsReply) throw Fail(body, $"'{name}' is an entry; only replies have wheel text.");
        return node;
    }

    /// <summary>{package, conversation, resref, nodeIds}: the conversation to copy when the package has none of that name.</summary>
    private static FromSpec FromOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "'from' is a mapping: {package: <file>, conversation: <name>, resref: <id>, nodeIds: <first id>}.");
        string? package = null, conversation = null;
        int? resRef = null, nodeIds = null;
        foreach (var (key, v) in Entries(m, "package", "conversation", "resref", "nodeIds"))
        {
            switch (key)
            {
                case "package": package = Scalar(v); break;
                case "conversation": conversation = Scalar(v); break;
                case "resref": resRef = Int(v); break;
                case "nodeIds": nodeIds = Int(v); break;
            }
        }
        // Each conversation needs its own resref and node ids: a ConvNode event fires on the conversation and node with its numbers.
        return new FromSpec(package ?? throw Fail(value, "'from' needs 'package' (the file with the template conversation)."),
            conversation ?? throw Fail(value, "'from' needs 'conversation' (the template's name)."),
            resRef is > 0 ? resRef.Value : throw Fail(value, "'from' needs 'resref': a positive id of its own (mount × 100 + n, e.g. 742101)."),
            nodeIds is > 0 ? nodeIds.Value : throw Fail(value, "'from' needs 'nodeIds': the first of its own node ids (e.g. 74210100)."));
    }

    private static Choice ChoiceOf(YamlNode value)
    {
        if (value is YamlScalarNode) return new Choice(RefOf(value), null);
        if (value is not YamlMappingNode m) throw Fail(value, "a wheel choice is a node name or {to, category, paraphrase}.");
        var to = m.Children.FirstOrDefault(c => Scalar(c.Key) == "to").Value ?? throw Fail(value, "the choice needs 'to'.");
        return new Choice(RefOf(to), WheelOf(m, "paraphrase", skip: "to"));
    }

    /// <summary>{strref|paraphrase: id, category: X, fr: "...", en: "..."}.</summary>
    private static WheelSpec WheelOf(YamlNode value, string idKey, string? skip = null)
    {
        if (value is not YamlMappingNode m) throw Fail(value, $"wheel text is a mapping: {{{idKey}: <strref>, category: <category>, fr: \"...\"}}.");
        int? strRef = null;
        string? category = null;
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in m.Children)
        {
            var key = Scalar(k);
            if (key == skip) continue;
            if (key == idKey) strRef = Int(v);
            else if (key == "category") category = Scalar(v);
            else text[Language(key)] = Scalar(v);
        }
        if (text.Count > 0 && strRef is null) throw Fail(value, $"wheel text needs '{idKey}' (the strref to write it to).");
        return new WheelSpec(strRef, category, text.Count > 0 ? text : null, (int)m.Start.Line);
    }

    private static Condition ConditionOf(YamlNode value)
    {
        var text = Scalar(value).Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return new Condition(-1, -1, false);
        if (BoolCondition().Match(text) is { Success: true } b)
        {
            var mustBe = !b.Groups["not"].Success && !b.Groups["value"].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            if (b.Groups["not"].Success && b.Groups["value"].Success) throw Fail(value, "write 'not bool <id>' or 'bool <id> false', not both.");
            return new Condition(int.Parse(b.Groups["id"].Value, CultureInfo.InvariantCulture), mustBe ? 1 : 0, false);
        }
        if (FunctionCondition().Match(text) is { Success: true } f)
        {
            var param = f.Groups["param"].Success ? int.Parse(f.Groups["param"].Value, CultureInfo.InvariantCulture) : 0;
            return new Condition(int.Parse(f.Groups["id"].Value, CultureInfo.InvariantCulture), param, true);
        }
        throw Fail(value, $"cannot read condition '{text}'. Use: bool <id>, not bool <id>, bool <id> false, conditional <id> [param <n>], none.");
    }

    private static Ref RefOf(YamlNode value) => new(Scalar(value), (int)value.Start.Line);
}
