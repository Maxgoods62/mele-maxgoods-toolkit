using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

// The Legendary Edition folder: MELE_LE_DIR as for mele-mcp (from WSL also set WSLENV=MELE_LE_DIR), else LEC's registry lookup.
var leDir = Environment.GetEnvironmentVariable("MELE_LE_DIR");
if (!string.IsNullOrWhiteSpace(leDir)) LegendaryExplorerCoreLibSettings.Instance.LEDirectory = leDir;
LE1Directory.ReloadDefaultGamePath();
var gameArg = Environment.GetEnvironmentVariable("LECGAME") ?? "LE1";
var game = Enum.Parse<MEGame>(gameArg);
if (game == MEGame.LE2) LE2Directory.ReloadDefaultGamePath(); if (game == MEGame.LE3) LE3Directory.ReloadDefaultGamePath();
LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default, s => Console.Error.WriteLine(s), objectDBsToLoad: new[]{game});
var cmd = args[0];
if (cmd == "ogg") {
  var dllDir = Environment.GetEnvironmentVariable("EXTRADLLDIR"); if (dllDir != null) SetDllDirectoryW(dllDir);
  var h = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ISACTTools.dll"));
  var fp = System.Runtime.InteropServices.NativeLibrary.GetExport(h, "CreateIPSOgg");
  var fn = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<CreateIPSOggDel>(fp);
  var wav = File.ReadAllBytes(args[1]); var outb = new byte[wav.Length];
  int r = fn(wav, (uint)wav.Length, outb, (uint)outb.Length, 0.8f);
  Console.WriteLine($"CreateIPSOgg -> {r} bytes from {wav.Length} wav bytes");
  if (r > 0) File.WriteAllBytes(args[2], outb.Take(r).ToArray());
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
  var outPath = args[3];
  if (File.Exists(outPath)) File.Delete(outPath);
  TwoDaRecipe recipe;
  try { recipe = new YamlDotNet.Serialization.DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<TwoDaRecipe>(File.ReadAllText(args[2])); }
  catch (YamlDotNet.Core.YamlException ex) { Console.WriteLine($"ERROR {Path.GetFileName(args[2])}: {ex.Message}"); Environment.Exit(1); return; }
  using var src = MEPackageHandler.OpenMEPackage(args[1]);
  var inv = System.Globalization.CultureInfo.InvariantCulture;
  var errors = new List<string>();
  var parts = new List<string>();
  MEPackageHandler.CreateAndSavePackage(outPath, MEGame.LE1);
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
            Bio2DACell.Bio2DADataType.TYPE_FLOAT => float.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var f) ? new Bio2DACell(f) : null,
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
    if (errors.Count == 0) pkg.Save();
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
  var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<List<M3DAEntry>>(File.ReadAllText(a[1])) ?? new();
  var (modDir, basePath, outPath) = (a[2], a[3], a[4]);
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
  basePkg.Save(outPath);
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
  var dumpDir = Environment.GetEnvironmentVariable("WEMDIR");
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
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  foreach (var e in pcc.Exports.Where(x => x.ClassName == "GFxMovieInfo")) {
    var rd = e.GetProperty<LegendaryExplorerCore.Unreal.ImmutableByteArrayProperty>("RawData");
    var bytes = rd?.Bytes;
    if (bytes == null) { var ap = e.GetProperty<ArrayProperty<ByteProperty>>("RawData"); if (ap != null) bytes = ap.Select(b => b.Value).ToArray(); }
    if (bytes == null) continue;
    var outp = Path.Combine(args[2], e.ObjectName.Instanced + ".gfx");
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
  // buildsound <donorPcc> <donorCueName> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCueName> <wavTitle> [volume]
  // Adds the cue to outPcc (created if missing); only the waves under the new cue get the Ogg.
  var (donorPath, donorCue, oggPath) = (args[1], args[2], args[3]);
  int samples = int.Parse(args[4]), channels = int.Parse(args[5]), rate = int.Parse(args[6]);
  var (outPath, newName, title) = (args[7], args[8], args[9]);
  using var donor = MEPackageHandler.OpenMEPackage(donorPath);
  var cue = donor.Exports.Single(x => x.ClassName == "SoundCue" && x.ObjectName.Instanced == donorCue);
  if (!File.Exists(outPath)) MEPackageHandler.CreateAndSavePackage(outPath, MEGame.LE1);
  using var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  var report = LegendaryExplorerCore.Packages.CloningImportingAndRelinking.EntryExporter.ExportExportToPackage(cue, pkg, out var ported, null, null);
  foreach (var r in report) Console.WriteLine("relink: " + r);
  var newCue = (ExportEntry)ported;
  newCue.ObjectName = NameReference.FromInstancedString(newName);
  newCue.Parent = null; // top level: <package>.<newName>, not under the donor's "snd_gui" group
  if (args.Length > 10) newCue.WriteProperty(new FloatProperty(float.Parse(args[10], System.Globalization.CultureInfo.InvariantCulture), "VolumeMultiplier"));
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
  pkg.Save();
  foreach (var prop in newCue.GetProperties()) Console.WriteLine($"cue prop {prop.Name.Instanced} = {(prop is FloatProperty f ? f.Value.ToString() : prop is ObjectProperty o ? o.Value.ToString() : prop is NameProperty n ? n.Value.Instanced : prop.GetType().Name)}");
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "portmesh") {
  // portmesh <srcPcc> <srcUIndex> <pcc> <newName>: ports a StaticMesh with its materials, textures and the
  // materials' local shaders to the top level of an existing package
  using var src = MEPackageHandler.OpenMEPackage(args[1]);
  var mesh = src.GetUExport(int.Parse(args[2]));
  using var pkg = MEPackageHandler.OpenMEPackage(args[3], forceLoadFromDisk: true);
  var report = LegendaryExplorerCore.Packages.CloningImportingAndRelinking.EntryExporter.ExportExportToPackage(mesh, pkg, out var ported, null, null);
  foreach (var r in report) Console.WriteLine("relink: " + r);
  var newMesh = (ExportEntry)ported;
  newMesh.ObjectName = NameReference.FromInstancedString(args[4]);
  newMesh.Parent = null;
  foreach (var x in pkg.Exports) x.ExportFlags &= ~LegendaryExplorerCore.Unreal.UnrealFlags.EExportFlags.ForcedExport;
  var portedPaths = pkg.Exports.Where(x => x.ClassName == "Material").Select(x => x.InstancedFullPath).ToHashSet();
  var srcMats = src.Exports.Where(x => x.ClassName == "Material" && portedPaths.Contains(x.InstancedFullPath)).ToList();
  var shaders = LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetLocalShadersForMaterials(srcMats, null);
  Console.WriteLine($"materials {srcMats.Count}: {string.Join(", ", srcMats.Select(m => m.InstancedFullPath))}; local shader maps {shaders?.MaterialShaderMaps.Count ?? 0}, shaders {shaders?.Shaders.Count ?? 0}");
  if (shaders != null) LegendaryExplorerCore.Shaders.ShaderCacheManipulator.AddShadersToFile(pkg, shaders);
  pkg.Save();
  foreach (var b in LegendaryExplorerCore.Shaders.ShaderCacheManipulator.GetBrokenMaterials(pkg, null)) Console.WriteLine("BROKEN material " + b);
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{x.DataSize}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
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
  // texpng <pcc> <uIndex> <out.png>: a Texture2D's top mip as PNG
  using var pcc = MEPackageHandler.OpenMEPackage(args[1]);
  Console.WriteLine(new LegendaryExplorerCore.Unreal.Classes.Texture2D(pcc.GetUExport(int.Parse(args[2]))).ExportToPNG(args[3]));
} else if (cmd == "coalesced") {
  // coalesced <bin> <outDir> [filter]: writes every ini of a LE1/LE2 Coalesced file to outDir and prints the lines
  // that contain filter, with tabs shown as \t (ConfigDelta removals must match a line exactly)
  using var fs = File.OpenRead(args[1]);
  var inis = LegendaryExplorerCore.Coalesced.CoalescedConverter.DecompileLE1LE2ToMemory(fs, Path.GetFileName(args[1]));
  Directory.CreateDirectory(args[2]);
  foreach (var kv in inis) {
    var name = kv.Key.Replace('\\', '/').Split('/').Last();
    var text = kv.Value.ToString();
    File.WriteAllText(Path.Combine(args[2], name), text);
    if (args.Length < 4) continue;
    foreach (var line in text.Split('\n').Where(l => l.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
      Console.WriteLine(name + ": " + line.TrimEnd('\r').Replace("\t", "\\t"));
  }
} else if (cmd == "compileclass") {
  // compileclass <outPcc> <packageName> <Class.uc>...: new LE1 package with the classes under a Package export, and a
  // CombinedStartupReferencer that keeps them loaded when the file is an AutoLoad GlobalPackage (LE1CP's layout)
  var outPath = args[1];
  if (File.Exists(outPath)) File.Delete(outPath);
  MEPackageHandler.CreateAndSavePackage(outPath, MEGame.LE1);
  using var pkg = MEPackageHandler.OpenMEPackage(outPath, forceLoadFromDisk: true);
  var classes = args.Skip(3).Select(f => new LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.LooseClass(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f), f)).ToList();
  using var cache = new PackageCache();
  var usop = new LegendaryExplorerCore.UnrealScript.UnrealScriptOptionsPackage { Cache = cache };
  var log = LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.CompileLooseClasses(pkg,
    new List<LegendaryExplorerCore.UnrealScript.UnrealScriptCompiler.LooseClassPackage> { new(args[2], classes) }, usop);
  foreach (var m in log.Content) Console.WriteLine("log: " + m);
  if (log.HasErrors) { Console.WriteLine("COMPILE FAILED"); Environment.Exit(1); }
  var referencer = ExportCreator.CreateExport(pkg, "CombinedStartupReferencer", "ObjectReferencer", null, null, false, false, null, null);
  var refs = new ArrayProperty<ObjectProperty>("ReferencedObjects");
  foreach (var c in pkg.Exports.Where(x => x.ClassName == "Class")) refs.Add(new ObjectProperty(c));
  referencer.WriteProperty(refs);
  pkg.Save();
  foreach (var x in pkg.Exports) Console.WriteLine($"{x.UIndex}\t{x.ClassName}\t{x.InstancedFullPath}\t{x.DataSize}");
  foreach (var i in pkg.Imports) Console.WriteLine($"import {i.UIndex}\t{i.ClassName}\t{i.InstancedFullPath}");
} else if (cmd == "addactor") {
  // addactor <levelPcc> <package> <class>: places one actor of a script class at the origin of the level, with its class
  // defaults. The class is imported from <package>, which must already be in memory (an AutoLoad GlobalPackage).
  using var pcc = MEPackageHandler.OpenMEPackage(args[1], forceLoadFromDisk: true);
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
  pcc.Save();
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
    File.WriteAllBytes(Path.Combine(args[2], name + ".raw"), bin.RawData);
    var bp = LegendaryExplorerCore.Sound.ISACT.ISACTHelper.GetPairedBanks(bin.RawData);
    int i = 0;
    foreach (var c in bp.ISBBank.GetAllBankChunks().Where(c => c.ChunkName == "data")) {
      File.WriteAllBytes(Path.Combine(args[2], $"{name}.data{i++}.bin"), c.RawData);
    }
    Console.WriteLine($"{e.UIndex}\t{e.InstancedFullPath}\traw={bin.RawData.Length}\tdatachunks={i}");
  }
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
