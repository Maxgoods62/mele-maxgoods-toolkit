using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using BmfBinary = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;
using MeshBinary = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

// First: numbers print the same on every Windows language (a French one writes 2.5 as "2,5"), in this code and in LEC's.
MeleMcp.Core.NumberText.UseInvariantCulture();
if (args.Length == 0) { Console.WriteLine("ERROR usage: lectool <command> <args>; the commands are listed in tools/lectool/README.md"); Environment.Exit(1); }
// The Legendary Edition folder: MELE_LE_DIR as for mele-mcp (from WSL also set WSLENV=MELE_LE_DIR), else LEC's registry lookup.
var leDir = Environment.GetEnvironmentVariable("MELE_LE_DIR");
if (!string.IsNullOrWhiteSpace(leDir)) LegendaryExplorerCoreLibSettings.Instance.LEDirectory = leDir;
LE1Directory.ReloadDefaultGamePath();
var gameArg = Environment.GetEnvironmentVariable("LECGAME") ?? "LE1";
// Only the three LE games: Enum.Parse also takes ME1, UDK or a number, and a package made for one of those is no LE package.
if (gameArg is not ("LE1" or "LE2" or "LE3")) { Console.WriteLine($"ERROR LECGAME={gameArg}: use LE1, LE2 or LE3"); Environment.Exit(1); }
var game = Enum.Parse<MEGame>(gameArg);
if (game == MEGame.LE2) LE2Directory.ReloadDefaultGamePath(); if (game == MEGame.LE3) LE3Directory.ReloadDefaultGamePath();
// morphface and portfx read a file of another game, so they need that game's object database too.
// LEC's Save reports a failure to this callback and returns as if it had saved: the callback throws, so the command ends there.
LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default, MeleMcp.Core.PackageWriter.SaveFailed, objectDBsToLoad: args[0] is "morphface" or "portfx" ? new[]{MEGame.LE1, MEGame.LE2, MEGame.LE3} : new[]{game});
var cmd = args[0];
// Every file lectool writes, deletes or saves in place goes through here: never one inside the game install (mele-mcp's
// guard, which also sees junctions, shares and \\?\ spellings of that folder).
string Writable(string path) {
  try { MeleMcp.Core.PackageWriter.EnsureWritable(game, path); }
  catch (InvalidOperationException ex) { Console.WriteLine("ERROR " + ex.Message); Environment.Exit(1); }
  return path;
}
// An output that is also an input: deleting or overwriting it first would destroy the input.
void NotAnInput(string outPath, params string[] inputs) {
  foreach (var p in inputs)
    if (MeleMcp.Core.GamePaths.Canonical(p).Equals(MeleMcp.Core.GamePaths.Canonical(outPath), StringComparison.OrdinalIgnoreCase)) {
      Console.WriteLine($"ERROR {outPath}: the output is also an input"); Environment.Exit(1);
    }
}
// Every package lectool saves or creates goes through mele-mcp's PackageWriter: written beside the file, then moved over it,
// so a save that fails or is cut short leaves the file as it was. No backup: lectool makes none.
void SavePackage(IMEPackage pkg, string path = null) => MeleMcp.Core.PackageWriter.Save(pkg, backup: false, path: path);
void NewPackage(string path, MEGame g) => MeleMcp.Core.PackageWriter.Create(path, g);
// What LEC's porter reports: each line is something the ported object lost or could not link (an import no file holds, a
// class it cannot find, a property it could not relink), except its note that a port from another game dropped
// properties the target game does not have. An error line stops the command before it saves.
int relinkErrors = 0;
void Relink(IEnumerable<LegendaryExplorerCore.Misc.EntryStringPair> report) {
  foreach (var r in report) {
    if (r.Message.Contains("Some properties were removed from this object because they do not exist in")) Console.WriteLine("relink note: " + r.Message);
    else { Console.WriteLine("ERROR relink: " + r.Message); relinkErrors++; }
  }
}
if (cmd == "ogg") {
  // ogg <wav> <out>: the wav as an Ogg, through CreateIPSOgg of LEC's ISACTTools.dll
  if (args.Length != 3) { Console.WriteLine("ERROR usage: ogg <wav> <out>"); Environment.Exit(1); }
  var oggPath = Writable(args[2]);
  NotAnInput(oggPath, args[1]);
  var dllDir = Environment.GetEnvironmentVariable("EXTRADLLDIR"); if (dllDir != null) SetDllDirectoryW(dllDir);
  var h = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ISACTTools.dll"));
  var fp = System.Runtime.InteropServices.NativeLibrary.GetExport(h, "CreateIPSOgg");
  var fn = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<CreateIPSOggDel>(fp);
  var wav = File.ReadAllBytes(args[1]); var outb = new byte[wav.Length];
  int r = fn(wav, (uint)wav.Length, outb, (uint)outb.Length, 0.8f);
  Console.WriteLine($"CreateIPSOgg -> {r} bytes from {wav.Length} wav bytes");
  // No Ogg: the build must stop here, not go on with an <out> an earlier run left.
  if (r <= 0) { Console.WriteLine($"ERROR CreateIPSOgg made no Ogg from {Path.GetFileName(args[1])}: nothing written"); Environment.Exit(1); }
  File.WriteAllBytes(oggPath, outb.Take(r).ToArray());
  return;
}
if (cmd == "2da") {
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var exp = pcc.GetUExport(int.Parse(args[2]));
  var t = new Bio2DA(exp);
  Console.WriteLine("ROW\t" + string.Join("\t", t.ColumnNames));
  for (int r = 0; r < t.RowCount; r++) {
    var cells = new List<string>();
    for (int c = 0; c < t.ColumnCount; c++) { var cell = t[r, c]; cells.Add(cell == null ? "" : cell.DisplayableValue); }
    Console.WriteLine(t.RowNames[r] + "\t" + string.Join("\t", cells));
  }
} else if (cmd == "2dapart") {
  // 2dapart <src.pcc> <recipe.yaml> <out.pcc>: a package for M3's Bio2DA merge (.m3da). Per table of the recipe, a
  // <Table>_part_1 export of the source table's class under Package exports named like the source's parents, with the
  // source's columns and only the recipe's rows. A row with `from` starts as a copy of that source row; `set` replaces
  // cells by column name, typed like the copied cell, else like most of the column's cells; `~` empties a cell. A table
  // stored without cell indexes has every cell filled, so an empty cell in its rows is an error: written that way, the
  // cells after it would shift left. Prints the tables as read back from out.pcc.
  if (args.Length != 4) { Console.WriteLine("ERROR usage: 2dapart <src.pcc> <recipe.yaml> <out.pcc>"); Environment.Exit(1); }
  var outPath = Writable(args[3]);
  NotAnInput(outPath, args[1], args[2]);
  if (File.Exists(outPath)) File.Delete(outPath);
  TwoDaRecipe recipe;
  try { recipe = new YamlDotNet.Serialization.DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<TwoDaRecipe>(File.ReadAllText(args[2])); }
  catch (YamlDotNet.Core.YamlException ex) { Console.WriteLine($"ERROR {Path.GetFileName(args[2])}: {ex.Message}"); Environment.Exit(1); return; }
  using var src = MEPackageHandler.OpenMEPackage(args[1]);
  var inv = System.Globalization.CultureInfo.InvariantCulture;
  var errors = new List<string>();
  var parts = new List<string>();
  NewPackage(outPath, MEGame.LE1);
  var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  try {
    var tablesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (path, recipeTable) in recipe?.tables ?? new()) {
      var vexp = src.FindExport(path);
      if (vexp == null || vexp.ClassName is not ("Bio2DA" or "Bio2DANumberedRows")) { errors.Add($"{path}: no Bio2DA or Bio2DANumberedRows export with this path"); continue; }
      if (!tablesSeen.Add(vexp.InstancedFullPath)) { errors.Add($"{path}: listed twice"); continue; }
      if (recipeTable?.rows == null || recipeTable.rows.Count == 0) { errors.Add($"{path}: no rows"); continue; }
      bool numbered = vexp.ClassName == "Bio2DANumberedRows";
      // A Bio2DANumberedRows row name is a number, spelt as the table spells it (8, not 08).
      string RowName(string key) => numbered && int.TryParse(key, System.Globalization.NumberStyles.AllowLeadingSign, inv, out var n) ? n.ToString(inv) : key;
      var source = new Bio2DA(vexp);
      IEntry parent = null;
      foreach (var anc in vexp.InstancedFullPath.Split('.').SkipLast(1)) parent = pkg.CreatePackageExport(NameReference.FromInstancedString(anc), parent, forcedExport: false);
      var exp = pkg.CreateExport(NameReference.FromInstancedString(vexp.ObjectName.Instanced + "_part_1"), vexp.ClassName, parent, indexed: false);
      var part = new Bio2DA { Export = exp, IsIndexed = source.IsIndexed };
      foreach (var c in source.ColumnNames) part.AddColumn(c);
      foreach (var (rowKey, recipeRow) in recipeTable.rows) {
        var rowName = RowName(rowKey);
        var where = $"{path} row {rowKey}";
        if (numbered && !int.TryParse(rowKey, System.Globalization.NumberStyles.AllowLeadingSign, inv, out _)) { errors.Add($"{where}: a Bio2DANumberedRows row name is a number"); continue; }
        if (recipeRow == null) { errors.Add($"{where}: empty, give it `from` or `set`"); continue; }
        if (part.TryGetRowIndexByName(rowName, out _)) { errors.Add($"{where}: listed twice"); continue; }
        if (source.TryGetRowIndexByName(rowName, out _)) Console.WriteLine($"note: {where} is already in {Path.GetFileName(args[1])}: M3 replaces that row with the recipe's");
        int r = part.AddRow(rowName), fromRow = -1;
        if (recipeRow.from != null) {
          if (!source.TryGetRowIndexByName(RowName(recipeRow.from), out fromRow)) { errors.Add($"{where}: no row {recipeRow.from} to copy"); continue; }
          for (int c = 0; c < source.ColumnCount; c++) part[r, c] = CopyCell(source[fromRow, c], pkg);
        }
        foreach (var (col, value) in recipeRow.set ?? new()) {
          if (!source.TryGetColumnIndexByName(col, out int c)) { errors.Add($"{where}: no column {col}"); continue; }
          if (value == null) { part[r, c] = new Bio2DACell { package = pkg }; continue; }
          var type = fromRow >= 0 && source[fromRow, c].Type != Bio2DACell.Bio2DADataType.TYPE_NULL ? source[fromRow, c].Type
            : Enumerable.Range(0, source.RowCount).Select(i => source[i, c].Type).Where(t => t != Bio2DACell.Bio2DADataType.TYPE_NULL)
                .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => (Bio2DACell.Bio2DADataType?)g.Key).FirstOrDefault();
          if (type == null) { errors.Add($"{where}: column {col} has only empty cells, so its type is unknown"); continue; }
          Bio2DACell cell = type switch {
            Bio2DACell.Bio2DADataType.TYPE_INT => int.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out var i) ? new Bio2DACell(i) : null,
            Bio2DACell.Bio2DADataType.TYPE_FLOAT => MeleMcp.Core.NumberText.TryFloat(value, out var f) ? new Bio2DACell(f) : null,
            _ => new Bio2DACell(NameReference.FromInstancedString(value), pkg),
          };
          if (cell == null) { errors.Add($"{where}: {col} is a {type} column, '{value}' is not one"); continue; }
          part[r, c] = cell;
        }
        if (!source.IsIndexed)
          foreach (var c in Enumerable.Range(0, part.ColumnCount).Where(c => part[r, c].Type == Bio2DACell.Bio2DADataType.TYPE_NULL))
            errors.Add($"{where}: {part.ColumnNames[c]} is empty, but this table is stored without cell indexes (every cell filled): give it a value");
      }
      part.Write2DAToExport(exp);
      parts.Add(exp.InstancedFullPath);
    }
    if (parts.Count == 0 && errors.Count == 0) errors.Add("the recipe has no tables");
    if (errors.Count == 0) SavePackage(pkg);
  } catch {
    pkg.Dispose(); File.Delete(outPath);
    throw;
  }
  pkg.Dispose();
  if (errors.Count > 0) {
    foreach (var e in errors) Console.WriteLine("ERROR " + e);
    File.Delete(outPath);
    Environment.Exit(1);
  }
  using var saved = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  foreach (var ifp in parts) {
    var exp = saved.FindExport(ifp);
    var t = new Bio2DA(exp);
    Console.WriteLine($"{exp.UIndex}\t{exp.ClassName}\t{ifp}\tcolumns={t.ColumnCount}\trows={t.RowCount}");
    PrintRows(t, t.RowNames);
  }
} else if (cmd == "2damerge") {
  // 2damerge [--add-only] <m3da> <modDir> <base.pcc> <out.pcc>: M3's Bio2DA merge of one .m3da manifest, done as
  // ME3TweaksCore's Bio2DAMerge.MergeManifest does it (Newtonsoft reads the manifest, the same table lookup, then LEC's
  // Bio2DA.MergeInto, which adds missing rows), on a copy of base.pcc saved as out.pcc. The merge package is found in
  // modDir by file name, as M3 finds it in the DLC's CookedPCConsole. Then out.pcc is read back and each merged table
  // checked once against the rows of all its parts, in manifest order (the last part to name a row decides it): the base
  // columns, every base row no part names unchanged, every part row there with its cells. Base rows a part replaces are
  // listed; with --add-only, replacing a row with other cells is an error (equal cells: these rows were merged before).
  // M3 first resets the tables to its own vanilla copies, so pass a vanilla base.pcc (M3's game backup) when there is
  // one. Exits 1 on an error, or when nothing was merged.
  string[] mergeable = { "Engine.pcc", "SFXGame.pcc", "EntryMenu.pcc", "BIOG_2DA_UNC_AreaMap_X.pcc", "BIOG_2DA_UNC_GalaxyMap_X.pcc",
    "BIOG_2DA_UNC_GamerProfile_X.pcc", "BIOG_2DA_UNC_Movement_X.pcc", "BIOG_2DA_UNC_Music_X.pcc", "BIOG_2DA_UNC_Talents_X.pcc",
    "BIOG_2DA_UNC_TreasureTables_X.pcc", "BIOG_2DA_UNC_UI_X.pcc" };    // Bio2DAMerge.Mergable2DAFiles
  bool addOnly = args.Contains("--add-only");
  var a = args.Where(x => x != "--add-only").ToArray();
  if (a.Length != 5 || a.Skip(1).Any(x => x.StartsWith("--"))) { Console.WriteLine("ERROR usage: 2damerge [--add-only] <m3da> <modDir> <base.pcc> <out.pcc>"); Environment.Exit(1); }
  var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<List<M3DAEntry>>(File.ReadAllText(a[1])) ?? new();
  var (modDir, basePath, outPath) = (a[2], a[3], Writable(a[4]));
  NotAnInput(outPath, a[1], basePath);
  var errors = new List<string>();
  void Fail(string e) { Console.WriteLine("ERROR " + e); errors.Add(e); }
  using var basePkg = MEPackageHandler.OpenMEPackage(basePath, forceLoadFromDisk: true);
  var merged = new List<(int uIndex, string ifp, Bio2DA before, List<Bio2DA> parts)>();
  foreach (var entry in manifest) {
    if (entry.packagefile == null || entry.mergepackagefile == null || entry.mergetables == null) { Fail("a manifest entry lacks packagefile, mergepackagefile or mergetables"); continue; }
    if (!mergeable.Contains(entry.packagefile, StringComparer.OrdinalIgnoreCase)) { Fail($"packagefile {entry.packagefile}: M3 merges only into {string.Join(", ", mergeable)}"); continue; }
    if (!string.Equals(entry.packagefile, Path.GetFileName(basePath), StringComparison.OrdinalIgnoreCase)) { Fail($"packagefile {entry.packagefile}, but the base is {Path.GetFileName(basePath)}"); continue; }
    var modPath = Directory.GetFiles(modDir, entry.mergepackagefile, SearchOption.AllDirectories).FirstOrDefault();
    if (modPath == null) { Fail($"mergepackagefile {entry.mergepackagefile}: not in {modDir}"); continue; }
    using var modPkg = MEPackageHandler.OpenMEPackage(modPath, forceLoadFromDisk: true);
    foreach (var table in entry.mergetables) {
      var objName = NameReference.FromInstancedString(table[(table.LastIndexOf('.') + 1)..]);
      if (!objName.Name.EndsWith("_part")) { Fail($"{table}: the object name does not end with _part"); continue; }
      var tableName = objName.Name[..^5];
      var modTable = modPkg.FindExport(table);
      if (modTable == null || !modTable.IsA("Bio2DA")) { Fail($"{table}: no Bio2DA export with this path in {Path.GetFileName(modPath)}"); continue; }
      bool Matches(ExportEntry x) => !x.IsDefaultObject && x.IsA("Bio2DA") && (x.ObjectName.Instanced.Equals(tableName, StringComparison.OrdinalIgnoreCase)
        || (x.ObjectName.Name.Length > 5 && x.ObjectName.Name.StartsWith(tableName, StringComparison.CurrentCultureIgnoreCase) && x.ObjectName.Name.EndsWith("_part")));
      var candidates = basePkg.Exports.Where(Matches).ToList();
      if (candidates.Count == 0) { Fail($"{table}: no table named {tableName} in {Path.GetFileName(basePath)}"); continue; }
      var baseTable = candidates[0];
      if (candidates.Count > 1) Console.WriteLine($"note: {table}: {candidates.Count} tables match, M3 takes the first: {string.Join(", ", candidates.Select(x => $"#{x.UIndex} {x.InstancedFullPath}"))}");
      var expectedIfp = modTable.Parent == null ? tableName : modTable.Parent.InstancedFullPath + "." + tableName;
      if (!baseTable.InstancedFullPath.Equals(expectedIfp, StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"note: {table} merges into {baseTable.InstancedFullPath}, not {expectedIfp}");
      int known = merged.FindIndex(m => m.uIndex == baseTable.UIndex);
      if (known < 0) { merged.Add((baseTable.UIndex, baseTable.InstancedFullPath, new Bio2DA(baseTable), new())); known = merged.Count - 1; }
      var mod2DA = new Bio2DA(modTable);
      var base2DA = new Bio2DA(baseTable);
      List<int> rows; Bio2DAMergeResult result;
      try { rows = mod2DA.MergeInto(base2DA, out result); }
      catch (Exception ex) { Fail($"{table}: MergeInto {baseTable.InstancedFullPath}: {ex.Message}"); continue; }
      if (result != Bio2DAMergeResult.OK) { Fail($"{table}: MergeInto {baseTable.InstancedFullPath}: {result} (columns: mod {mod2DA.ColumnCount}, base {base2DA.ColumnCount})"); continue; }
      base2DA.Write2DAToExport();
      merged[known].parts.Add(mod2DA);
      Console.WriteLine($"merged {rows.Count} rows of {table} into #{baseTable.UIndex} {baseTable.InstancedFullPath}");
    }
  }
  if (errors.Count == 0 && merged.All(m => m.parts.Count == 0)) Fail("the manifest merged no table");
  if (errors.Count > 0) Environment.Exit(1);
  SavePackage(basePkg, outPath);
  using var outPkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  foreach (var (_, ifp, before, parts) in merged) {
    var after = new Bio2DA(outPkg.FindExport(ifp));
    if (!after.ColumnNames.SequenceEqual(before.ColumnNames)) { Fail($"{ifp}: columns changed"); continue; }
    var expected = new Dictionary<string, (Bio2DA part, int row)>(StringComparer.OrdinalIgnoreCase);
    var order = new List<string>();
    foreach (var p in parts)
      for (int r = 0; r < p.RowCount; r++) { if (!expected.ContainsKey(p.RowNames[r])) order.Add(p.RowNames[r]); expected[p.RowNames[r]] = (p, r); }
    int unchanged = 0, kept = 0;
    var replaced = new List<string>(); var again = new List<string>();
    for (int r = 0; r < before.RowCount; r++) {
      var name = before.RowNames[r];
      if (expected.TryGetValue(name, out var e)) {
        bool equal = e.part.ColumnNames.All(c => SameCell(before[r, c], e.part[e.row, c]));
        (equal ? again : replaced).Add(name);
        if (addOnly && !equal) Fail($"{ifp}: the merge replaces base row {name} (--add-only)");
        continue;
      }
      kept++;
      if (r < after.RowCount && after.RowNames[r] == before.RowNames[r] && Enumerable.Range(0, before.ColumnCount).All(c => SameCell(before[r, c], after[r, c]))) unchanged++;
      else Fail($"{ifp}: base row {name} changed");
    }
    int added = order.Count(n => !before.TryGetRowIndexByName(n, out _));
    foreach (var n in order) {
      var (p, pr) = expected[n];
      if (!after.TryGetRowIndexByName(n, out int ar)) { Fail($"{ifp}: row {n} missing"); continue; }
      var bad = p.ColumnNames.FirstOrDefault(c => !SameCell(p[pr, c], after[ar, c]));
      if (bad != null) Fail($"{ifp}: row {n}: {bad} is '{after[ar, bad].DisplayableValue}', the part has '{p[pr, bad].DisplayableValue}'");
    }
    if (after.RowCount != before.RowCount + added) Fail($"{ifp}: {after.RowCount} rows, expected {before.RowCount} + {added}");
    string Names(List<string> l) => l.Count == 0 ? "" : " (" + string.Join(", ", l) + ")";
    Console.WriteLine($"{ifp}: columns {after.ColumnCount} unchanged; rows {before.RowCount} -> {after.RowCount}; {added} added, {replaced.Count} replaced{Names(replaced)}, {again.Count} merged before{Names(again)}; base rows unchanged {unchanged}/{kept}");
    PrintRows(after, order);
  }
  if (errors.Count > 0) Environment.Exit(1);
} else if (cmd == "actortype") {
  // actortype <recipe.yaml> <template.pcc> <look.pcc> <out.pcc>: a new package holding one actor type at its top level,
  // not forced (a spawned pawn's Tag is its actor type's object name). It gets the `keep` properties of the template's
  // actor type (plain values and imports) and the `take` properties of the look's actor type, each object they name
  // copied with what it references: a subobject of the look's actor type goes under the new one, another export to its
  // own path, an import stays an import. The `moveRoots` trees then move under a Package export `moveUnder`, not forced,
  // so they share no path with the look package's own copies; an import under them is an error, as it could no longer
  // be found. `set` gives plain values, typed by the class. An ObjectReferencer lists the actor type, as in LE1CP's
  // rebuilt henchman packages. No shader cache is copied: pkgcheck tells whether the game's RefShaderCache covers it.
  if (args.Length != 5) { Console.WriteLine("ERROR usage: actortype <recipe.yaml> <template.pcc> <look.pcc> <out.pcc>"); Environment.Exit(1); }
  var (templatePath, lookPath, outPath) = (args[2], args[3], Writable(args[4]));
  NotAnInput(outPath, args[1], templatePath, lookPath);
  if (File.Exists(outPath)) File.Delete(outPath);
  ActorTypeRecipe recipe;
  try { recipe = new YamlDotNet.Serialization.DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<ActorTypeRecipe>(File.ReadAllText(args[1])); }
  catch (YamlDotNet.Core.YamlException ex) { Console.WriteLine($"ERROR {Path.GetFileName(args[1])}: {ex.Message}"); Environment.Exit(1); return; }
  var inv = System.Globalization.CultureInfo.InvariantCulture;
  var errors = new List<string>();
  using var cache = new PackageCache();
  using var template = MEPackageHandler.OpenMEPackage(templatePath, forceLoadFromDisk: true);
  using var look = MEPackageHandler.OpenMEPackage(lookPath, forceLoadFromDisk: true);
  var tType = recipe?.template?.actorType == null ? null : template.FindExport(recipe.template.actorType);
  var lType = recipe?.look?.actorType == null ? null : look.FindExport(recipe.look.actorType);
  if (recipe?.name == null) errors.Add("the recipe has no name");
  if (tType == null) errors.Add($"template.actorType {recipe?.template?.actorType}: not an export of {Path.GetFileName(templatePath)}");
  if (lType == null) errors.Add($"look.actorType {recipe?.look?.actorType}: not an export of {Path.GetFileName(lookPath)}");
  if (tType != null && lType != null && tType.ClassName != lType.ClassName) errors.Add($"the template's actor type is a {tType.ClassName}, the look's a {lType.ClassName}");
  var keyUse = (recipe?.template?.keep ?? new()).Concat(recipe?.look?.take?.Keys ?? Enumerable.Empty<string>()).Concat(recipe?.set?.Keys ?? Enumerable.Empty<string>());
  foreach (var dup in keyUse.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) errors.Add($"{dup.Key} is listed more than once in keep, take and set");
  var under = recipe?.look?.moveUnder;
  if (under != null && (under.Equals(recipe.name, StringComparison.OrdinalIgnoreCase) || (recipe.look.moveRoots ?? new()).Contains(under, StringComparer.OrdinalIgnoreCase)))
    errors.Add($"look.moveUnder {under}: also the actor type's name or a moveRoots tree");
  // Every take is checked against the look as it is on disk: porting from a BIOG_ or GUI_ file re-parents the look's
  // top-level exports in memory (LEC's PrepareGlobalFileForPorting), so a later take would see another path.
  var takes = new List<(string name, ObjectProperty prop, IEntry entry)>();
  if (lType != null) {
    var lProps = lType.GetProperties();
    foreach (var (name, expected) in recipe.look.take ?? new()) {
      if (lProps.FirstOrDefault(p => p.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase)) is not ObjectProperty op) { errors.Add($"take {name}: not an object property that {lType.InstancedFullPath} sets"); continue; }
      var e = look.GetEntry(op.Value);
      if (e == null) errors.Add($"take {name}: None in {lType.InstancedFullPath}");
      else if (!e.InstancedFullPath.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"take {name}: names {e.InstancedFullPath}, the recipe expects {expected}");
      else takes.Add((name, op, e));
    }
  }
  if (errors.Count > 0) { foreach (var e in errors) Console.WriteLine("ERROR " + e); Environment.Exit(1); }
  var lTypeUIndex = lType.UIndex;
  NewPackage(outPath, MEGame.LE1);
  var pcc = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  ExportEntry type = null;
  try {
    var rop = new RelinkerOptionsPackage { Cache = cache };
    type = pcc.CreateExport(NameReference.FromInstancedString(recipe.name), tType.ClassName, null, indexed: false, cache: cache);
    type.ObjectFlags = tType.ObjectFlags;
    type.ExportFlags = 0;
    var props = new PropertyCollection();
    var tProps = tType.GetProperties();
    var keep = new HashSet<string>(recipe.template.keep ?? new(), StringComparer.OrdinalIgnoreCase);
    foreach (var name in keep.Where(k => !tProps.Any(p => p.Name.Instanced.Equals(k, StringComparison.OrdinalIgnoreCase)))) errors.Add($"keep {name}: {tType.InstancedFullPath} does not set it");
    foreach (var p in tProps.Where(p => keep.Contains(p.Name.Instanced))) {
      if (p is ObjectProperty op) {
        var e = template.GetEntry(op.Value);
        if (e is ExportEntry) { errors.Add($"keep {p.Name}: names the export {e.InstancedFullPath}; keep copies only plain values and imports"); continue; }
        props.Add(e == null ? new ObjectProperty(0, p.Name) : new ObjectProperty(EntryImporter.GetOrAddCrossImportOrPackage(e.InstancedFullPath, template, pcc, rop), p.Name));
      } else if (p is ArrayPropertyBase or StructProperty or DelegateProperty) errors.Add($"keep {p.Name}: a {p.PropType} is not supported");
      else props.Add(p);
    }
    foreach (var (name, op, e) in takes) {
      IEntry ported;
      if (e is ImportEntry) ported = EntryImporter.GetOrAddCrossImportOrPackage(e.InstancedFullPath, look, pcc, rop);
      else {
        var parent = e.Parent?.UIndex == lTypeUIndex ? type : EntryExporter.PortParents(e, pcc, cache: cache);
        var report = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, (ExportEntry)e, pcc, parent, true, new RelinkerOptionsPackage { Cache = cache }, out ported);
        foreach (var r in report) errors.Add($"take {name}: relink: {r.Message}");
      }
      props.AddOrReplaceProp(new ObjectProperty(ported, op.Name));
    }
    foreach (var (name, value) in recipe.set ?? new()) {
      var info = GlobalUnrealObjectInfo.GetPropertyInfo(MEGame.LE1, name, tType.ClassName);
      if (info == null) { errors.Add($"set {name}: {tType.ClassName} has no such property"); continue; }
      Property p = info.Type switch {
        PropertyType.IntProperty => int.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out var i) ? new IntProperty(i, name) : null,
        PropertyType.StringRefProperty => int.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out var s) ? new StringRefProperty(s, name) : null,
        PropertyType.FloatProperty => MeleMcp.Core.NumberText.TryFloat(value, out var f) ? new FloatProperty(f, name) : null,
        PropertyType.BoolProperty => bool.TryParse(value, out var b) ? new BoolProperty(b, name) : null,
        PropertyType.NameProperty => new NameProperty(NameReference.FromInstancedString(value), name),
        PropertyType.StrProperty => new StrProperty(value, name),
        PropertyType.ByteProperty when info.IsEnumProp() => GlobalUnrealObjectInfo.GetEnumValues(MEGame.LE1, info.Reference).FirstOrDefault(v => v.Instanced.Equals(value, StringComparison.OrdinalIgnoreCase)) is var ev && ev.Name != null
          ? new EnumProperty(ev, info.Reference, MEGame.LE1, name) : null,
        _ => null,
      };
      if (p == null) { errors.Add($"set {name}: '{value}' is not a value of its type ({info.Type}{(info.Reference != null ? " " + info.Reference : "")}), or the type is not supported"); continue; }
      props.AddOrReplaceProp(p);
    }
    type.WriteProperties(props);
    if (pcc.FindExport(lType.InstancedFullPath) != null) errors.Add($"{lType.InstancedFullPath}, the look's actor type, was copied in through a reference");
    foreach (var d in type.GetAllDescendants().OfType<ExportEntry>()) d.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
    var moved = new List<string>();
    if (recipe.look.moveRoots?.Count > 0) {
      if (recipe.look.moveUnder == null) errors.Add("look.moveRoots needs look.moveUnder");
      else if (pcc.Exports.Any(e => e.Parent == null && e.ObjectName.Instanced.Equals(recipe.look.moveUnder, StringComparison.OrdinalIgnoreCase)))
        errors.Add($"look.moveUnder {recipe.look.moveUnder}: the copy already has a top-level export of that name");
      else {
        var underPkg = pcc.CreatePackageExport(NameReference.FromInstancedString(recipe.look.moveUnder), null, forcedExport: false);
        foreach (var root in recipe.look.moveRoots) {
          var x = pcc.Exports.FirstOrDefault(e => e.Parent == null && e.ObjectName.Instanced.Equals(root, StringComparison.OrdinalIgnoreCase));
          if (x == null) { errors.Add($"moveRoots {root}: not a top-level export of the copy"); continue; }
          var subtree = x.GetAllDescendants().Prepend(x).ToList();
          foreach (var i in subtree.OfType<ImportEntry>()) errors.Add($"moveRoots {root}: the import {i.InstancedFullPath} is under it and could not be found once moved");
          x.Parent = underPkg;
          foreach (var d in subtree.OfType<ExportEntry>()) d.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
          moved.Add($"{root} ({subtree.Count} exports)");
        }
      }
    }
    var referencer = pcc.CreateExport("ObjectReferencer", "ObjectReferencer", null, indexed: true, cache: cache);
    referencer.ObjectFlags = LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForClient | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForServer | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForEdit;
    referencer.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(referencer), new ObjectProperty(type) }, "ReferencedObjects"));
    if (errors.Count == 0) {
      SavePackage(pcc);
      Console.WriteLine($"{Path.GetFileName(outPath)}: exports {pcc.ExportCount}, imports {pcc.ImportCount}{(moved.Count > 0 ? $"; moved under {recipe.look.moveUnder}: {string.Join(", ", moved)}" : "")}");
      foreach (var x in pcc.Exports.Where(e => e.Parent == null)) Console.WriteLine($"top\t{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{(x.IsForcedExport ? "forced" : "not forced")}\t{x.GetAllDescendants().Count} below");
      foreach (var p in type.GetProperties()) Console.WriteLine($"{type.ObjectName.Instanced}\t{p.Name.Instanced}\t{PropText(p, pcc)}");
    }
  } catch {
    pcc.Dispose(); File.Delete(outPath);
    throw;
  }
  pcc.Dispose();
  if (errors.Count > 0) {
    foreach (var e in errors) Console.WriteLine("ERROR " + e);
    File.Delete(outPath);
    Environment.Exit(1);
  }
} else if (cmd == "pkgcheck") {
  // pkgcheck <pcc> [--like <ref.pcc>]... [--allow-import <path>]... [--forbid-root <name>]...: LEC's reference check
  // (every issue it reports is an error), two exports with one path, every material (decals included) whose shaders are
  // neither in the package nor in the game's RefShaderCache, and every import that is not a Package. A Class import
  // must resolve, or be a native class of the package it names (Core.Package). Any other import must resolve the way LEC
  // finds imports to an object of its class and class package, and with --like its path and class must also be an
  // import or an export of one of the reference packages. --allow-import exempts one import path and reports it;
  // --forbid-root fails on an import or a top-level export with that root. Exits 1 on an error.
  var likes = new List<string>(); var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  for (int i = 2; i < args.Length; i += 2) {
    if (args[i] is not ("--like" or "--allow-import" or "--forbid-root")) { Console.WriteLine($"ERROR unknown option {args[i]}"); Environment.Exit(1); }
    if (i + 1 == args.Length) { Console.WriteLine($"ERROR {args[i]} needs a value"); Environment.Exit(1); }
    if (args[i] == "--like") likes.Add(args[i + 1]);
    else if (args[i] == "--allow-import") allowed.Add(args[i + 1]);
    else forbidden.Add(args[i + 1]);
  }
  using var pcc = MEPackageHandler.OpenMEPackage(args[1], forceLoadFromDisk: true);
  using var cache = new PackageCache();
  int errs = 0;
  void Err(string m) { Console.WriteLine("ERROR " + m); errs++; }
  var report = new ReferenceCheckPackage();
  EntryChecker.CheckReferences(report, pcc, LegendaryExplorerCore.Localization.LECLocalizationShim.NonLocalizedStringConverter, Path.GetFileName(args[1]));
  foreach (var m in report.GetBlockingErrors().Concat(report.GetSignificantIssues())) Err($"refs: {m.Entry?.InstancedFullPath}: {m.Message}");
  foreach (var g in pcc.Exports.GroupBy(x => x.InstancedFullPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
    Err($"export path {g.Key}: held by {string.Join(", ", g.Select(x => "#" + x.UIndex))}");
  // LEC's GetBrokenMaterials, for every Material subclass (it reads only the class Material itself)
  var paramSets = new Dictionary<StaticParameterSet, List<ExportEntry>>();
  int materials = 0;
  foreach (var x in pcc.Exports) {
    StaticParameterSet set = null;
    if (x.IsA("Material")) { materials++; set = (StaticParameterSet)ObjectBinary.From<Material>(x).SM3MaterialResource.ID; }
    else if (x.IsA("MaterialInstance")) { materials++; if (x.GetProperty<BoolProperty>("bHasStaticPermutationResource")) set = ObjectBinary.From<MaterialInstance>(x).SM3StaticParameterSet; }
    if (set == null) continue;
    if (!paramSets.TryGetValue(set, out var list)) paramSets[set] = list = new();
    list.Add(x);
  }
  var noShaders = paramSets.Keys.ToHashSet();
  if (pcc.FindExport("SeekFreeShaderCache") is ExportEntry localCache)
    foreach ((StaticParameterSet key, _) in localCache.GetBinaryData<ShaderCache>().MaterialShaderMaps) noShaders.Remove(key);
  if (noShaders.Count > 0) LegendaryExplorerCore.Shaders.RefShaderCacheReader.RemoveStaticParameterSetsThatAreInTheGlobalCache(noShaders, pcc.Game);
  foreach (var x in noShaders.SelectMany(k => paramSets[k])) Err($"shaders: {x.InstancedFullPath} ({x.ClassName}) has no shaders in the package or in the game's RefShaderCache");
  var likeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach (var path in likes) {
    using var r = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
    foreach (var x in r.Exports) likeKeys.Add(x.InstancedFullPath + "|" + x.ClassName);
    foreach (var x in r.Imports) likeKeys.Add(x.InstancedFullPath + "|" + x.ClassName);
  }
  var classes = GlobalUnrealObjectInfo.GetClasses(pcc.Game);
  int checkedImports = 0;
  string Root(IEntry e) { while (e.Parent != null) e = e.Parent; return e.ObjectName.Instanced; }
  string ClassPackage(ExportEntry x) => x.Class switch {
    null => "Core",
    ExportEntry c when c.Parent == null => Path.GetFileNameWithoutExtension(x.FileRef.FilePath),
    IEntry c => Root(c),
  };
  foreach (var i in pcc.Imports) {
    if (forbidden.Contains(Root(i))) Err($"import {i.InstancedFullPath}: its root {Root(i)} is forbidden");
    if (i.ClassName == "Package") continue;
    checkedImports++;
    var resolved = EntryImporter.ResolveImport(i, cache);
    if (allowed.Contains(i.InstancedFullPath)) { Console.WriteLine($"note: allowed import {i.InstancedFullPath} ({i.ClassName}): {(resolved == null ? "not found" : "found in " + Path.GetFileName(resolved.FileRef.FilePath))}"); continue; }
    if (i.ClassName == "Class") {
      if (resolved == null && !(classes.TryGetValue(i.ObjectName.Name, out var ci) && Path.GetFileNameWithoutExtension(ci.pccPath.Replace('\\', '/')).Equals(Root(i), StringComparison.OrdinalIgnoreCase)))
        Err($"class {i.InstancedFullPath}: not found");
      continue;
    }
    if (resolved == null) Err($"import {i.InstancedFullPath} ({i.ClassName}): not found");
    else if (!resolved.ClassName.Equals(i.ClassName, StringComparison.OrdinalIgnoreCase) || !ClassPackage(resolved).Equals(i.PackageFile, StringComparison.OrdinalIgnoreCase))
      Err($"import {i.InstancedFullPath} ({i.PackageFile}.{i.ClassName}): found in {Path.GetFileName(resolved.FileRef.FilePath)} as a {ClassPackage(resolved)}.{resolved.ClassName}");
    if (likes.Count > 0 && !likeKeys.Contains(i.InstancedFullPath + "|" + i.ClassName)) Err($"import {i.InstancedFullPath} ({i.ClassName}): in none of the --like packages with this class");
  }
  foreach (var x in pcc.Exports.Where(x => x.Parent == null && forbidden.Contains(x.ObjectName.Instanced))) Err($"export {x.InstancedFullPath}: a forbidden root");
  Console.WriteLine($"pkgcheck {Path.GetFileName(args[1])}: exports {pcc.ExportCount}, imports {pcc.ImportCount} ({checkedImports} checked), materials {materials}, errors {errs}");
  if (errs > 0) Environment.Exit(1);
} else if (cmd == "pkglist") {
  // pkglist <pcc>: one tab-separated line per export (UIndex, class, instanced full path, F if forced, object flags in
  // hex, data size, a Texture2D's TextureFileCacheName) and per import (UIndex, class, instanced full path)
  using var pcc = MEPackageHandler.OpenMEPackage(args[1], forceLoadFromDisk: true);
  foreach (var x in pcc.Exports)
    Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{(x.IsForcedExport ? "F" : "-")}\t{(ulong)x.ObjectFlags:X}\t{x.DataSize}\t{(x.ClassName == "Texture2D" ? x.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced : "")}");
  foreach (var i in pcc.Imports) Console.WriteLine($"{i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "morphface") {
  // morphface <donor.pcc> <donorFace> <pcc> <face> [--mic <path>] [--sha1 <hex>]: gives the BioMorphFace <face> of <pcc> the
  // look of the BioMorphFace <donorFace>, in place: its LOD0 and LOD1 vertex positions, its bones (m_aFinalSkeleton, by
  // name) and the colors, scalars and textures of its BioMaterialOverride (by parameter name, a texture by object name
  // among the package's own). The donor may be a face of another game. LOD2 stays: the game draws it from the mesh. The two
  // base heads must have the same vertices in the same order on LOD0 and LOD1, or the donor's positions would land on the
  // wrong vertices, and every bone and parameter must exist on both sides: arrays keep their length. --mic also sets a
  // baked material instance of the head (a level pawn holds one) from the same values. --sha1 stops on any other donor
  // look. Saves, reads the file back and exits 1 unless it holds the donor's look; prints the look's SHA-1.
  var a = args.Skip(1).ToList();
  string Opt(string name) { int i = a.IndexOf(name); if (i < 0) return null; var v = i + 1 < a.Count ? a[i + 1] : null; a.RemoveRange(i, Math.Min(2, a.Count - i)); return v ?? ""; }
  var (micPath, wantSha) = (Opt("--mic"), Opt("--sha1"));
  if (a.Count != 4 || micPath == "" || wantSha == "") { Console.WriteLine("usage: morphface <donor.pcc> <donorFace> <pcc> <face> [--mic <path>] [--sha1 <hex>]"); Environment.Exit(1); return; }
  var (donorPath, donorName, path, faceName) = (a[0], a[1], Writable(a[2]), a[3]);
  using var donorPkg = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
  var errors = new List<string>();
  void Stop() { foreach (var e in errors) Console.WriteLine("ERROR " + e); Environment.Exit(1); }
  var donor = donorPkg.FindExport(donorName);
  if (donor?.ClassName != "BioMorphFace") { errors.Add($"{donorName}: no BioMorphFace export with this path in {Path.GetFileName(donorPath)}"); Stop(); return; }
  var dLook = FaceLook.Read(donor);
  if (dLook.Lods.Length < 2) { errors.Add($"{donorName}: {dLook.Lods.Length} LODs in its binary, LOD0 and LOD1 needed"); Stop(); return; }
  var dSha = dLook.Sha1();
  if (wantSha != null && !dSha.Equals(wantSha, StringComparison.OrdinalIgnoreCase)) errors.Add($"{donorName}: its look is {dSha}, not the {wantSha} asked for (a mod changed {Path.GetFileName(donorPath)}?)");
  using (var pcc = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true)) {
    var face = pcc.FindExport(faceName);
    if (face?.ClassName != "BioMorphFace") { errors.Add($"{faceName}: no BioMorphFace export with this path in {Path.GetFileName(path)}"); Stop(); return; }
    var mic = micPath == null ? null : pcc.FindExport(micPath);
    if (micPath != null && mic?.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues") == null) errors.Add($"--mic {micPath}: no material instance with parameters at this path");
    var over = face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(pcc) as ExportEntry;
    if (over?.ClassName != "BioMaterialOverride") errors.Add($"{faceName}: m_oMaterialOverrides is no BioMaterialOverride export of this package");
    var dHead = FaceLook.BaseHead(donor);
    var head = FaceLook.BaseHead(face);
    if (dHead == null || head == null) errors.Add("m_oBaseHead: both faces need a SkeletalMesh export in their own package");
    else foreach (int l in new[] { 0, 1 }) {
      if (l >= dHead.LODModels.Length || l >= head.LODModels.Length) { errors.Add($"the base heads need a LOD{l}"); continue; }
      var (dv, v) = (dHead.LODModels[l].VertexBufferGPUSkin.VertexData, head.LODModels[l].VertexBufferGPUSkin.VertexData);
      if (dv.Length != v.Length || dLook.Lods[l].Length != v.Length) { errors.Add($"LOD{l}: {dv.Length} verts on the donor's base head, {v.Length} on this one, {dLook.Lods[l].Length} in the donor's face"); continue; }
      int off = Enumerable.Range(0, v.Length).Count(i => (dv[i].Position - v[i].Position).Length() > 0.001f);
      if (off > 0) errors.Add($"LOD{l}: {off} of {v.Length} verts of the two base heads differ: not the same head, or not the same order");
    }
    if (errors.Count > 0) { Stop(); return; }
    var bin = ObjectBinary.From<BmfBinary>(face);
    if (bin.LODs.Length < 2) errors.Add($"{faceName}: {bin.LODs.Length} LODs in its binary, LOD0 and LOD1 needed");
    var props = face.GetProperties();
    var skel = props.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton");
    if (skel == null) { errors.Add($"{faceName}: no m_aFinalSkeleton"); Stop(); return; }
    var names = skel.Select(s => s.GetProp<NameProperty>("nName").Value.Instanced).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!names.SetEquals(dLook.Bones.Keys)) errors.Add($"m_aFinalSkeleton: the bones differ: {string.Join(", ", names.Except(dLook.Bones.Keys, StringComparer.OrdinalIgnoreCase).Concat(dLook.Bones.Keys.Except(names, StringComparer.OrdinalIgnoreCase)))}");
    foreach (var n in dLook.Params.OneSided(FaceParams.Read(over, false))) errors.Add($"{over.InstancedFullPath}: parameter {n} is not on both sides");
    if (mic != null) foreach (var n in dLook.Params.OneSided(FaceParams.Read(mic, true))) errors.Add($"{mic.InstancedFullPath}: parameter {n} is not on both sides");
    if (errors.Count > 0) { Stop(); return; }
    int moved = Enumerable.Range(0, dLook.Lods[0].Length).Count(i => (dLook.Lods[0][i] - head.LODModels[0].VertexBufferGPUSkin.VertexData[i].Position).Length() > 0.001f);
    bin.LODs[0] = dLook.Lods[0];
    bin.LODs[1] = dLook.Lods[1];
    foreach (var s in skel) {
      var (p, d) = (s.GetProp<StructProperty>("vPos"), dLook.Bones[s.GetProp<NameProperty>("nName").Value.Instanced]);
      p.GetProp<FloatProperty>("X").Value = d[0]; p.GetProp<FloatProperty>("Y").Value = d[1]; p.GetProp<FloatProperty>("Z").Value = d[2];
    }
    face.WritePropertiesAndBinary(props, bin);
    int Texture(string objectName) {
      var t = pcc.Exports.Where(e => e.ClassName == "Texture2D" && e.ObjectName.Instanced.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToList();
      if (t.Count != 1) errors.Add($"texture {objectName}: {t.Count} Texture2D exports of this name in {Path.GetFileName(path)}, one needed");
      return t.Count == 1 ? t[0].UIndex : 0;
    }
    dLook.Params.WriteTo(over, false, Texture, errors);
    if (mic != null) dLook.Params.WriteTo(mic, true, Texture, errors);
    if (errors.Count > 0) { Stop(); return; }
    SavePackage(pcc);
    Console.WriteLine($"{Path.GetFileName(path)}: {face.InstancedFullPath} now looks like {donor.ObjectName.Instanced}: {moved} of {dLook.Lods[0].Length} LOD0 verts off the base head, {skel.Count} bones, {dLook.Params.Colors.Count} colors, {dLook.Params.Scalars.Count} scalars, {dLook.Params.Textures.Count} texture(s){(mic != null ? ", the same on " + mic.ObjectName.Instanced : "")}");
  }
  using var saved = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
  var back = saved.FindExport(faceName);
  var sha = FaceLook.Read(back).Sha1();
  if (sha != dSha) errors.Add($"read back: {faceName} has the look {sha}, the donor {dSha}");
  if (micPath != null && FaceParams.Read(saved.FindExport(micPath), true).Text() != dLook.Params.Text()) errors.Add($"read back: {micPath} differs from the donor's parameters");
  if (errors.Count > 0) { Stop(); return; }
  Console.WriteLine($"look sha1 {sha}");
} else if (cmd == "gfxpackage") {
  // gfxpackage <src.pcc> <movie> <out.pcc> [--raw-data <file>] [--copy-textures] [--add-texture <name> <like>]...
  //            [--texture <name> <png|#RRGGBB>]...: a new package holding a copy of a GFx movie at its top level, not
  // forced, under the movie's own name, so that a HandlerLibrary PanelResource "<out file name>.<movie name>" opens it.
  // --raw-data gives the copy that file's bytes (a patched movie) instead of the source's. The textures the movie
  // references stay imports of their paths in src.pcc (a startup package's are always loaded); --copy-textures makes
  // them top-level copies, as LE1CP's GUI packages have them. --add-texture adds a top-level copy of the referenced
  // texture <like> under a new name to the references, for an image the patched movie adds. --texture makes a
  // referenced or added texture a top-level copy whose image comes from a PNG of the texture's size, or is one colour;
  // it keeps the texture's format, mip count and NeverStream, stored in the package.
  var errors = new List<string>();
  const string usage = "gfxpackage <src.pcc> <movie> <out.pcc> [--raw-data <file>] [--copy-textures] [--add-texture <name> <like>]... [--texture <name> <png|#RRGGBB>]...";
  if (args.Length < 4) { Console.WriteLine("ERROR usage: " + usage); Environment.Exit(1); }
  var (srcPath, moviePath, outPath) = (args[1], args[2], Writable(args[3]));
  var copyAll = false;
  string rawPath = null;
  var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
  var adds = new List<(string Name, string Like)>();
  for (int i = 4; i < args.Length; i++) {
    if (args[i] == "--copy-textures") copyAll = true;
    else if (args[i] == "--raw-data" && i + 1 < args.Length) { if (rawPath != null) errors.Add("--raw-data: given twice"); rawPath = args[++i]; }
    else if (args[i] == "--texture" && i + 2 < args.Length) { if (!replace.TryAdd(args[i + 1], args[i + 2])) errors.Add($"--texture {args[i + 1]}: given twice"); i += 2; }
    else if (args[i] == "--add-texture" && i + 2 < args.Length) { adds.Add((args[i + 1], args[i + 2])); i += 2; }
    else errors.Add(args[i] switch {
      "--texture" => "--texture needs a texture name and a PNG or #RRGGBB",
      "--add-texture" => "--add-texture needs a new texture name and the referenced texture it copies",
      "--raw-data" => "--raw-data needs a file",
      _ => $"unknown option {args[i]}",
    });
  }
  NotAnInput(outPath, new[] { srcPath, rawPath }.Concat(replace.Values.Where(v => !v.StartsWith('#'))).Where(p => p != null).ToArray());
  if (rawPath != null && !File.Exists(rawPath)) errors.Add($"--raw-data {rawPath}: no such file");
  foreach (var (name, v) in replace)
    if (v.StartsWith('#') ? v.Length != 7 || !int.TryParse(v[1..], System.Globalization.NumberStyles.HexNumber, null, out _) : !File.Exists(v))
      errors.Add($"--texture {name}: {v} is neither a PNG file nor a colour #RRGGBB");
  foreach (var dup in adds.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
    errors.Add($"--add-texture {dup.Key}: given twice");
  if (errors.Count > 0) { foreach (var e in errors) Console.WriteLine("ERROR " + e); Environment.Exit(1); }
  if (File.Exists(outPath)) File.Delete(outPath);
  using var src = MEPackageHandler.OpenMEPackage(srcPath, forceLoadFromDisk: true);
  var movie = src.FindExport(moviePath);
  if (movie == null || movie.ClassName != "GFxMovieInfo") { Console.WriteLine($"ERROR {moviePath}: not a GFxMovieInfo export of {Path.GetFileName(srcPath)}"); Environment.Exit(1); }
  var mProps = movie.GetProperties();
  var srcRaw = mProps.GetProp<ImmutableByteArrayProperty>("RawData")?.Bytes;
  var raw = rawPath != null ? File.ReadAllBytes(rawPath) : srcRaw;
  var refEntries = (mProps.GetProp<ArrayProperty<ObjectProperty>>("References") ?? new ArrayProperty<ObjectProperty>("References")).Select(r => src.GetEntry(r.Value)).ToList();
  if (srcRaw == null) errors.Add($"{moviePath}: no RawData");
  if (movie.GetBinaryData().Length > 0) errors.Add($"{moviePath}: {movie.GetBinaryData().Length} bytes of binary data after the properties, which this command does not copy");
  foreach (var p in mProps.Where(p => p.Name.Instanced is not ("RawData" or "References") && p is ObjectProperty or ArrayPropertyBase or StructProperty))
    errors.Add($"{moviePath}: property {p.Name.Instanced} is not copied by this command");
  bool IsRef(string n) => refEntries.Any(e => e != null && e.ObjectName.Instanced.Equals(n, StringComparison.OrdinalIgnoreCase));
  foreach (var (name, like) in adds) {
    if (IsRef(name)) errors.Add($"--add-texture {name}: the movie already references a texture of that name");
    var l = refEntries.FirstOrDefault(e => e != null && e.ObjectName.Instanced.Equals(like, StringComparison.OrdinalIgnoreCase));
    if (l is not ExportEntry { ClassName: "Texture2D" }) errors.Add($"--add-texture {name}: {like} is not a Texture2D export the movie references");
  }
  foreach (var name in replace.Keys.Where(n => !IsRef(n) && !adds.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
    errors.Add($"--texture {name}: not a texture the movie references or --add-texture adds");
  foreach (var dup in refEntries.Where(e => e != null).GroupBy(e => e.ObjectName.Instanced, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
    errors.Add($"the movie references two objects named {dup.Key}: their top-level copies would share a path");
  if (errors.Count > 0) { foreach (var e in errors) Console.WriteLine("ERROR " + e); Environment.Exit(1); }
  NewPackage(outPath, game);
  var pcc = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  try {
    using var cache = new PackageCache();
    var copy = pcc.CreateExport(movie.ObjectName, movie.ClassName, null, indexed: false, cache: cache);
    copy.ObjectFlags = movie.ObjectFlags;
    copy.ExportFlags = 0;
    // A top-level, not forced copy of one of the movie's textures.
    ExportEntry CopyTexture(ExportEntry e) {
      var report = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, e, pcc, null, true, new RelinkerOptionsPackage { Cache = cache }, out var ported);
      foreach (var r in report) errors.Add($"{e.InstancedFullPath}: relink: {r.Message}");
      var tex = (ExportEntry)ported;
      tex.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
      return tex;
    }
    // The texture's image from a PNG of its size or a colour; null and an error when it cannot be.
    string ReplaceImage(ExportEntry tex, string name, string v) {
      if (tex.ClassName != "Texture2D") { errors.Add($"--texture {name}: a {tex.ClassName}, not a Texture2D"); return null; }
      var neverStream = tex.GetProperty<BoolProperty>("NeverStream")?.Value == true;
      var t2d = new Texture2D(tex);
      var (w, h) = (t2d.Mips[0].width, t2d.Mips[0].height);
      var px = new byte[w * h * 4];
      if (v.StartsWith('#')) {
        var c = Convert.ToInt32(v[1..], 16);
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)c; px[i + 1] = (byte)(c >> 8); px[i + 2] = (byte)(c >> 16); px[i + 3] = 255; }   // BGRA
      } else {
        // Decoded here, not by LEC's Image(file): that one hands RGBA bytes on as ARGB (red and blue swapped) and copies
        // only the first 4 MB of ImageSharp's pixel buffer (a 1024x2048 portrait does not load).
        using var png = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(v);
        if (png.Width != w || png.Height != h) { errors.Add($"--texture {name}: {v} is {png.Width}x{png.Height}, the texture {w}x{h}"); return null; }
        png.CopyPixelDataTo(px);
      }
      var img = LegendaryExplorerCore.Textures.Image.LoadFromRaw(px, LegendaryExplorerCore.Textures.PixelFormat.ARGB, w, h);
      foreach (var m in t2d.Replace(img, tex.GetProperties(), isPackageStored: true)) Console.WriteLine($"note: --texture {name}: {m}");
      if (neverStream) {
        // LEC's Replace drops NeverStream below 6 mips; the vanilla UI textures have one mip and NeverStream.
        var tp = tex.GetProperties();
        tp.AddOrReplaceProp(new BoolProperty(true, "NeverStream"));
        tex.WriteProperties(tp);
      }
      return $"{v} ({w}x{h}, {tex.GetProperty<EnumProperty>("Format")?.Value.Instanced})";
    }
    // Added textures first: the cloner would hand back a copy made earlier under the same path.
    var added = new List<(string Name, ExportEntry Tex)>();
    foreach (var (name, like) in adds) {
      var tex = CopyTexture((ExportEntry)refEntries.First(e => e != null && e.ObjectName.Instanced.Equals(like, StringComparison.OrdinalIgnoreCase)));
      tex.ObjectName = new NameReference(name);
      added.Add((name, tex));
    }
    var refs = new ArrayProperty<ObjectProperty>("References");
    var how = new List<string>();
    foreach (var e in refEntries) {
      if (e == null) { refs.Add(new ObjectProperty(0)); how.Add("None"); continue; }
      var name = e.ObjectName.Instanced;
      var wantCopy = copyAll || replace.ContainsKey(name);
      if (!wantCopy || e is ImportEntry) {
        if (wantCopy) errors.Add($"{e.InstancedFullPath}: an import in {Path.GetFileName(srcPath)}, so it cannot be copied");
        // An import by path, its parents Package imports. (LEC's GetOrAddCrossImportOrPackage copies an export of the
        // source under a forced package instead.)
        var classPkg = e switch {
          ImportEntry ie => ie.PackageFile,
          ExportEntry { Class: null } => "Core",
          ExportEntry { Class.Parent: null } => Path.GetFileNameWithoutExtension(srcPath),
          ExportEntry x => x.Class.Parent.ObjectName.Instanced,
          _ => "Core",
        };
        refs.Add(new ObjectProperty(pcc.GetEntryOrAddImport(e.InstancedFullPath, e.ClassName, classPkg)));
        how.Add($"{name}\timport\t{e.InstancedFullPath}");
        continue;
      }
      var tex = CopyTexture((ExportEntry)e);
      refs.Add(new ObjectProperty(tex));
      if (!replace.TryGetValue(name, out var v)) { how.Add($"{name}\tcopy\t{e.InstancedFullPath}"); continue; }
      if (ReplaceImage(tex, name, v) is { } what) how.Add($"{name}\treplaced\t{what}");
    }
    foreach (var (name, tex) in added) {
      refs.Add(new ObjectProperty(tex));
      var like = adds.First(a => a.Name == name).Like;
      if (!replace.TryGetValue(name, out var v)) { how.Add($"{name}\tadded\ta copy of {like}"); continue; }
      if (ReplaceImage(tex, name, v) is { } what) how.Add($"{name}\tadded\t{what}, from {like}");
    }
    mProps.AddOrReplaceProp(refs);
    if (rawPath != null) mProps.AddOrReplaceProp(new ImmutableByteArrayProperty(raw, "RawData"));
    copy.WriteProperties(mProps);
    if (errors.Count == 0) {
      SavePackage(pcc);
      string Sha1(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();
      Console.WriteLine($"{Path.GetFileName(outPath)}: movie {copy.InstancedFullPath}, RawData {raw.Length} bytes, sha1 {Sha1(raw)}"
                        + (rawPath != null ? $" (from {Path.GetFileName(rawPath)}; the source's sha1 {Sha1(srcRaw)})" : "")
                        + $"; exports {pcc.ExportCount}, imports {pcc.ImportCount}");
      foreach (var line in how) Console.WriteLine("reference\t" + line);
    }
  } catch {
    pcc.Dispose(); File.Delete(outPath);
    throw;
  }
  pcc.Dispose();
  if (errors.Count > 0) {
    foreach (var e in errors) Console.WriteLine("ERROR " + e);
    File.Delete(outPath);
    Environment.Exit(1);
  }
} else if (cmd == "snd") {
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  foreach (var e in pcc.Exports.Where(x => x.ClassName == "SoundNodeWave" && args.Skip(2).Any(f => x.ObjectName.Instanced.Contains(f, StringComparison.OrdinalIgnoreCase)))) {
    var bin = ObjectBinary.From<SoundNodeWave>(e);
    Console.WriteLine($"== {e.UIndex} {e.InstancedFullPath} raw={bin.RawData.Length}");
    try {
      var bp = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
      foreach (var (tag, bank) in new[]{("ICB", bp.ICBBank), ("ISB", bp.ISBBank)}) {
        foreach (var c in bank.GetAllBankChunks()) {
          if (c.ChunkName == "data") { Console.WriteLine($"  {tag} data {c.RawData?.Length} bytes head={System.Text.Encoding.ASCII.GetString(c.RawData.Take(4).ToArray())}"); continue; }
          var str = string.Join(", ", c.GetType().GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance).Where(f => f.Name is not ("RawData" or "Parent" or "SubChunks" or "BankSubChunkMap" or "ChunkName")).Select(f => f.Name + "=" + f.GetValue(c)).Concat(c.GetType().GetProperties(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.DeclaredOnly).Where(pp => pp.GetIndexParameters().Length==0 && pp.Name is not ("SampleData" or "Parent")).Select(pp => { try { return pp.Name + "=" + pp.GetValue(c); } catch { return pp.Name + "=?"; } })));
          if (c.ChunkName is "LIST" or "loop" or "sinf" or "chnk" or "cmpi" or "titl" or "trks" or "prel" or "stri" or "snde" || str.Contains("loop", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  {tag} {c.ChunkName}: {str}");
        }
      }
    } catch (Exception ex) { Console.WriteLine("  parse error " + ex.Message); }
  }
} else if (cmd == "wwise") {
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var exp = pcc.GetUExport(int.Parse(args[2]));
  var bank = ObjectBinary.From<WwiseBankParsed>(exp);
  Console.WriteLine($"bank {exp.InstancedFullPath} id={bank.ID} embedded={bank.EmbeddedFiles.Count} hirc={bank.HIRCObjects.Count}");
  var dumpDir = Environment.GetEnvironmentVariable("WEMDIR") is { } wemDir ? Writable(wemDir) : null;
  var dumpIds = (Environment.GetEnvironmentVariable("WEMIDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToHashSet();
  foreach (var kv in bank.EmbeddedFiles) { if (dumpDir != null && dumpIds.Contains(kv.Key)) File.WriteAllBytes(Path.Combine(dumpDir, kv.Key + ".wem"), kv.Value); }
  foreach (var g in bank.HIRCObjects.Values.GroupBy(h => h.Type)) Console.WriteLine($"  hirc {g.Key} x{g.Count()}");
  var map = new Dictionary<uint, WwiseBankParsed.HIRCObject>(); foreach (var kv in bank.HIRCObjects) map[kv.Key] = kv.Value;
  foreach (var a in args.Skip(3)) {
    uint evId = unchecked((uint)int.Parse(a));
    Console.WriteLine($"event {a} -> {evId}");
    if (!map.TryGetValue(evId, out var ev) || ev is not WwiseBankParsed.Event e2) { Console.WriteLine("  not found"); continue; }
    foreach (var actId in e2.EventActions) {
      if (map.TryGetValue(actId, out var act) && act is WwiseBankParsed.EventAction ea) {
        Console.WriteLine($"  action {actId} type={ea.ActionType} target={ea.ReferencedObjectID}");
        var stack = new Stack<uint>(); stack.Push(ea.ReferencedObjectID); var seen = new HashSet<uint>();
        while (stack.Count > 0) {
          var id = stack.Pop(); if (!seen.Add(id)) continue;
          if (!map.TryGetValue(id, out var o)) { Console.WriteLine($"    {id} not in bank"); continue; }
          var u = o.unparsed ?? Array.Empty<byte>();
          string extra = "";
          if (o.Type == HIRCType.SoundSXFSoundVoice && u.Length > 13) {
            uint src = BitConverter.ToUInt32(u, 5); uint memsize = BitConverter.ToUInt32(u, 9); byte stype = u[4];
            extra = $" streamType={stype} sourceID={src} inMemSize={memsize} embedded={bank.EmbeddedFiles.ContainsKey(src)}";
          }
          Console.WriteLine($"    obj {id} {o.Type} len={u.Length}{extra} head={Convert.ToHexString(u.Take(24).ToArray())}");
          if (o.Type is HIRCType.RandomOrSequenceContainer or HIRCType.SwitchContainer or HIRCType.BlendContainer) {
            // children list: scan for any uint that is a key in map
            for (int i = 0; i + 4 <= u.Length; i++) { var cand = BitConverter.ToUInt32(u, i); if (cand != id && map.ContainsKey(cand) && map[cand].Type == HIRCType.SoundSXFSoundVoice) stack.Push(cand); }
          }
        }
      }
    }
  }
} else if (cmd == "swf") {
  // swf <pcc> <outDir> [movie]: each GFx movie's RawData (or only that movie's, by path) as <name>.gfx
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var movies = pcc.Exports.Where(x => x.ClassName == "GFxMovieInfo" && (args.Length < 4 || x.InstancedFullPath.Equals(args[3], StringComparison.OrdinalIgnoreCase))).ToList();
  if (args.Length >= 4 && movies.Count != 1) { Console.WriteLine($"ERROR {args[3]}: {movies.Count} GFxMovieInfo exports of that path"); Environment.Exit(1); }
  foreach (var e in movies) {
    var rd = e.GetProperty<LegendaryExplorerCore.Unreal.ImmutableByteArrayProperty>("RawData");
    var bytes = rd?.Bytes;
    if (bytes == null) { var ap = e.GetProperty<ArrayProperty<ByteProperty>>("RawData"); if (ap != null) bytes = ap.Select(b => b.Value).ToArray(); }
    if (bytes == null) continue;
    var outp = Writable(Path.Combine(args[2], e.ObjectName.Instanced + ".gfx"));
    File.WriteAllBytes(outp, bytes);
    Console.WriteLine($"{e.UIndex}\t{e.InstancedFullPath}\t{bytes.Length}");
  }
} else if (cmd == "reflect") {
  // reflect <namespacePrefix>: public types and members of LEC in that namespace
  var asm = typeof(MEPackageHandler).Assembly;
  foreach (var t in asm.GetTypes().Where(t => t.FullName.Contains(args[1])).OrderBy(t => t.FullName)) {
    Console.WriteLine("T " + t.FullName + (t.BaseType != null ? " : " + t.BaseType.Name : ""));
    foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.DeclaredOnly))
      if (m is System.Reflection.MethodInfo mi && !mi.IsSpecialName) Console.WriteLine("   M " + (mi.IsStatic?"static ":"") + mi.ReturnType.Name + " " + mi.Name + "(" + string.Join(", ", mi.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name)) + ")");
      else if (m is System.Reflection.FieldInfo fi) Console.WriteLine("   F " + fi.FieldType.Name + " " + fi.Name);
      else if (m is System.Reflection.PropertyInfo pi) Console.WriteLine("   P " + pi.PropertyType.Name + " " + pi.Name);
  }
} else if (cmd == "buildsound") {
  // buildsound <donorPcc> <donorCueName> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCueName> <wavTitle> [volume] [--loop] [--unique-wave]
  // Adds the cue to outPcc (created if missing); only the waves under the new cue get the Ogg.
  var (donorPath, donorCue, oggPath) = (args[1], args[2], args[3]);
  int samples = int.Parse(args[4]), channels = int.Parse(args[5]), rate = int.Parse(args[6]);
  var (outPath, newName, title) = (Writable(args[7]), args[8], args[9]);
  // A flag this lectool does not know (a build script newer than the toolkit) must stop the build, not be skipped.
  foreach (var f in args.Skip(10).Where(f => f.StartsWith("--") && f is not ("--loop" or "--unique-wave"))) { Console.WriteLine($"ERROR unknown option {f}"); Environment.Exit(1); }
  // Read before any file is opened. float.Parse took "0,5" for 5: a comma is a thousands separator for it.
  float volume = 1;
  if (args.Length > 10 && !MeleMcp.Core.NumberText.TryFloat(args[10], out volume)) { Console.WriteLine($"ERROR volume '{args[10]}' is not a number such as 0.5"); Environment.Exit(1); }
  NotAnInput(outPath, donorPath);
  using var donor = MEPackageHandler.OpenMEPackage(donorPath);
  var cue = donor.Exports.Single(x => x.ClassName == "SoundCue" && x.ObjectName.Instanced == donorCue);
  var madeNew = !File.Exists(outPath);
  if (madeNew) NewPackage(outPath, MEGame.LE1);
  using var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  var report = LegendaryExplorerCore.Packages.CloningImportingAndRelinking.EntryExporter.ExportExportToPackage(cue, pkg, out var ported, null, null);
  Relink(report);
  // Nothing is saved yet: a package this run made new goes again, an existing one stays as it was.
  if (relinkErrors > 0) { if (madeNew) { pkg.Dispose(); File.Delete(outPath); } Environment.Exit(1); }
  var newCue = (ExportEntry)ported;
  newCue.ObjectName = NameReference.FromInstancedString(newName);
  newCue.Parent = null; // top level: <package>.<newName>, not under the donor's "snd_gui" group
  if (args.Length > 10) newCue.WriteProperty(new FloatProperty(volume, "VolumeMultiplier"));
  newCue.RemoveProperty("PitchMultiplier"); // a donor's pitch (c_snd_guiswish has 2) is not ours
  if (args.Contains("--loop")) {
    // cue -> SoundNodeLooping (indefinite) -> the donor's first node, as snd_gui.c_txt_scroll does
    var firstNode = newCue.GetProperty<ObjectProperty>("FirstNode");
    var loopNode = ExportCreator.CreateExport(pkg, "SoundNodeLooping", "SoundNodeLooping", newCue, null, true, false, null, null);
    loopNode.WriteProperty(new BoolProperty(true, "bLoopIndefinitely"));
    loopNode.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(firstNode.Value) }, "ChildNodes"));
    newCue.WriteProperty(new ObjectProperty(loopNode, "FirstNode"));
  }
  foreach (var x in pkg.Exports) x.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  var ogg = File.ReadAllBytes(oggPath);
  double seconds = samples / (double)rate;
  var cueWaves = new List<ExportEntry>();
  var pending = new Stack<ExportEntry>();
  if (newCue.GetProperty<ObjectProperty>("FirstNode")?.ResolveToEntry(pkg) is ExportEntry first) pending.Push(first);
  while (pending.Count > 0) {
    var node = pending.Pop();
    if (node.ClassName == "SoundNodeWave") { cueWaves.Add(node); continue; }
    foreach (var child in node.GetProperty<ArrayProperty<ObjectProperty>>("ChildNodes") ?? new ArrayProperty<ObjectProperty>("ChildNodes"))
      if (child.ResolveToEntry(pkg) is ExportEntry c) pending.Push(c);
  }
  // An LE1 wave is named <bank>:<title>, and the game plays the sound of its ISACT content index whose title is the
  // part after the colon: that part must stay the donor's. --unique-wave puts the new cue's name before the colon. Under
  // the donor's name, the next cue built from the same donor would get this wave back from the porter (an export of
  // that path is already in outPcc) instead of a copy of its own; so build the one cue that keeps the donor's name last.
  if (args.Contains("--unique-wave"))
    foreach (var w in cueWaves) {
      var name = w.ObjectName.Instanced;
      w.ObjectName = NameReference.FromInstancedString($"{newName}:{name[(name.IndexOf(':') + 1)..]}");
    }
  foreach (var w in cueWaves) {
    var bin = ObjectBinary.From<SoundNodeWave>(w);
    var banks = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
    var chunks = banks.ISBBank.GetAllBankChunks();
    var sinf = chunks.OfType<SampleInfoBankChunk>().Single();
    sinf.TimeLength = (int)Math.Round(seconds * 1000); sinf.SamplesPerSecond = rate; sinf.ByteLength = samples * channels * 2; sinf.BitsPerSample = 16;
    chunks.OfType<LegendaryExplorerCore.Sound.ISACT.ChannelBankChunk>().Single().ChannelCount = channels;
    var cmpi = chunks.OfType<CompressionInfoBankChunk>().Single(); cmpi.TotalSize = ogg.Length;
    chunks.Single(c => c.ChunkName == "data").RawData = ogg;
    bin.RawData = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.SerializePairedBanks(banks);
    w.WriteBinary(bin);
    w.WriteProperty(new FloatProperty((float)seconds, "Duration"));
    Console.WriteLine($"wave {w.InstancedFullPath}: raw={bin.RawData.Length} ogg={ogg.Length} ms={sinf.TimeLength} bytes={sinf.ByteLength} ch={channels}");
  }
  SavePackage(pkg);
  foreach (var prop in newCue.GetProperties()) Console.WriteLine($"cue prop {prop.Name.Instanced} = {(prop is FloatProperty f ? f.Value.ToString() : prop is ObjectProperty o ? o.Value.ToString() : prop is NameProperty n ? n.Value.Instanced : prop.GetType().Name)}");
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "portmesh") {
  // portmesh <srcPcc> <srcUIndex> <pcc> <newName>: ports a StaticMesh with its materials, textures and the
  // materials' local shaders to the top level of an existing package
  if (args.Length != 5) { Console.WriteLine("ERROR usage: portmesh <srcPcc> <srcUIndex> <pcc> <newName>"); Environment.Exit(1); }
  using var src = MEPackageHandler.OpenMEPackage(args[1]);
  var mesh = src.GetUExport(int.Parse(args[2]));
  using var pkg = MEPackageHandler.OpenMEPackage(Writable(args[3]), forceLoadFromDisk: true);
  var report = LegendaryExplorerCore.Packages.CloningImportingAndRelinking.EntryExporter.ExportExportToPackage(mesh, pkg, out var ported, null, null);
  Relink(report);
  if (relinkErrors > 0) Environment.Exit(1);
  var newMesh = (ExportEntry)ported;
  newMesh.ObjectName = NameReference.FromInstancedString(args[4]);
  newMesh.Parent = null;
  foreach (var x in pkg.Exports) x.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  var portedPaths = pkg.Exports.Where(x => x.ClassName == "Material").Select(x => x.InstancedFullPath).ToHashSet();
  var srcMats = src.Exports.Where(x => x.ClassName == "Material" && portedPaths.Contains(x.InstancedFullPath)).ToList();
  var shaders = LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetLocalShadersForMaterials(srcMats, null);
  Console.WriteLine($"materials {srcMats.Count}: {string.Join(", ", srcMats.Select(m => m.InstancedFullPath))}; local shader maps {shaders?.MaterialShaderMaps.Count ?? 0}, shaders {shaders?.Shaders.Count ?? 0}");
  if (shaders != null) LegendaryExplorerCore.Shaders.ShaderCacheManipulator.AddShadersToFile(pkg, shaders);
  SavePackage(pkg);
  foreach (var b in LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetBrokenMaterials(pkg, null)) Console.WriteLine("BROKEN material " + b);
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{x.DataSize}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "portfx") {
  // portfx <srcPcc> <srcUIndex> <pcc> <newName>: like portmesh, for any export (a particle system) and from another
  // game. An object it needs that the target game can't load, such as an LE3 material that only LE3's SFXGame.pcc holds,
  // is first copied from the source game's file that holds it, at the same path, so the export links to the copy.
  // LEC copies shaders between LE1 and LE3 only: from LE2 the materials come out broken.
  if (args.Length != 5) { Console.WriteLine("ERROR usage: portfx <srcPcc> <srcUIndex> <pcc> <newName>"); Environment.Exit(1); }
  using var src = MEPackageHandler.OpenMEPackage(args[1]);
  if (src.Game == MEGame.LE2) LE2Directory.ReloadDefaultGamePath(); else if (src.Game == MEGame.LE3) LE3Directory.ReloadDefaultGamePath();
  var root = src.GetUExport(int.Parse(args[2]));
  var cache = new PackageCache();
  bool Loadable(ImportEntry i) => i.ClassName is "Class" or "Package" || EntryImporter.CanResolveImport(i, cache);
  // Pass 1, never saved: the imports the port adds that the target game can't load.
  List<string> missing;
  using (var dry = MEPackageHandler.OpenMEPackage(Writable(args[3]), forceLoadFromDisk: true)) {
    var before = dry.Imports.Select(i => i.InstancedFullPath).ToHashSet();
    EntryExporter.ExportExportToPackage(root, dry, out _, cache, null);
    missing = dry.Imports.Where(i => !before.Contains(i.InstancedFullPath) && !Loadable(i)).Select(i => i.InstancedFullPath).ToList();
  }
  using var pkg = MEPackageHandler.OpenMEPackage(Writable(args[3]), forceLoadFromDisk: true);
  var importsBefore = pkg.Imports.Select(i => i.InstancedFullPath).ToHashSet();
  foreach (var path in missing) {
    var imp = src.Imports.FirstOrDefault(i => i.InstancedFullPath == path);
    var dep = imp == null ? null : EntryImporter.ResolveImport(imp, cache);
    if (dep == null) { Console.WriteLine($"ERROR {path}: {pkg.Game} can't load it, and no {src.Game} file holds it"); Environment.Exit(1); }
    Relink(EntryExporter.ExportExportToPackage(dep, pkg, out _, cache, null));
    Console.WriteLine($"copied {path} from {Path.GetFileName(dep.FileRef.FilePath)}");
  }
  var fxReport = EntryExporter.ExportExportToPackage(root, pkg, out var portedFx, cache, null);
  Relink(fxReport);
  if (relinkErrors > 0) Environment.Exit(1);
  var newFx = (ExportEntry)portedFx;
  newFx.ObjectName = NameReference.FromInstancedString(args[4]);
  newFx.Parent = null;
  foreach (var x in pkg.Exports) x.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  if (src.Game == pkg.Game) {
    var portedPaths = pkg.Exports.Where(x => x.ClassName == "Material").Select(x => x.InstancedFullPath).ToHashSet();
    var srcMats = src.Exports.Where(x => x.ClassName == "Material" && portedPaths.Contains(x.InstancedFullPath)).ToList();
    var shaders = LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetLocalShadersForMaterials(srcMats, null);
    if (shaders != null) LegendaryExplorerCore.Shaders.ShaderCacheManipulator.AddShadersToFile(pkg, shaders);
  }
  var unloadable = pkg.Imports.Where(i => !importsBefore.Contains(i.InstancedFullPath) && !Loadable(i)).ToList();
  foreach (var i in unloadable) Console.WriteLine($"ERROR import {i.InstancedFullPath}: {pkg.Game} can't load it");
  if (unloadable.Count > 0) Environment.Exit(1);
  // Checked before the save: a port that fails leaves the package as it was.
  var broken = LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetBrokenMaterials(pkg, null).ToList();
  foreach (var b in broken) Console.WriteLine("ERROR broken material " + b);
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{x.DataSize}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
  if (broken.Count > 0) Environment.Exit(1);
  SavePackage(pkg);
} else if (cmd == "addtexture") {
  // addtexture <pcc> <name> <png> [--argb]: a new top-level texture in an existing package, made from a PNG: DXT5 (with
  // --argb uncompressed, as the vanilla UI gradients are: DXT5 bands dark colours), one mip, not sRGB, TEXTUREGROUP_UI,
  // NeverStream, stored in the package (as the vanilla UI textures are)
  var argb = args.Length == 5 && args[4] == "--argb";
  if (args.Length != 4 && !argb) { Console.WriteLine("ERROR usage: addtexture <pcc> <name> <png> [--argb]"); Environment.Exit(1); }
  using var png = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(args[3]);
  var (w, h) = (png.Width, png.Height);
  var px = new byte[w * h * 4];
  png.CopyPixelDataTo(px);
  using var pkg = MEPackageHandler.OpenMEPackage(Writable(args[1]), forceLoadFromDisk: true);
  if (pkg.FindExport(args[2]) != null) { Console.WriteLine($"ERROR {args[2]}: already in {Path.GetFileName(args[1])}"); Environment.Exit(1); }
  var tex = Texture2D.CreateTexture(pkg, new NameReference(args[2]), w, h, argb ? LegendaryExplorerCore.Textures.PixelFormat.ARGB : LegendaryExplorerCore.Textures.PixelFormat.DXT5, false, null, new NameReference("TEXTUREGROUP_UI"));
  tex.ObjectFlags |= LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.Public | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForClient
    | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForServer | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForEdit;
  var img = LegendaryExplorerCore.Textures.Image.LoadFromRaw(px, LegendaryExplorerCore.Textures.PixelFormat.ARGB, w, h);
  foreach (var m in new Texture2D(tex).Replace(img, tex.GetProperties(), isPackageStored: true)) Console.WriteLine("note: " + m);
  // LEC's Replace drops NeverStream below 6 mips, as in gfxpackage. CreateTexture leaves the original size out, and
  // Canvas.DrawTile divides by it.
  var tp = tex.GetProperties();
  tp.AddOrReplaceProp(new BoolProperty(true, "NeverStream"));
  tp.AddOrReplaceProp(new BoolProperty(false, "SRGB"));
  tp.AddOrReplaceProp(new IntProperty(w, "OriginalSizeX"));
  tp.AddOrReplaceProp(new IntProperty(h, "OriginalSizeY"));
  if (argb) tp.AddOrReplaceProp(new BoolProperty(true, "CompressionNone"));
  tex.WriteProperties(tp);
  SavePackage(pkg);
  Console.WriteLine($"{tex.UIndex}\t{tex.InstancedFullPath}\t{w}x{h}\t{tex.DataSize} bytes");
} else if (cmd == "tlkpackage") {
  // tlkpackage <outPcc> <strings.tsv> [--ids-from <other.tsv>]: one language of a mod's GlobalTlk, in the layout of
  // LE1CP's files: the exports GlobalTlk_tlk (female player) and GlobalTlk_tlk_M (male player), the same strings in both,
  // public and marked as localized. With --ids-from the file must have exactly the ids of that other file, so no
  // language can lack a string. Reads the package back and compares it with the input.
  var idsFrom = args.Length == 5 && args[3] == "--ids-from";
  if (args.Length != 3 && !idsFrom) { Console.WriteLine("ERROR usage: tlkpackage <outPcc> <strings.tsv> [--ids-from <other.tsv>]"); Environment.Exit(1); }
  var strings = ReadStrings(args[2]);
  if (idsFrom) {
    var wanted = ReadStrings(args[4]);
    var missing = wanted.Keys.Except(strings.Keys).OrderBy(i => i).ToList();
    var extra = strings.Keys.Except(wanted.Keys).OrderBy(i => i).ToList();
    if (missing.Count + extra.Count > 0) {
      Console.WriteLine($"ERROR {Path.GetFileName(args[2])}: missing ids [{string.Join(", ", missing)}], ids not in {Path.GetFileName(args[4])} [{string.Join(", ", extra)}]");
      Environment.Exit(1);
    }
  }
  var outPath = Writable(args[1]);
  NotAnInput(outPath, idsFrom ? new[] { args[2], args[4] } : new[] { args[2] });
  if (File.Exists(outPath)) File.Delete(outPath);
  NewPackage(outPath, MEGame.LE1);
  string[] tlkNames = { "GlobalTlk_tlk", "GlobalTlk_tlk_M" };
  using (var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true)) {
    foreach (var name in tlkNames) {
      var exp = pkg.CreateExport(new NameReference(name), "BioTlkFile", indexed: false);
      exp.ObjectFlags = LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.Public | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.Standalone
        | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForClient | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForServer
        | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LoadForEdit | LegendaryExplorerCore.Unreal.UnrealFlags.EObjectFlags.LocalizedResource;
      var huffman = new LegendaryExplorerCore.TLK.ME1.HuffmanCompression();
      huffman.LoadInputData(strings.Select(s => new LegendaryExplorerCore.TLK.TLKStringRef(s.Key, s.Value)).ToList());
      huffman.SerializeTalkfileToExport(exp);
    }
    SavePackage(pkg);
  }
  using (var back = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true)) {
    foreach (var name in tlkNames) {
      var read = new LegendaryExplorerCore.TLK.ME1.ME1TalkFile(back.FindExport(name)).StringRefs.ToDictionary(r => r.StringID, r => r.Data);
      var wrong = strings.Where(s => !read.TryGetValue(s.Key, out var t) || t != s.Value).Select(s => s.Key).ToList();
      if (read.Count != strings.Count || wrong.Count > 0) {
        Console.WriteLine($"ERROR {Path.GetFileName(outPath)} {name}: read {read.Count} strings of {strings.Count}, wrong ids [{string.Join(", ", wrong)}]");
        Environment.Exit(1);
      }
    }
  }
  Console.WriteLine($"{Path.GetFileName(outPath)}\t{strings.Count} strings\t{strings.Keys.Min()}-{strings.Keys.Max()}");
} else if (cmd == "meshinfo") {
  // meshinfo <pcc> <uIndex>: a StaticMesh's bounds, LODs and element materials
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var e = pcc.GetUExport(int.Parse(args[2]));
  var sm = ObjectBinary.From<StaticMesh>(e);
  var b = sm.Bounds;
  Console.WriteLine($"{e.InstancedFullPath} origin=({b.Origin.X},{b.Origin.Y},{b.Origin.Z}) extent=({b.BoxExtent.X},{b.BoxExtent.Y},{b.BoxExtent.Z}) radius={b.SphereRadius}");
  for (int l = 0; l < sm.LODModels.Length; l++) {
    var lod = sm.LODModels[l];
    Console.WriteLine($"LOD{l} verts={lod.NumVertices} elements={lod.Elements.Length}");
    foreach (var el in lod.Elements) Console.WriteLine($"  mat={(el.Material == 0 ? "None" : pcc.GetEntry(el.Material).InstancedFullPath)} tris={el.NumTriangles}");
  }
} else if (cmd == "texpng") {
  // texpng <pcc> <uIndex|path> <out.png>: a Texture2D's top mip as PNG
  if (args.Length != 4) { Console.WriteLine("ERROR usage: texpng <pcc> <uIndex|path> <out.png>"); Environment.Exit(1); }
  var pngPath = Writable(args[3]);
  NotAnInput(pngPath, args[1]);
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var tex = int.TryParse(args[2], out var ui) ? pcc.GetUExport(ui) : pcc.FindExport(args[2]);
  if (tex is not { ClassName: "Texture2D" }) { Console.WriteLine($"ERROR {args[2]}: not a Texture2D export of {Path.GetFileName(args[1])}"); Environment.Exit(1); }
  Console.WriteLine(new LegendaryExplorerCore.Unreal.Classes.Texture2D(tex).ExportToPNG(pngPath));
} else if (cmd == "cubeshrink") {
  // cubeshrink <pcc> <cubePath> <size> <DXT1|DXT5>: makes the six faces of a TextureCube smaller, in place. Each face's
  // top mip is halved with a 2x2 box filter down to <size> (what the cooker does when a LOD group cuts top mips), then
  // stored in the package in the new format with a full mip chain. The engine builds the cube from the faces, so all six
  // must already match in size and format, and they match again after. Reads the package back and checks every face.
  if (args.Length != 5 || !int.TryParse(args[3], out var size) || args[4] is not ("DXT1" or "DXT5")) {
    Console.WriteLine("ERROR usage: cubeshrink <pcc> <cubePath> <size> <DXT1|DXT5>"); Environment.Exit(1); return;
  }
  var fmt = args[4] == "DXT1" ? LegendaryExplorerCore.Textures.PixelFormat.DXT1 : LegendaryExplorerCore.Textures.PixelFormat.DXT5;
  string[] faceProps = { "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ" };
  var pkgPath = Writable(args[1]);
  int wantMips = 1;
  using (var pkg = MEPackageHandler.OpenMEPackage(pkgPath, forceLoadFromDisk: true)) {
    var cube = pkg.FindExport(args[2]);
    if (cube is not { ClassName: "TextureCube" }) { Console.WriteLine($"ERROR {args[2]}: not a TextureCube export of {Path.GetFileName(args[1])}"); Environment.Exit(1); return; }
    var faces = faceProps.Select(n => cube.GetProperty<ObjectProperty>(n)?.ResolveToEntry(pkg) as ExportEntry).ToList();
    if (faces.Any(f => f is not { ClassName: "Texture2D" }) || faces.Distinct().Count() != 6) {
      Console.WriteLine($"ERROR {args[2]}: needs six different Texture2D exports of this package as faces"); Environment.Exit(1); return;
    }
    var texs = faces.Select(f => new Texture2D(f)).ToList();
    var top = texs[0].GetTopMip();
    if (texs.Any(t => t.GetTopMip().width != top.width || t.GetTopMip().height != top.height || t.TextureFormat != texs[0].TextureFormat)) {
      Console.WriteLine($"ERROR {args[2]}: the faces differ in size or format"); Environment.Exit(1); return;
    }
    if (top.width != top.height || size < 4 || (size & (size - 1)) != 0 || size > top.width) {
      Console.WriteLine($"ERROR size {size}: must be a power of two from 4 to the faces' {top.width}x{top.height}, and the faces square"); Environment.Exit(1); return;
    }
    for (int s = size; s > 1; s >>= 1) wantMips++;
    for (int i = 0; i < 6; i++) {
      var before = faces[i].DataSize;
      var px = texs[i].ToImage(LegendaryExplorerCore.Textures.PixelFormat.ARGB).mipMaps.First(m => m.origWidth == size).data;
      // DXT1 keeps one bit of alpha, and its encoder turns every texel under half alpha into black. A face whose alpha
      // follows its brightness, as StarsCube01's does, came out all black: DXT1 faces are made opaque (BGRA bytes).
      if (fmt == LegendaryExplorerCore.Textures.PixelFormat.DXT1) for (int b = 3; b < px.Length; b += 4) px[b] = 255;
      var img = LegendaryExplorerCore.Textures.Image.LoadFromRaw(px, LegendaryExplorerCore.Textures.PixelFormat.ARGB, size, size);
      foreach (var m in texs[i].Replace(img, faces[i].GetProperties(), isPackageStored: true, forcedNewFormat: fmt, forceMipping: true)) Console.WriteLine("note: " + m);
      // Replace leaves these: the face has mips now, and its original size is the stored one. It keeps NeverStream only
      // from 6 mips up, and these mips are in the package, so the engine must never look for them elsewhere.
      var p = faces[i].GetProperties();
      p.RemoveNamedProperty("CompressionNoMipmaps");
      p.AddOrReplaceProp(new IntProperty(size, "OriginalSizeX"));
      p.AddOrReplaceProp(new IntProperty(size, "OriginalSizeY"));
      p.AddOrReplaceProp(new BoolProperty(true, "NeverStream"));
      faces[i].WriteProperties(p);
      Console.WriteLine($"{faces[i].UIndex}\t{faceProps[i]}\t{faces[i].InstancedFullPath}\t{before} -> {faces[i].DataSize} bytes");
    }
    SavePackage(pkg);
  }
  using (var back = MEPackageHandler.OpenMEPackage(pkgPath, forceLoadFromDisk: true)) {
    var cube = back.FindExport(args[2]);
    long total = 0;
    foreach (var n in faceProps) {
      var f = (ExportEntry)cube.GetProperty<ObjectProperty>(n).ResolveToEntry(back);
      var t = new Texture2D(f);
      var tm = t.GetTopMip();
      var stored = t.Mips.All(m => m.storageType is StorageTypes.pccUnc or StorageTypes.pccLZO or StorageTypes.pccZlib or StorageTypes.pccOodle);
      if (tm.width != size || tm.height != size || t.Mips.Count != wantMips || t.TextureFormat != "PF_" + args[4] || !stored) {
        Console.WriteLine($"ERROR {f.InstancedFullPath}: read back {tm.width}x{tm.height} {t.TextureFormat}, {t.Mips.Count} mips (want {wantMips}), all in the package: {stored}");
        Environment.Exit(1);
      }
      total += f.DataSize;
    }
    Console.WriteLine($"{cube.InstancedFullPath}\t6 faces {size}x{size} PF_{args[4]}, {wantMips} mips\t{total} bytes");
  }
} else if (cmd == "forcepackage") {
  // forcepackage <pcc> <package> [--move-out <path>]...: marks a top-level Package export and every export under it
  // ForcedExport, as the game's own levels store shared art. Each object then loads under its own path (BIOG_NOR.Base.X,
  // not <file>.BIOG_NOR.Base.X): the game uses the copy already in memory when there is one, and a texture override
  // (M3's .m3to, which matches a texture's full path) finds it. --move-out first moves an export and everything under
  // it to the top level of the file, not forced: for what the mod changed, which must not stand in for the game's copy.
  var moveOut = new List<string>();
  bool usageOk = args.Length >= 3;
  for (int i = 3; i < args.Length && usageOk; i++) {
    if (args[i] == "--move-out" && i + 1 < args.Length) moveOut.Add(args[++i]); else usageOk = false;
  }
  if (!usageOk) { Console.WriteLine("ERROR usage: forcepackage <pcc> <package> [--move-out <path>]..."); Environment.Exit(1); return; }
  var pkgPath = Writable(args[1]);
  var forced = LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  using (var pkg = MEPackageHandler.OpenMEPackage(pkgPath, forceLoadFromDisk: true)) {
    var top = pkg.FindExport(args[2]);
    if (top is not { ClassName: "Package", Parent: null }) { Console.WriteLine($"ERROR {args[2]}: not a top-level Package export of {Path.GetFileName(args[1])}"); Environment.Exit(1); return; }
    foreach (var path in moveOut) {
      var e = pkg.FindExport(path);
      if (e == null || !e.InstancedFullPath.StartsWith(top.InstancedFullPath + ".")) { Console.WriteLine($"ERROR {path}: not an export under {args[2]}"); Environment.Exit(1); return; }
      if (pkg.Exports.Any(x => x.Parent == null && x.ObjectName == e.ObjectName)) { Console.WriteLine($"ERROR {path}: the top level already has a {e.ObjectName.Instanced}"); Environment.Exit(1); return; }
      e.Parent = null;
      foreach (var d in e.GetAllDescendants().OfType<ExportEntry>().Append(e)) d.ExportFlags &= ~forced;
    }
    foreach (var d in top.GetAllDescendants().OfType<ExportEntry>().Append(top)) d.ExportFlags |= forced;
    SavePackage(pkg);
  }
  using (var back = MEPackageHandler.OpenMEPackage(pkgPath, forceLoadFromDisk: true)) {
    var top = back.FindExport(args[2]);
    var below = top.GetAllDescendants().OfType<ExportEntry>().ToList();
    if (!top.IsForcedExport || below.Any(d => !d.IsForcedExport)) { Console.WriteLine($"ERROR {args[2]}: read back with exports not forced"); Environment.Exit(1); }
    foreach (var x in back.Exports.Where(e => e.Parent == null)) Console.WriteLine($"top\t{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{(x.IsForcedExport ? "forced" : "not forced")}\t{x.GetAllDescendants().Count} below");
  }
} else if (cmd == "coalesced") {
  // coalesced <bin> <outDir> [filter]: writes every ini of a Coalesced file (LE1, LE2, or LE3's Coalesced.bin and DLC
  // Default_<DLC>.bin) to outDir and prints the lines that contain filter, with tabs shown as \t (ConfigDelta removals
  // must match a line exactly). LE3 files hold a type per value: written as the key prefixes LEC's ini reader knows,
  // > overwrite (type 0), ! clear (1), none add (2), + add unique (3), - remove (4); a line break in a value as \n.
  using var fs = File.OpenRead(args[1]);
  var magic = new byte[4];
  fs.ReadExactly(magic);
  fs.Position = 0;
  var files = new List<(string Name, string Text)>();
  if (BitConverter.ToInt32(magic) == LegendaryExplorerCore.Coalesced.CoalescedConverter.CoalescedMagicNumber) {
    var coal = new LegendaryExplorerCore.Coalesced.CoalescedFileXml();
    coal.Deserialize(fs);
    string[] prefixes = { ">", "!", "", "+", "-" };
    foreach (var file in coal.Files) {
      var sb = new System.Text.StringBuilder();
      foreach (var section in file.Sections) {
        if (sb.Length > 0) sb.Append("\r\n");
        sb.Append($"[{section.Key}]\r\n");
        foreach (var key in section.Value)
          foreach (var value in key.Value)
            sb.Append($"{prefixes[value.Type]}{key.Key}={(value.Type == 1 ? "CLEAR" : value.Value)?.Replace("\r", "\\r").Replace("\n", "\\n")}\r\n");
      }
      files.Add((file.Name.Replace('\\', '/').Split('/').Last(), sb.ToString()));
    }
  } else {
    foreach (var kv in LegendaryExplorerCore.Coalesced.CoalescedConverter.DecompileLE1LE2ToMemory(fs, Path.GetFileName(args[1])))
      files.Add((kv.Key.Replace('\\', '/').Split('/').Last(), kv.Value.ToString()));
  }
  Directory.CreateDirectory(Writable(args[2]));
  foreach (var (name, text) in files) {
    File.WriteAllText(Path.Combine(args[2], name), text);
    if (args.Length < 4) continue;
    foreach (var line in text.Split('\n').Where(l => l.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
      Console.WriteLine(name + ": " + line.TrimEnd('\r').Replace("\t", "\\t"));
  }
} else if (cmd == "compileclass") {
  // compileclass <outPcc> <packageName> <Class.uc>...: new LE1 package with the classes under a Package export, and a
  // CombinedStartupReferencer that keeps them loaded when the file is an AutoLoad GlobalPackage (LE1CP's layout).
  // --pkg=<name> puts the .uc files after it into another Package export, so one file can hold a copy of another mod's
  // base class next to our subclasses. LEC marks each Package export ForcedExport (found in memory by its own path);
  // --unforce=<name> clears that, so its classes load from this file as <file>.<name>.<class> (LE1CP Mod Settings Menu
  // pages, which the menu loads by path when it opens them). --no-referencer leaves out the referencer (a file that is
  // not a GlobalPackage).
  var outPath = Writable(args[1]);
  NotAnInput(outPath, args.Skip(3).Where(a => !a.StartsWith("--")).ToArray());
  if (File.Exists(outPath)) File.Delete(outPath);
  NewPackage(outPath, MEGame.LE1);
  using var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  var looseByPackage = new List<LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.LooseClassPackage>();
  var unforce = new List<string>();
  var currentClasses = new List<LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.LooseClass>();
  looseByPackage.Add(new(args[2], currentClasses));
  foreach (var a in args.Skip(3)) {
    if (a.StartsWith("--pkg=")) { currentClasses = new(); looseByPackage.Add(new(a["--pkg=".Length..], currentClasses)); }
    else if (a.StartsWith("--unforce=")) unforce.Add(a["--unforce=".Length..]);
    else if (a == "--no-referencer") continue;
    else currentClasses.Add(new(Path.GetFileNameWithoutExtension(a), File.ReadAllText(a), a));
  }
  looseByPackage.RemoveAll(p => p.Classes.Count == 0);
  using var cache = new PackageCache();
  var usop = new LegendaryExplorerCore.UnrealScript.UnrealScriptOptionsPackage { Cache = cache };
  var log = LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.CompileLooseClasses(pkg, looseByPackage, usop);
  foreach (var m in log.Content) Console.WriteLine("log: " + m);
  // The output was deleted and made new above: a failed compile must not leave the empty package where a good one was.
  if (log.HasErrors) { Console.WriteLine("COMPILE FAILED"); pkg.Dispose(); File.Delete(outPath); Environment.Exit(1); }
  foreach (var name in unforce) {
    var p = pkg.Exports.SingleOrDefault(x => x.ClassName == "Package" && x.Parent == null && x.ObjectName.Instanced == name)
      ?? throw new Exception($"--unforce: no top-level Package export {name}");
    foreach (var d in p.GetAllDescendants().OfType<ExportEntry>().Prepend(p))
      d.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  }
  if (!args.Contains("--no-referencer")) {
    var referencer = ExportCreator.CreateExport(pkg, "CombinedStartupReferencer", "ObjectReferencer", null, null, false, false, null, null);
    var refs = new ArrayProperty<ObjectProperty>("ReferencedObjects");
    foreach (var c in pkg.Exports.Where(x => x.ClassName == "Class")) refs.Add(new ObjectProperty(c));
    referencer.WriteProperty(refs);
  }
  SavePackage(pkg);
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{x.DataSize}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "addactor") {
  // addactor <levelPcc> <package> <class>: places one actor of a script class at the origin of the level, with its class
  // defaults. The class is imported from <package>, which must already be in memory (an AutoLoad GlobalPackage).
  if (args.Length != 4) { Console.WriteLine("ERROR usage: addactor <levelPcc> <package> <class>"); Environment.Exit(1); }
  using var pcc = MEPackageHandler.OpenMEPackage(Writable(args[1]), forceLoadFromDisk: true);
  var (pkgName, clsName) = (args[2], args[3]);
  ImportEntry Imp(IEntry parent, string name, string className, string packageFile) {
    var found = pcc.Imports.FirstOrDefault(i => i.ObjectName.Instanced == name && i.Parent == parent);
    if (found != null) return found;
    var imp = new ImportEntry(pcc, parent, NameReference.FromInstancedString(name)) { ClassName = className, PackageFile = packageFile };
    pcc.AddImport(imp);
    return imp;
  }
  var pkgImp = Imp(null, pkgName, "Package", "Core");
  var clsImp = Imp(pkgImp, clsName, "Class", "Core");
  var defImp = Imp(pkgImp, "Default__" + clsName, clsName, pkgName);
  var levelExp = pcc.Exports.Single(e => e.ClassName == "Level");
  var worldInfo = pcc.Exports.First(e => e.Parent == levelExp && e.HasStack);
  // Same script stack as the level's WorldInfo, pointing at our class instead.
  var stack = worldInfo.GetPrePropBinary().ToArray();
  BitConverter.GetBytes(clsImp.UIndex).CopyTo(stack, 0);
  BitConverter.GetBytes(clsImp.UIndex).CopyTo(stack, 4);
  var actor = new ExportEntry(pcc, levelExp, new NameReference(clsName, 1), stack, new PropertyCollection(), null, false);
  actor.Class = clsImp;
  actor.Archetype = defImp;
  actor.ObjectFlags = worldInfo.ObjectFlags;
  pcc.AddExport(actor);
  var level = ObjectBinary.From<Level>(levelExp);
  level.Actors.Add(actor.UIndex);
  levelExp.WriteBinary(level);
  SavePackage(pcc);
  Console.WriteLine($"{actor.UIndex}\t{actor.InstancedFullPath}\tclass={clsImp.InstancedFullPath}\tactors={level.Actors.Count}");
} else if (cmd == "bankdump") {
  // bankdump <pcc> <waveNameFilter>: every ICB and ISB chunk of the matching waves with all fields (data chunks by size)
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  foreach (var e in pcc.Exports.Where(x => x.ClassName == "SoundNodeWave" && x.ObjectName.Instanced.Contains(args[2], StringComparison.OrdinalIgnoreCase))) {
    var bin = ObjectBinary.From<SoundNodeWave>(e);
    Console.WriteLine($"== {e.UIndex} {e.InstancedFullPath}");
    var bp = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
    foreach (var (tag, bank) in new[] { ("ICB", bp.ICBBank), ("ISB", bp.ISBBank) })
      foreach (var c in bank.GetAllBankChunks()) {
        if (c.ChunkName == "data") { Console.WriteLine($"  {tag} data {c.RawData?.Length}"); continue; }
        var fields = c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
          .Where(f => f.Name is not ("Parent" or "SubChunks" or "BankSubChunkMap" or "ChunkName")).Select(f => f.Name + "=" + (f.GetValue(c) is byte[] b ? Convert.ToHexString(b) : f.GetValue(c)));
        var props = c.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
          .Where(pp => pp.GetIndexParameters().Length == 0 && pp.Name is not ("Parent" or "SampleData")).Select(pp => { try { var v = pp.GetValue(c); return pp.Name + "=" + (v is byte[] b ? Convert.ToHexString(b) : v); } catch { return pp.Name + "=?"; } });
        Console.WriteLine($"  {tag} {c.ChunkName}: {string.Join(", ", fields.Concat(props))}");
      }
  }
} else if (cmd == "eventflags") {
  // eventflags <pcc>...: per wave, its sound events' info Flags and their tracks' Flags (to find ISACT's loop flag)
  foreach (var path in args.Skip(1)) {
    using var pcc = MEPackageHandler.OpenMEPackage(path);
    foreach (var e in pcc.Exports.Where(x => x.ClassName == "SoundNodeWave")) {
      try {
        var bin = ObjectBinary.From<SoundNodeWave>(e);
        var bp = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
        var chunks = bp.ICBBank.GetAllBankChunks().ToList();
        var infos = chunks.OfType<SoundEventInfoBankChunk>().Select(i => i.Flags.ToString());
        var tracks = chunks.Where(c => c.ChunkName == "sdtf").SelectMany(c => (System.Collections.IEnumerable)c.GetType().GetField("SoundTracks").GetValue(c) as IEnumerable<object> ?? Array.Empty<object>())
          .Select(t => ((ISACTSoundTrack)t).Flags.ToString("X"));
        var types = chunks.OfType<LegendaryExplorerCore.Sound.ISACT.ISACTListBankChunk>().Select(l => l.ObjectType).Where(t => t is "snde" or "sdqu").Distinct();
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(path)}\t{e.ObjectName.Instanced}\t{string.Join("/", types)}\tinfo={string.Join(",", infos)}\ttracks={string.Join(",", tracks)}");
      } catch (Exception ex) { Console.WriteLine($"{e.ObjectName.Instanced}\terror {ex.Message}"); }
    }
  }
} else if (cmd == "preprop") {
  // preprop <pcc> <uIndex>: an export's pre-property bytes (the script stack of a HasStack object) and its class index
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  var e = pcc.GetUExport(int.Parse(args[2]));
  Console.WriteLine($"{e.InstancedFullPath} class={e.idxClass} hasStack={e.HasStack} preprop={Convert.ToHexString(e.GetPrePropBinary())}");
} else if (cmd == "dumpwave") {
  // dumpwave <pcc> <outdir> <nameFilter...>: writes each matching wave's RawData and its ISB data chunk
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  foreach (var e in pcc.Exports.Where(x => x.ClassName == "SoundNodeWave" && args.Skip(3).Any(f => x.ObjectName.Instanced.Contains(f, StringComparison.OrdinalIgnoreCase)))) {
    var bin = ObjectBinary.From<SoundNodeWave>(e);
    var name = e.ObjectName.Instanced.Replace(":", "_");
    File.WriteAllBytes(Writable(Path.Combine(args[2], name + ".raw")), bin.RawData);
    var bp = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
    int i = 0;
    foreach (var c in bp.ISBBank.GetAllBankChunks().Where(c => c.ChunkName == "data")) {
      File.WriteAllBytes(Writable(Path.Combine(args[2], $"{name}.data{i++}.bin")), c.RawData);
    }
    Console.WriteLine($"{e.UIndex}\t{e.InstancedFullPath}\traw={bin.RawData.Length}\tdatachunks={i}");
  }
} else {
  // A build script that calls a command this lectool does not have (an older toolkit) must fail, not pass.
  Console.WriteLine($"ERROR unknown command {cmd}; the commands are listed in tools/lectool/README.md");
  Environment.Exit(1);
}

// A strings file: UTF-8 lines of an id, one tab and the text. Empty lines and lines starting with # are skipped.
static Dictionary<int, string> ReadStrings(string path) {
  var strings = new Dictionary<int, string>();
  var lines = File.ReadAllLines(path, new System.Text.UTF8Encoding(false, true));
  void Fail(int line, string why) { Console.WriteLine($"ERROR {Path.GetFileName(path)} line {line}: {why}"); Environment.Exit(1); }
  for (int i = 0; i < lines.Length; i++) {
    if (lines[i].Length == 0 || lines[i][0] == '#') continue;
    var parts = lines[i].Split('\t');
    if (parts.Length != 2) Fail(i + 1, "not an id, one tab and the text");
    else if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == 0) Fail(i + 1, $"bad id '{parts[0]}'");
    else if (parts[1].Trim().Length == 0) Fail(i + 1, $"no text for {id}");
    else if (!strings.TryAdd(id, parts[1])) Fail(i + 1, $"id {id} twice");
  }
  if (strings.Count == 0) { Console.WriteLine($"ERROR {Path.GetFileName(path)}: no strings"); Environment.Exit(1); }
  return strings;
}
// The cell's value in a cell of the given package (a name cell resolves its name there).
static Bio2DACell CopyCell(Bio2DACell c, IMEPackage pkg) => c.Type switch {
  Bio2DACell.Bio2DADataType.TYPE_INT => new Bio2DACell(c.IntValue),
  Bio2DACell.Bio2DADataType.TYPE_FLOAT => new Bio2DACell(c.FloatValue),
  Bio2DACell.Bio2DADataType.TYPE_NAME => new Bio2DACell(c.NameValue, pkg),
  _ => new Bio2DACell { package = pkg },
};
static bool SameCell(Bio2DACell a, Bio2DACell b) => a.Type == b.Type && a.Type switch {
  Bio2DACell.Bio2DADataType.TYPE_INT => a.IntValue == b.IntValue,
  Bio2DACell.Bio2DADataType.TYPE_FLOAT => BitConverter.SingleToInt32Bits(a.FloatValue) == BitConverter.SingleToInt32Bits(b.FloatValue),
  Bio2DACell.Bio2DADataType.TYPE_NAME => a.NameValue == b.NameValue,    // as LEC and the engine compare names: case aside
  _ => true,
};
// The named rows as `2da` prints a table: a header line, then one tab-separated line per row.
static void PrintRows(Bio2DA t, IEnumerable<string> rows) {
  Console.WriteLine("ROW\t" + string.Join("\t", t.ColumnNames));
  foreach (var n in rows) { int r = t.GetRowIndexByName(n); Console.WriteLine(n + "\t" + string.Join("\t", Enumerable.Range(0, t.ColumnCount).Select(c => t[r, c].DisplayableValue))); }
}

// A property's value as one line of text: an object as its path, a string ref with a $.
static string PropText(Property p, IMEPackage pcc) => p switch {
  ObjectProperty o => o.Value == 0 ? "None" : pcc.GetEntry(o.Value)?.InstancedFullPath ?? $"#{o.Value}?",
  StringRefProperty s => "$" + s.Value,
  IntProperty i => i.Value.ToString(),
  FloatProperty f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
  BoolProperty b => b.Value.ToString(),
  EnumProperty e => e.Value.Instanced,
  NameProperty n => n.Value.Instanced,
  StrProperty s => "\"" + s.Value + "\"",
  _ => p.PropType.ToString(),
};

// The look morphface copies: a BioMorphFace's LOD0 and LOD1 positions, its bones and its BioMaterialOverride's parameters.
class FaceLook {
  public System.Numerics.Vector3[][] Lods;
  public SortedDictionary<string, float[]> Bones = new(StringComparer.OrdinalIgnoreCase);
  public FaceParams Params = new();
  public static MeshBinary BaseHead(ExportEntry face) =>
    face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(face.FileRef) is ExportEntry { ClassName: "SkeletalMesh" } m ? ObjectBinary.From<MeshBinary>(m) : null;
  public static FaceLook Read(ExportEntry face) {
    var look = new FaceLook { Lods = ObjectBinary.From<BmfBinary>(face).LODs.Take(2).ToArray() };
    foreach (var s in face.GetProperty<ArrayProperty<StructProperty>>("m_aFinalSkeleton") ?? Enumerable.Empty<StructProperty>()) {
      var p = s.GetProp<StructProperty>("vPos");
      look.Bones[s.GetProp<NameProperty>("nName").Value.Instanced] = new[] { p.GetProp<FloatProperty>("X").Value, p.GetProp<FloatProperty>("Y").Value, p.GetProp<FloatProperty>("Z").Value };
    }
    if (face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(face.FileRef) is ExportEntry over) look.Params = FaceParams.Read(over, false);
    return look;
  }
  public string Sha1() {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    foreach (var lod in Lods) foreach (var v in lod) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    foreach (var (n, p) in Bones) { w.Write(n.ToLowerInvariant()); foreach (var f in p) w.Write(f); }
    w.Write(Params.Text());
    w.Flush();
    return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(ms.ToArray())).ToLowerInvariant();
  }
}
// Material parameters by name, from a BioMaterialOverride or a material instance; a texture as its object name.
class FaceParams {
  public SortedDictionary<string, float[]> Colors = new(StringComparer.OrdinalIgnoreCase);
  public SortedDictionary<string, float> Scalars = new(StringComparer.OrdinalIgnoreCase);
  public SortedDictionary<string, string> Textures = new(StringComparer.OrdinalIgnoreCase);
  static (string colors, string scalars, string textures, string name, string color, string scalar, string texture) Keys(bool mic) => mic
    ? ("VectorParameterValues", "ScalarParameterValues", "TextureParameterValues", "ParameterName", "ParameterValue", "ParameterValue", "ParameterValue")
    : ("m_aColorOverrides", "m_aScalarOverrides", "m_aTextureOverrides", "nName", "cValue", "sValue", "m_pTexture");
  public static FaceParams Read(ExportEntry e, bool mic) {
    var k = Keys(mic);
    var r = new FaceParams();
    IEnumerable<StructProperty> Items(string array) => e.GetProperty<ArrayProperty<StructProperty>>(array) ?? Enumerable.Empty<StructProperty>();
    string Name(StructProperty s) => s.GetProp<NameProperty>(k.name).Value.Instanced;
    foreach (var s in Items(k.colors)) { var c = s.GetProp<StructProperty>(k.color); r.Colors[Name(s)] = "RGBA".Select(ch => c.GetProp<FloatProperty>(ch.ToString()).Value).ToArray(); }
    foreach (var s in Items(k.scalars)) r.Scalars[Name(s)] = s.GetProp<FloatProperty>(k.scalar).Value;
    foreach (var s in Items(k.textures)) r.Textures[Name(s)] = s.GetProp<ObjectProperty>(k.texture).ResolveToEntry(e.FileRef)?.ObjectName.Instanced ?? "";
    return r;
  }
  // The parameter names only one of the two has. Arrays keep their length, so a look carries over only between equal names.
  public IEnumerable<string> OneSided(FaceParams o) {
    IEnumerable<string> Diff(IEnumerable<string> a, IEnumerable<string> b) =>
      a.Except(b, StringComparer.OrdinalIgnoreCase).Concat(b.Except(a, StringComparer.OrdinalIgnoreCase));
    return Diff(Colors.Keys, o.Colors.Keys).Concat(Diff(Scalars.Keys, o.Scalars.Keys)).Concat(Diff(Textures.Keys, o.Textures.Keys));
  }
  // Sets these values on e, by parameter name. An array keeps its length: a name e lacks is an error.
  public void WriteTo(ExportEntry e, bool mic, Func<string, int> texture, List<string> errors) {
    var k = Keys(mic);
    var props = e.GetProperties();
    StructProperty Find(string array, string name) {
      var s = props.GetProp<ArrayProperty<StructProperty>>(array)?.FirstOrDefault(x => x.GetProp<NameProperty>(k.name).Value.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
      if (s == null) errors.Add($"{e.InstancedFullPath}: no {name} in {array}");
      return s;
    }
    foreach (var (n, v) in Colors) if (Find(k.colors, n)?.GetProp<StructProperty>(k.color) is { } c) foreach (var (ch, f) in "RGBA".Zip(v)) c.GetProp<FloatProperty>(ch.ToString()).Value = f;
    foreach (var (n, v) in Scalars) if (Find(k.scalars, n) is { } s) s.GetProp<FloatProperty>(k.scalar).Value = v;
    foreach (var (n, v) in Textures) if (Find(k.textures, n) is { } s) s.GetProp<ObjectProperty>(k.texture).Value = texture(v);
    e.WriteProperties(props);
  }
  public string Text() => string.Join("\n",
    Colors.Select(c => $"c {c.Key.ToLowerInvariant()} {string.Join(" ", c.Value.Select(BitConverter.SingleToInt32Bits))}")
      .Concat(Scalars.Select(s => $"s {s.Key.ToLowerInvariant()} {BitConverter.SingleToInt32Bits(s.Value)}"))
      .Concat(Textures.Select(t => $"t {t.Key.ToLowerInvariant()} {t.Value.ToLowerInvariant()}")));
}

// actortype's recipe.
class ActorTypeRecipe { public string name { get; set; } public ActorTypeTemplate template { get; set; } public ActorTypeLook look { get; set; } public Dictionary<string, string> set { get; set; } }
class ActorTypeTemplate { public string actorType { get; set; } public List<string> keep { get; set; } }
class ActorTypeLook { public string actorType { get; set; } public Dictionary<string, string> take { get; set; } public string moveUnder { get; set; } public List<string> moveRoots { get; set; } }
// 2dapart's recipe: tables: { <Package.Table>: { rows: { <row>: { from: <row>, set: { <column>: <value> } } } } }
class TwoDaRecipe { public Dictionary<string, TwoDaTable> tables { get; set; } }
class TwoDaTable { public Dictionary<string, TwoDaRow> rows { get; set; } }
class TwoDaRow { public string from { get; set; } public Dictionary<string, string> set { get; set; } }
// One entry of an M3 .m3da manifest (ME3TweaksCore's Bio2DAMergeManifest).
class M3DAEntry { public string comment { get; set; } public string packagefile { get; set; } public string mergepackagefile { get; set; } public List<string> mergetables { get; set; } }

partial class Program { [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] internal static extern bool SetDllDirectoryW(string p); }
[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
delegate int CreateIPSOggDel(byte[] wavedata, uint waveDataLen, [System.Runtime.InteropServices.Out] byte[] dstBuf, uint dstLen, float quality);

static partial class NativeHelp { }
