using System.Diagnostics;
using System.Text;

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
        var sw0 = sw;
        string tid = mod.PackageId;

        // Une passe = un jeu de conditions. Par défaut deux passes : conditions respectées (mod seul), puis tout supposé actif
        // (variantes IfModActive/MayRequire/FindMod, CE...) ; l'union est extraite, la première passe l'emportant en cas de conflit
        // (des variantes exclusives peuvent se remplacer l'une l'autre : on garde alors les deux jeux de clés).
        List<Entry> Collect(bool assumeAll, bool note)
        {
            GameEnv.AssumeAll = assumeAll;
            var order = env.BuildLoadOrder(mod, extra, rep);
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
            var baseline = new Dictionary<string, string>();
            if (pipe.Patches.Any(p => p.Mod == mod) && foreign.Count > 0)
            {
                var docA = pipe.Build(false);
                foreach (var e in ext.ExtractDefs(docA, o => o != tid)) baseline[e.Folder + "/" + e.Key] = e.Sig;
            }
            else foreach (var e in foreign) baseline[e.Folder + "/" + e.Key] = e.Sig;
            var changed = foreign.Where(e => !baseline.TryGetValue(e.Folder + "/" + e.Key, out var s) || s != e.Sig).ToList();
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

        WriteReport(outDir, mod, entries, keyed, writer, rep, env);
        Console.WriteLine($"\n{writer.Written} entrées écrites ({entries.Count} defs, {keyed.Count} keyed), {writer.Preserved} traductions conservées, " +
                          $"{writer.Changed} EN modifiés, {writer.Obsolete} obsolètes.");
        Console.WriteLine($"Sortie : {langDir}");
        Console.WriteLine($"Rapport : {Path.Combine(outDir, "report.txt")}  ({sw.Elapsed.TotalSeconds:F1}s)");
        return 0;
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
