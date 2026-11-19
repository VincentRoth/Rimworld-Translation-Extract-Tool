using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

static class Program
{
    const string Usage = @"TranslationExtractor — extrait les textes à traduire d'un mod RimWorld (defs + patchs + Keyed)

Usage :
  TranslationExtractor <mod> [options]      mod = chemin, packageId, ID Workshop ou nom
  TranslationExtractor --list-mods [filtre]
  TranslationExtractor --check <dossier|fichier>   vérifie une traduction (TODO, longueurs, placeholders)
  TranslationExtractor --calibrate <Core|Royalty|...>  compare l'extraction aux traductions FR officielles
  TranslationExtractor --fill-tm <dossier|packageId> [--tm-global]
                       remplit les TODO avec la traduction d'une entrée au même texte anglais (même mod, puis tout out\ avec --tm-global)
  TranslationExtractor --label-tables <id[,id2]> [--tables <dossier>]
                       décompose les libellés « matériau x motif » (Flooring Floors) et écrit/met à jour
                       label-patterns.csv et label-materials.csv (défaut : out\<premier id>)
  TranslationExtractor --fill-labels <id[,id2]> [--tables <dossier>]
                       remplit les .label TODO à partir des deux tables traduites
                       (--relabel : régénère aussi les libellés déjà générés, après correction des tables)
     options de ces deux commandes : --label-folder TerrainDef (dossier DefInjected visé), --min-share 3

Options :
  --version 1.6        version du jeu visée (défaut : version installée)
  --with a,b           packageIds/noms de mods optionnels à considérer actifs
  --out <dossier>      sortie (défaut : <outil>\out\<packageId>)
  --lang French        nom du dossier de langue
  --placeholder TODO   valeur laissée dans chaque entrée
  --prefill-en         mettre le texte anglais comme valeur
  --skip-shipped       ne pas extraire les entrées que le mod traduit déjà lui-même (voir l'avertissement)
  --fresh              ne pas fusionner avec une sortie existante
  --strict             ignorer les champs sans [MustTranslate]
  --respect-conditions respecter IfModActive/MayRequire/FindMod (défaut : tout est supposé actif, pour extraire tous les textes)
  --no-implicit        ne pas extraire les textes absents du XML (défauts du code, formes féminines/plurielles, voir implicit-rules.json)
  --scan-dll           analyser les DLL du mod pour trouver où les clés Keyed sont utilisées
  --game-dir <dossier> dossier RimWorld
  --workshop-dir <d>   dossier workshop\content\294100
";

    static string ToolDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "TranslationExtractor.csproj"))) return d.FullName;
            d = d.Parent;
        }
        return @"D:\Jeux\Rimworld\TranslationExtractTool";
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? target = null, outDir = null, with = null, lang = "French", placeholder = "TODO",
                gameDir = null, workDir = null, version = null, checkPath = null, calibrate = null, listFilter = null, probe = null;
        bool prefill = false, fresh = false, strict = false, scanDll = false, list = false, skipShipped = false, noImplicit = false; string? dumpDef = null;
        string? fillTm = null, labelTables = null, fillLabels = null, tablesDir = null; string labelFolder = "TerrainDef"; bool tmGlobal = false, relabel = false; int minShare = 3;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new Exception("Valeur manquante pour " + args[i]);
            switch (args[i])
            {
                case "--version": version = Next(); break;
                case "--with": with = Next(); break;
                case "--out": outDir = Next(); break;
                case "--lang": lang = Next(); break;
                case "--placeholder": placeholder = Next(); break;
                case "--prefill-en": prefill = true; break;
                case "--fresh": fresh = true; break;
                case "--skip-shipped": skipShipped = true; break;
                case "--strict": strict = true; break;
                case "--no-implicit": noImplicit = true; break;
                case "--dump-def": dumpDef = Next(); break;
                case "--respect-conditions": GameEnv.AssumeAll = false; break;
                case "--scan-dll": scanDll = true; break;
                case "--game-dir": gameDir = Next(); break;
                case "--workshop-dir": workDir = Next(); break;
                case "--check": checkPath = Next(); break;
                case "--probe": probe = Next(); break;
                case "--calibrate": calibrate = Next(); break;
                case "--fill-tm": fillTm = Next(); break;
                case "--tm-global": tmGlobal = true; break;
                case "--label-tables": labelTables = Next(); break;
                case "--fill-labels": fillLabels = Next(); break;
                case "--tables": tablesDir = Next(); break;
                case "--label-folder": labelFolder = Next(); break;
                case "--min-share": minShare = int.Parse(Next()); break;
                case "--relabel": relabel = true; break;
                case "--list-mods": list = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) listFilter = args[++i]; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default: if (args[i].StartsWith("--")) { Console.Error.WriteLine("Option inconnue " + args[i]); return 2; } target = args[i]; break;
            }
        }

        if (checkPath != null) return Checker.Run(checkPath);
        // post-extraction helpers: they only read and write under out\ (or the given folder)
        var outRoot = Path.Combine(ToolDir(), "out");
        string[] Ids(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            if (fillTm != null) return TranslationMemory.Run(fillTm, outRoot, lang, placeholder, tmGlobal);
            if (labelTables != null) return LabelGen.Tables(Ids(labelTables), outRoot, lang, placeholder, tablesDir, labelFolder, minShare);
            if (fillLabels != null) return LabelGen.Fill(Ids(fillLabels), outRoot, lang, placeholder, tablesDir, labelFolder, minShare, relabel);
        }
        catch (Exception e) when (fillTm != null || labelTables != null || fillLabels != null) { Console.Error.WriteLine(e.Message); return 2; }
        if (probe != null)
        {
            var pe = new GameEnv(); pe.Scan(); var rr = new Report();
            using var ti = new TypeIndex(pe.ManagedDir, Array.Empty<string>(), rr);
            if (probe.StartsWith("ctor:")) Probe2.Ctor(ti, probe[5..]); else Probe.Dump(ti, probe); return 0;
        }

        var env = new GameEnv();
        if (gameDir != null) env.GameDir = gameDir;
        if (workDir != null) env.WorkshopDir = workDir;
        if (version != null) env.Version = version;
        env.Scan();

        if (list)
        {
            foreach (var m in env.Index.Where(m => listFilter == null || m.Name.Contains(listFilter, StringComparison.OrdinalIgnoreCase) || m.PackageId.Contains(listFilter, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"{m.PackageId,-45} {m.Name}   {(m.Official ? "(officiel)" : m.Root)}");
            return 0;
        }
        if (calibrate != null) target = calibrate;
        if (target == null) { Console.WriteLine(Usage); return 1; }

        var sw = Stopwatch.StartNew();
        var rep = new Report();
        ModMeta mod;
        try { mod = env.ResolveTarget(target); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
        Console.WriteLine($"Cible : {mod}  ({mod.Root})  version {env.Version}");

        var extra = (with ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Passe "toutes conditions" : charger aussi les mods installés que la cible vise (IfModActive de LoadFolders,
        // MayRequire, PatchOperationFindMod), sinon ses patchs sur ces mods ne trouvent rien (ex. Intimacy sur VRE Highmate).
        var autoWith = TargetedMods(env, mod);
        var extraAll = extra.Concat(autoWith.Select(m => m.PackageId)).Distinct().ToArray();
        if (autoWith.Count > 0) rep.Notes.Add($"Mods visés par les conditions, chargés pour la passe « toutes conditions » : {string.Join(", ", autoWith.Select(m => m.Name))}");
        var sw0 = sw;
        string tid = mod.PackageId;

        // Une passe = un jeu de conditions. Par défaut deux passes : conditions respectées (mod seul), puis tout supposé actif
        // (variantes IfModActive/MayRequire/FindMod, CE...) ; l'union est extraite, la première passe l'emportant en cas de conflit
        // (des variantes exclusives peuvent se remplacer l'une l'autre : on garde alors les deux jeux de clés).
        List<Entry> Collect(bool assumeAll, bool note)
        {
            GameEnv.AssumeAll = assumeAll;
            var order = env.BuildLoadOrder(mod, assumeAll ? extraAll : extra, rep);
            if (note) Console.WriteLine("Ordre : " + string.Join(" > ", order.Select(o => o.Name)));
            var pipe = new Pipeline(env, order, mod, rep);
            pipe.Load();
            Console.WriteLine($"[{(assumeAll ? "toutes conditions" : "conditions respectées")}] Defs chargées : {pipe.BaseDoc.DocumentElement!.ChildNodes.Count}, patchs : {pipe.Patches.Count}  ({sw.Elapsed.TotalSeconds:F1}s)");

            // DLL des mods (cible + dépendances) pour résoudre les types custom
            var dlls = order.Where(o => !o.Official).SelectMany(o => o.Folders)
                .Select(f => Path.Combine(f, "Assemblies")).Where(Directory.Exists).Distinct()
                .SelectMany(d => Directory.GetFiles(d, "*.dll")).ToList();
            using var types = new TypeIndex(env.ManagedDir, dlls, rep);
            var ext = new Extraction(types, rep) { IncludeUnmarked = !strict, CountOrigin = mod.PackageId, ImplicitDefaults = !noImplicit, Companions = CompanionFile.Load(Path.Combine(ToolDir(), "implicit-rules.json")) };

            // run B : tous les patchs
            var docB = pipe.Build(true);
            if (dumpDef != null) foreach (var n in docB.SelectNodes("//*[defName='" + dumpDef + "']")!.OfType<System.Xml.XmlElement>()) Console.WriteLine(n.OuterXml);
            var entriesB = ext.ExtractDefs(docB, o => true);
            Console.WriteLine($"Entrées brutes : {entriesB.Count}  ({sw.Elapsed.TotalSeconds:F1}s)");

            if (mod.Official) return entriesB.Where(e => e.Origin == tid).ToList();
            var own = entriesB.Where(e => e.Origin == tid).ToList();
            var foreign = entriesB.Where(e => e.Origin != tid).ToList();
            // (key, text) pairs: a key can appear twice in one def with different texts (same TKey in two quest nodes),
            // keeping one text per key would report the other one as changed by the mod
            var baseline = new HashSet<string>();
            if (pipe.Patches.Any(p => p.Mod == mod) && foreign.Count > 0)
            {
                var docA = pipe.Build(false);
                foreach (var e in ext.ExtractDefs(docA, o => o != tid)) baseline.Add(e.Folder + "/" + e.Key + "\n" + e.Sig);
            }
            else foreach (var e in foreign) baseline.Add(e.Folder + "/" + e.Key + "\n" + e.Sig);
            var changed = foreign.Where(e => !baseline.Contains(e.Folder + "/" + e.Key + "\n" + e.Sig)).ToList();
            if (note) rep.Notes.Add($"Defs du mod : {own.Count} entrées ; textes ajoutés/modifiés par ses patchs sur d'autres mods : {changed.Count}");
            return own.Concat(changed).ToList();
        }

        var wantAll = GameEnv.AssumeAll;
        List<Entry> entries;
        if (wantAll && !mod.Official)
        {
            var first = Collect(false, false);
            var all = Collect(true, true);    // laisse mod.Folders au maximum (Keyed de tous les dossiers)
            var seen = first.Select(e => e.Folder + "/" + e.Key).ToHashSet();
            var onlyAll = all.Where(e => !seen.Contains(e.Folder + "/" + e.Key)).ToList();
            rep.Notes.Add($"Textes propres aux variantes conditionnelles (dossiers IfModActive, MayRequire, FindMod, CE...) : {onlyAll.Count} entrées en plus du mod seul");
            entries = first.Concat(onlyAll).ToList();
            var du = rep.UnknownTypes.Distinct().ToList(); rep.UnknownTypes.Clear(); rep.UnknownTypes.AddRange(du);
        }
        else entries = Collect(wantAll, true);

        if (calibrate != null) return Calibrate(env, mod, entries, rep);
        if (!mod.Official) entries.AddRange(EnglishDefInjected(mod, entries, rep));

        // Keyed
        Dictionary<string, List<string>> usages = new();
        if (scanDll)
            foreach (var d in mod.Folders.Select(f => Path.Combine(f, "Assemblies")).Where(Directory.Exists).Distinct())
                foreach (var dll in Directory.GetFiles(d, "*.dll"))
                    foreach (var kv in DllStringScanner.Scan(dll)) usages[kv.Key] = kv.Value;
        var keyed = new Extraction(null!, rep).ExtractKeyed(mod, usages);
        if (keyed.Count == 0 && mod.Folders.All(f => !Directory.Exists(Path.Combine(f, "Languages", "English", "Keyed"))))
            rep.Notes.Add("Aucun dossier Languages/English/Keyed dans le mod.");

        // traduction déjà embarquée par le mod : avertir, mesurer la couverture, option pour ne pas la ré-extraire
        var shipped = ShippedFrench.Load(mod, lang, rep);
        if (shipped.Any)
        {
            var sbShipped = new StringBuilder();
            var (doneDefs, doneKeyed) = shipped.Evaluate(entries, keyed, rep, sbShipped);
            rep.ShippedText = sbShipped.ToString();
            if (skipShipped)
            {
                var d = doneDefs.ToHashSet(); var k = doneKeyed.ToHashSet();
                entries = entries.Where(e => !d.Contains(e)).ToList();
                keyed = keyed.Where(x => !k.Contains(x)).ToList();
                rep.Notes.Add($"--skip-shipped : {doneDefs.Count} defs et {doneKeyed.Count} Keyed déjà traduits par le mod ne sont pas extraits.");
                Console.WriteLine($"--skip-shipped : {doneDefs.Count} defs et {doneKeyed.Count} Keyed ignorés.");
            }
        }

        outDir ??= Path.Combine(ToolDir(), "out", mod.PackageId);
        var langDir = Path.Combine(outDir, "Languages", lang);
        var rules = new ContextResolver(Path.Combine(ToolDir(), "context-rules.json"));
        var wo = new WriterOptions { OutDir = langDir, Placeholder = placeholder, PrefillEn = prefill, Fresh = fresh };
        var writer = new LanguageWriter(wo, rules, new OfficialFr(env.GameDir), rep);
        writer.WriteDefs(entries, tid);
        writer.WriteKeyed(keyed);
        writer.CleanOrphans();

        WriteReport(outDir, mod, entries, keyed, writer, rep, env);
        Console.WriteLine($"\n{writer.Written} entrées écrites ({entries.Count} defs, {keyed.Count} keyed), {writer.Preserved} traductions conservées, " +
                          $"{writer.Changed} EN modifiés, {writer.Obsolete} obsolètes.");
        Console.WriteLine($"Sortie : {langDir}");
        Console.WriteLine($"Rapport : {Path.Combine(outDir, "report.txt")}  ({sw.Elapsed.TotalSeconds:F1}s)");
        return 0;
    }

    /// <summary>Textes que le mod ne met que dans sa traduction anglaise (Languages/English*/DefInjected), absents du XML
    /// de ses defs (ex. Simple Improve : labels de WorkTypeDef) : le jeu les affiche, ils doivent être traduits.</summary>
    static List<Entry> EnglishDefInjected(ModMeta mod, List<Entry> entries, Report rep)
    {
        var have = entries.Select(e => e.Folder + "/" + e.Key).ToHashSet();
        // dossier d'une sous-classe -> type à base de données, comme nos extractions
        var folderMap = entries.Where(e => e.LegacyFolder != "" && e.LegacyFolder != e.Folder).GroupBy(e => e.LegacyFolder).ToDictionary(g => g.Key, g => g.First().Folder);
        // champs déjà extraits (def + dernier segment) : une clé anglaise indexée (stages.0.label) en serait un doublon
        var leaves = entries.Select(e => e.Folder + "/" + e.DefName + "/" + e.Leaf).ToHashSet();
        var indexed = new System.Text.RegularExpressions.Regex(@"\.\d+(\.|$)");
        var res = new List<Entry>();
        foreach (var f in mod.Folders)
        {
            var lang = Path.Combine(f, "Languages");
            if (!Directory.Exists(lang)) continue;
            foreach (var en in Directory.GetDirectories(lang).Where(d => Path.GetFileName(d).StartsWith("English", StringComparison.OrdinalIgnoreCase)))
            {
                var di = Path.Combine(en, "DefInjected");
                if (!Directory.Exists(di)) continue;
                foreach (var typeDir in Directory.GetDirectories(di))
                {
                    var folder = Path.GetFileName(typeDir);
                    if (folderMap.TryGetValue(folder, out var mapped)) folder = mapped;
                    foreach (var file in Directory.EnumerateFiles(typeDir, "*.xml", SearchOption.AllDirectories))
                    {
                        System.Xml.XmlDocument doc = new();
                        try { doc.Load(file); } catch { continue; }
                        foreach (var el in doc.DocumentElement!.ChildNodes.OfType<System.Xml.XmlElement>())
                        {
                            var key = el.Name; var dot = key.IndexOf('.');
                            if (dot <= 0 || have.Contains(folder + "/" + key)) continue;
                            if (indexed.IsMatch(key) && leaves.Contains(folder + "/" + key[..dot] + "/" + key[(key.LastIndexOf('.') + 1)..])) continue;
                            have.Add(folder + "/" + key);
                            var lis = el.ChildNodes.OfType<System.Xml.XmlElement>().Where(x => x.Name == "li").Select(x => x.InnerText).ToList();
                            if (lis.Count == 0 && string.IsNullOrWhiteSpace(el.InnerText)) continue;
                            res.Add(new Entry
                            {
                                Folder = folder, LegacyFolder = folder, File = Path.GetFileName(file), DefName = key[..dot], Path = key[(dot + 1)..],
                                Value = lis.Count == 0 ? el.InnerText : "", Items = lis.Count > 0 ? lis : null,
                                DefTypeName = folder.Contains('.') ? folder[(folder.LastIndexOf('.') + 1)..] : folder, TypeChain = new() { folder },
                                Origin = mod.PackageId, Marked = true, FieldType = lis.Count > 0 ? "List<string>" : "string",
                                Note = "texte fourni seulement par la traduction anglaise du mod (Languages/English/DefInjected)",
                            });
                        }
                    }
                }
            }
        }
        if (res.Count > 0) rep.Notes.Add($"Textes pris dans la traduction anglaise du mod (absents du XML des defs) : {res.Count}");
        return res;
    }

    /// <summary>Mods installés (hors officiels et cible) visés par les conditions du mod : IfModActive* de LoadFolders,
    /// MayRequire/MayRequireAnyOf de ses XML, noms de PatchOperationFindMod.</summary>
    static List<ModMeta> TargetedMods(GameEnv env, ModMeta mod)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lf = Path.Combine(mod.Root, "LoadFolders.xml");
        if (File.Exists(lf))
            foreach (Match m in Regex.Matches(File.ReadAllText(lf), "IfModActive(?:Any|All)?\\s*=\\s*\"([^\"]+)\""))
                foreach (var x in m.Groups[1].Value.Split(',')) ids.Add(x.Trim());
        var saved = GameEnv.AssumeAll; GameEnv.AssumeAll = true;
        try { env.ComputeFolders(mod, new HashSet<string>()); } finally { GameEnv.AssumeAll = saved; }
        foreach (var f in mod.Folders)
            foreach (var sub in new[] { "Defs", "Patches" })
            {
                var d = Path.Combine(f, sub);
                if (!Directory.Exists(d)) continue;
                foreach (var file in Directory.EnumerateFiles(d, "*.xml", SearchOption.AllDirectories))
                {
                    string t; try { t = File.ReadAllText(file); } catch { continue; }
                    foreach (Match m in Regex.Matches(t, "MayRequire(?:AnyOf)?\\s*=\\s*\"([^\"]+)\""))
                        foreach (var x in m.Groups[1].Value.Split(',')) ids.Add(x.Trim());
                    foreach (Match m in Regex.Matches(t, "<mods>(.*?)</mods>", RegexOptions.Singleline))
                        foreach (Match li in Regex.Matches(m.Groups[1].Value, "<li>\\s*(.*?)\\s*</li>", RegexOptions.Singleline))
                            names.Add(System.Net.WebUtility.HtmlDecode(li.Groups[1].Value));
                }
            }
        var res = new List<ModMeta>();
        foreach (var id in ids) { var m = env.FindByPackageId(id); if (m != null) res.Add(m); }
        foreach (var n in names) { var m = env.Index.FirstOrDefault(i => i.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); if (m != null) res.Add(m); }
        return res.Where(m => !m.Official && m.Root != mod.Root).GroupBy(m => m.Root).Select(g => g.First()).ToList();
    }

    static void WriteReport(string outDir, ModMeta mod, List<Entry> entries, List<KeyedEntry> keyed, LanguageWriter w, Report rep, GameEnv env)
    {
        Directory.CreateDirectory(outDir);
        rep.Notes.Add("Non extraits (générés par le code du jeu) : recettes/terrains implicites, valeurs par défaut non littérales (ex. WorkTypeDef.label), textes ajoutés par des patchs C# (CE...). Les formes absentes du XML listées dans implicit-rules.json sont extraites (sauf --no-implicit).");
        var sb = new StringBuilder();
        sb.AppendLine($"Mod : {mod}  — version jeu {env.Version}");
        sb.AppendLine($"Entrées defs : {entries.Count}, Keyed : {keyed.Count}");
        foreach (var n in rep.Notes) sb.AppendLine("Note : " + n);
        sb.AppendLine("\n== Entrées par type de def ==");
        foreach (var g in entries.GroupBy(e => e.Folder).OrderByDescending(g => g.Count()))
            sb.AppendLine($"{g.Count(),6}  {g.Key}");
        if (rep.ShippedText != null) sb.Append(rep.ShippedText);
        if (rep.UnsupportedPatches.Count > 0)
        {
            sb.AppendLine("\n== Patchs non exécutés (classes C# personnalisées) : à vérifier à la main ==");
            foreach (var s in rep.UnsupportedPatches.Distinct()) sb.AppendLine("  " + s);
        }
        if (rep.UnknownTypes.Count > 0)
        {
            sb.AppendLine("\n== Types de def introuvables (DLL absente ?) ==");
            foreach (var s in rep.UnknownTypes) sb.AppendLine("  " + s);
        }
        if (rep.MissingMods.Count > 0)
        {
            sb.AppendLine("\n== Mods manquants ==");
            foreach (var s in rep.MissingMods.Distinct()) sb.AppendLine("  " + s);
        }
        if (rep.SkippedUnmarked.Count > 0)
        {
            sb.AppendLine("\n== Champs texte non marqués ignorés (top 40) ==");
            foreach (var kv in rep.SkippedUnmarked.OrderByDescending(k => k.Value).Take(40)) sb.AppendLine($"  {kv.Value,6}  {kv.Key}");
        }
        if (rep.Warnings.Count > 0)
        {
            sb.AppendLine("\n== Avertissements ==");
            foreach (var s in rep.Warnings.Take(200)) sb.AppendLine("  " + s);
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString(), new UTF8Encoding(true));
    }

    static int Calibrate(GameEnv env, ModMeta mod, List<Entry> entries, Report rep)
    {
        var fr = new OfficialFr(env.GameDir, Path.GetFileName(mod.Root));
        var official = fr.AllDefKeys();
        var mine = entries.Select(e => e.Folder + "/" + e.Key).ToHashSet();
        var mineMarked = entries.Where(e => e.Marked).Select(e => e.Folder + "/" + e.Key).ToHashSet();
        // le jeu ne produit pas de clés en collection index (rulesStrings.N) : normaliser
        var missing = official.Where(k => !mine.Contains(k)).OrderBy(k => k).ToList();
        var extra = mine.Where(k => !official.Contains(k)).OrderBy(k => k).ToList();
        Console.WriteLine($"Officiel : {official.Count}  Extrait : {mine.Count}  (marqués : {mineMarked.Count})");
        Console.WriteLine($"Manquants (dans FR officiel, pas extraits) : {missing.Count}");
        foreach (var k in missing.Take(40)) Console.WriteLine("  - " + k);
        Console.WriteLine($"En trop (extraits, absents du FR officiel) : {extra.Count}");
        foreach (var k in extra.Take(40)) Console.WriteLine("  + " + k);
        Func<string, string> shape = k =>
        {
            var slash = k.IndexOf('/');
            var dot = k.IndexOf('.', slash);
            var s = k.Substring(0, slash) + "/" + (dot >= 0 ? k.Substring(dot + 1) : k);
            return System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+\b", "N");
        };
        Console.WriteLine("-- manquants par forme --");
        foreach (var g in missing.GroupBy(shape).OrderByDescending(g => g.Count()).Take(40)) Console.WriteLine($"  {g.Count(),5} {g.Key}   ex: {g.First()}");
        Console.WriteLine("-- en trop par forme --");
        foreach (var g in extra.GroupBy(shape).OrderByDescending(g => g.Count()).Take(25)) Console.WriteLine($"  {g.Count(),5} {g.Key}   ex: {g.First()}");
        var extraUnmarked = extra.Where(k => !mineMarked.Contains(k)).Count();
        Console.WriteLine($"   dont non marqués : {extraUnmarked}");
        return 0;
    }
}
