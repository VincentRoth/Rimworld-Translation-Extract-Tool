using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// --audit-keys: entries already in out\ whose field the code uses as an identifier (FieldUsageIndex), extracted before
/// this check existed. An entry does not record its field's declaring type: a field of that name counts only if it is
/// not [MustTranslate] and its type is declared by the game or by an assembly of the mod, of its dependencies or of a
/// mod it loads after (patch targets). Candidates to review; re-extracting the mod applies the exact rule (field type
/// known) and moves an existing translation to an OBSOLETE comment.
/// Reads the game, the workshop and out\; writes only out\audit-keys.csv.
/// </summary>
public static class AuditKeys
{
    static readonly Regex En = new(@"<!-- EN: (.*?) -->", RegexOptions.Compiled);
    static readonly Regex Identifier = new(@"^[\w\-\.]+$", RegexOptions.Compiled);
    static readonly Regex VersionDir = new(@"[\\/]v?(\d+\.\d+)(-(\d+\.\d+))?[\\/]", RegexOptions.Compiled);

    public static int Run(string target, GameEnv env, string outRoot, string lang, string placeholder)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var acs = Path.Combine(env.ManagedDir, "Assembly-CSharp.dll");
        var dlls = new List<string> { acs };
        var dllsByMod = new Dictionary<string, List<string>>();
        foreach (var m in env.Index.Where(m => !m.Official))
        {
            if (!dllsByMod.TryGetValue(m.PackageId, out var own)) dllsByMod[m.PackageId] = own = new();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(m.Root, "*.dll", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                var rel = f[m.Root.Length..];
                if (!rel.Contains("Assemblies", StringComparison.OrdinalIgnoreCase) || rel.Contains(@"\Source\", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;
                // other game versions left out (folder "1.5", "v1.5", or a range "1.2-1.6" that does not include the current one)
                var v = VersionDir.Match(rel);
                if (v.Success && v.Groups[1].Value != env.Version && !(v.Groups[3].Success && InRange(env.Version, v.Groups[1].Value, v.Groups[3].Value))) continue;
                dlls.Add(f); own.Add(f);
            }
        }
        var idx = FieldUsageIndex.Build(dlls);
        var (marked, typeDlls, markedNames) = Declarations(dlls);
        Console.WriteLine($"Assemblies analysées : {dlls.Count}, champs utilisés comme identifiant : {idx.All.Count()}, champs [MustTranslate] : {marked.Count}  ({sw.Elapsed.TotalSeconds:F1}s)");

        // field name -> (field key, declaring assemblies, usages), [MustTranslate] fields left out
        var byName = new Dictionary<string, List<(string field, HashSet<string> asm, List<FieldUsageIndex.Usage> uses)>>();
        var anyway = CompanionFile.Load(Path.Combine(outRoot, "..", "implicit-rules.json")).TranslateAnywayKeys();
        foreach (var kv in idx.All)
        {
            if (marked.Contains(kv.Key)) continue;
            if (anyway.Contains(kv.Key)) continue;   // translated by choice (implicit-rules.json)
            var dot = kv.Key.LastIndexOf('.');
            var type = kv.Key[..dot]; var name = kv.Key[(dot + 1)..];
            if (!typeDlls.TryGetValue(type, out var asm)) continue;
            if (!byName.TryGetValue(name, out var l)) byName[name] = l = new();
            l.Add((kv.Key, asm, kv.Value));
        }

        var mods = target == "all"
            ? Directory.GetDirectories(outRoot).Select(d => Path.GetFileName(d)!).Where(d => Directory.Exists(Path.Combine(outRoot, d, "Languages", lang, "DefInjected"))).ToList()
            : target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GameEnv.NormId).ToList();
        var rows = new List<string> { "mod;fichier;cle;anglais;traduction;champ;usage" };
        var perField = new Dictionary<string, (int n, int done)>();
        var perMod = new Dictionary<string, (int n, int done)>();
        foreach (var mod in mods)
        {
            var di = Path.Combine(outRoot, mod, "Languages", lang, "DefInjected");
            if (!Directory.Exists(di)) { Console.Error.WriteLine("Pas de DefInjected : " + di); continue; }
            var allowed = AllowedAssemblies(env, mod, dllsByMod);
            allowed.Add(acs);
            foreach (var file in Directory.EnumerateFiles(di, "*.xml", SearchOption.AllDirectories))
            {
                string? en = null;
                foreach (var line in File.ReadLines(file))
                {
                    var m = En.Match(line);
                    if (m.Success) { en = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value); continue; }
                    if (en == null || line.TrimStart().StartsWith("<!--")) continue;
                    var p = LangXml.Parse(line.Trim());
                    if (p == null) continue;
                    var (key, inner, _) = p.Value;
                    // quest texts (SlateRef) are not filtered by the extraction rule
                    if (key.EndsWith(".slateRef")) { en = null; continue; }
                    if (Leaf(key).StartsWith("debug")) { en = null; continue; }   // debug labels: never filtered by the extraction either
                    var leaf = Leaf(key);
                    var oneWord = Identifier.IsMatch(en.Trim());
                    string? why = null, field = null;
                    // a name that is [MustTranslate] in an assembly the mod loads (Def.label...) is ambiguous: left out
                    var ambiguous = markedNames.TryGetValue(leaf, out var mna) && mna.Overlaps(allowed);
                    if (!ambiguous && byName.TryGetValue(leaf, out var all))
                    {
                        var cands = all.Where(c => c.asm.Overlaps(allowed)).ToList();
                        var hard = cands.SelectMany(c => c.uses.Where(u => u.Kind is not ("transkey" or "compare-tr")).Select(u => (c.field, u))).FirstOrDefault();
                        var tk = cands.SelectMany(c => c.uses.Where(u => u.Kind == "transkey").Select(u => (c.field, u))).FirstOrDefault();
                        if (hard.u != null) { why = hard.u.Kind + " : " + hard.u.Where; field = string.Join(" | ", cands.Select(c => c.field).Distinct().Take(4)); }
                        else if (tk.u != null && oneWord) { why = "transkey : " + tk.u.Where; field = tk.field; }
                    }
                    if (why == null && oneWord && leaf.EndsWith("Key")) { why = "nom en *Key, valeur d'un mot (clé Keyed)"; field = leaf; }
                    if (why != null)
                    {
                        var done = inner.Trim() != placeholder;
                        rows.Add(string.Join(";", new[] { mod, Path.GetRelativePath(di, file), key, en, done ? inner.Trim() : "", field!, why }.Select(Csv)));
                        var pf = perField.GetValueOrDefault(leaf); perField[leaf] = (pf.n + 1, pf.done + (done ? 1 : 0));
                        var pm = perMod.GetValueOrDefault(mod); perMod[mod] = (pm.n + 1, pm.done + (done ? 1 : 0));
                    }
                    en = null;
                }
            }
        }
        var outFile = Path.Combine(outRoot, "audit-keys.csv");
        File.WriteAllLines(outFile, rows, new UTF8Encoding(true));
        Console.WriteLine($"\nEntrées candidates : {rows.Count - 1} (dont traduites : {perMod.Values.Sum(x => x.done)}) dans {perMod.Count} mods.");
        Console.WriteLine("Par champ (entrées, traduites) :");
        foreach (var kv in perField.OrderByDescending(k => k.Value.n).Take(40)) Console.WriteLine($"  {kv.Value.n,6} {kv.Value.done,6}  {kv.Key}");
        Console.WriteLine("Mods avec des entrées traduites (vérifier, puis réextraire) :");
        foreach (var kv in perMod.Where(k => k.Value.done > 0).OrderByDescending(k => k.Value.done)) Console.WriteLine($"  {kv.Value.done,6}  {kv.Key}");
        Console.WriteLine($"Détail : {outFile}  ({sw.Elapsed.TotalSeconds:F1}s)");
        return 0;
    }

    /// <summary>Assemblies of the mod, its dependencies and the mods it loads after, transitively.</summary>
    static HashSet<string> AllowedAssemblies(GameEnv env, string packageId, Dictionary<string, List<string>> dllsByMod)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>();
        var todo = new Stack<string>(); todo.Push(packageId);
        while (todo.Count > 0)
        {
            var id = GameEnv.NormId(todo.Pop());
            if (!seen.Add(id)) continue;
            if (dllsByMod.TryGetValue(id, out var l)) res.UnionWith(l);
            var m = env.FindByPackageId(id);
            if (m == null) continue;
            foreach (var d in m.Deps.Concat(m.LoadAfter)) todo.Push(d);
        }
        return res;
    }

    static bool InRange(string v, string lo, string hi) =>
        Version.TryParse(v, out var x) && Version.TryParse(lo, out var a) && Version.TryParse(hi, out var b) && x >= a && x <= b;

    /// <summary>Last field of a key path, skipping list indexes and the ".slateRef" suffix of quest texts.</summary>
    static string Leaf(string key)
    {
        var parts = key.Split('.');
        for (int i = parts.Length - 1; i > 0; i--)
            if (!Regex.IsMatch(parts[i], @"^\d+$") && parts[i] != "slateRef") return parts[i];
        return parts[^1];
    }

    static string Csv(string s) => s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>[MustTranslate] fields ("Type+Nested.field") and, for each type, the assemblies declaring it.</summary>
    static (HashSet<string> marked, Dictionary<string, HashSet<string>> typeDlls, Dictionary<string, HashSet<string>> markedNames) Declarations(IEnumerable<string> dlls)
    {
        var marked = new HashSet<string>();
        var markedNames = new Dictionary<string, HashSet<string>>();
        var types = new Dictionary<string, HashSet<string>>();
        foreach (var p in dlls.Where(p => !FieldUsageIndex.Skip(p)))
        {
            try
            {
                using var fs = File.OpenRead(p);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var th in md.TypeDefinitions)
                {
                    var tn = TypeName(md, th);
                    if (!types.TryGetValue(tn, out var set)) types[tn] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(p);
                    foreach (var fh in md.GetTypeDefinition(th).GetFields())
                    {
                        var f = md.GetFieldDefinition(fh);
                        foreach (var ah in f.GetCustomAttributes())
                        {
                            var a = md.GetCustomAttribute(ah);
                            string an = a.Constructor.Kind switch
                            {
                                HandleKind.MemberReference when md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } pr
                                    => md.GetString(md.GetTypeReference((TypeReferenceHandle)pr).Name),
                                HandleKind.MethodDefinition => md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType()).Name),
                                _ => ""
                            };
                            if (an == "MustTranslateAttribute")
                            {
                                var fn = md.GetString(f.Name);
                                marked.Add(tn + "." + fn);
                                if (!markedNames.TryGetValue(fn, out var mn)) markedNames[fn] = mn = new(StringComparer.OrdinalIgnoreCase);
                                mn.Add(p);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return (marked, types, markedNames);
    }

    static string TypeName(MetadataReader md, TypeDefinitionHandle th)
    {
        var td = md.GetTypeDefinition(th);
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeName(md, decl) + "+" + name;
        var ns = md.GetString(td.Namespace);
        return ns == "" ? name : ns + "." + name;
    }
}
