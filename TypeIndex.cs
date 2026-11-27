using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace TranslationExtractor;

public sealed class FieldInfoEx
{
    public FieldInfo Field = null!;
    public bool MustTranslate, NoTranslate;
    public bool CanChangeCount;   // [TranslationCanChangeCount] : traduction par liste entière (<li>) autorisée
    public int? HandlePriority;   // null = pas un handle
}

/// <summary>Index de types du jeu et des mods, chargés par réflexion sans exécution (MetadataLoadContext).</summary>
public sealed class TypeIndex : IDisposable
{
    readonly MetadataLoadContext mlc;
    readonly Dictionary<string, List<Type>> bySimple = new();
    readonly Dictionary<string, Type> byFull = new();
    readonly Dictionary<Type, Dictionary<string, FieldInfoEx>> fieldCache = new();
    readonly Dictionary<Type, List<FieldInfoEx>> handleCache = new();
    readonly Report rep;
    List<string> indexedPaths = new();
    FieldUsageIndex? usage;

    /// <summary>How the code of the indexed assemblies uses a string field (compared, dictionary key, Keyed key...), null if not.</summary>
    public IReadOnlyList<FieldUsageIndex.Usage>? LookupUsage(FieldInfo f) => (usage ??= FieldUsageIndex.Build(indexedPaths)).Get(f);

    static readonly string[] CorePrefixes = { "Verse", "RimWorld" };

    public TypeIndex(string managedDir, IEnumerable<string> modDlls, Report rep)
    {
        this.rep = rep;
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(managedDir, "*.dll")) paths[Path.GetFileName(f)] = f;
        var modPaths = new List<string>();
        foreach (var f in modDlls)
        {
            var n = Path.GetFileName(f);
            if (paths.ContainsKey(n)) continue;
            paths[n] = f; modPaths.Add(f);
        }
        var resolver = new PathAssemblyResolver(paths.Values);
        mlc = new MetadataLoadContext(resolver, "mscorlib");

        var toIndex = new List<string>
        {
            Path.Combine(managedDir, "Assembly-CSharp.dll"),
            Path.Combine(managedDir, "Assembly-CSharp-firstpass.dll"),
        };
        toIndex.AddRange(modPaths);
        indexedPaths = toIndex;
        foreach (var p in toIndex)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var asm = mlc.LoadFromAssemblyPath(p);
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    var full = t.FullName ?? t.Name;
                    byFull.TryAdd(full, t);
                    if (!bySimple.TryGetValue(t.Name, out var l)) bySimple[t.Name] = l = new();
                    l.Add(t);
                }
            }
            catch (Exception ex) { rep.Warn($"DLL non indexée {Path.GetFileName(p)}: {ex.Message}"); }
        }
    }

    public Type? Resolve(string name)
    {
        if (byFull.TryGetValue(name, out var t)) return t;
        if (!bySimple.TryGetValue(name, out var l))
        {
            var k = bySimple.Keys.FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (k == null) return null;
            l = bySimple[k];
        }
        return l.FirstOrDefault(x => CorePrefixes.Any(p => (x.Namespace ?? "").StartsWith(p))) ?? l[0];
    }

    public static bool IsDef(Type t)
    {
        for (var b = t; b != null; b = SafeBase(b))
            if (b.FullName == "Verse.Def") return true;
        return false;
    }

    public static Type? SafeBase(Type t)
    {
        try { return t.BaseType; } catch { return null; }
    }

    // DefInjected folder = the def type that owns a DefDatabase, as the game resolves it: the highest
    // non-abstract ancestor below Verse.Def (GenDefDatabase.AllDefTypesWithDatabases). A subclass folder
    // (e.g. Vehicles.VehicleDef : ThingDef) is loaded but never matches a def, so its entries are lost.
    public static Type DatabaseType(Type t)
    {
        var root = t;
        for (var b = SafeBase(t); b != null && b.FullName != "Verse.Def"; b = SafeBase(b))
            if (!b.IsAbstract) root = b;
        return root;
    }

    public static string FolderName(Type t) => TypeFolderName(DatabaseType(t));

    // Folder name used before the database-type fix; kept to merge older extractions into the right folder.
    public static string LegacyFolderName(Type t) => TypeFolderName(t);

    static string TypeFolderName(Type t)
    {
        var ns = t.Namespace ?? "";
        if (ns == "" || ns.StartsWith("Verse") || ns.StartsWith("RimWorld"))
            return t.Name;
        return t.FullName ?? t.Name;
    }

    public static IEnumerable<string> BaseChainNames(Type t)
    {
        for (var b = t; b != null; b = SafeBase(b)) yield return b.Name;
    }

    static string? AttrArgString(CustomAttributeData a) =>
        a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;

    Dictionary<string, FieldInfoEx> FieldsOf(Type t)
    {
        if (fieldCache.TryGetValue(t, out var d)) return d;
        d = new Dictionary<string, FieldInfoEx>();
        fieldCache[t] = d;
        var chain = new List<Type>();
        for (var b = t; b != null; b = SafeBase(b)) chain.Add(b);
        // du plus dérivé vers la base : le plus dérivé gagne
        foreach (var ty in chain)
        {
            FieldInfo[] fields;
            try { fields = ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var f in fields)
            {
                var ex = new FieldInfoEx { Field = f };
                var aliases = new List<string>();
                try
                {
                    foreach (var a in f.GetCustomAttributesData())
                    {
                        switch (a.AttributeType.FullName)
                        {
                            case "Verse.MustTranslateAttribute": ex.MustTranslate = true; break;
                            // the game refuses [Unsaved] fields like [NoTranslate] ones (DefInjectionPackage.SetDefFieldAtPath)
                            case "Verse.UnsavedAttribute":
                            case "Verse.NoTranslateAttribute": ex.NoTranslate = true; break;
                            case "Verse.TranslationCanChangeCountAttribute": ex.CanChangeCount = true; break;
                            case "Verse.TranslationHandleAttribute":
                                ex.HandlePriority = 0;
                                foreach (var na in a.NamedArguments)
                                    if (na.MemberName == "Priority" && na.TypedValue.Value is int p) ex.HandlePriority = p;
                                break;
                            case "Verse.LoadAliasAttribute":
                                var al = AttrArgString(a); if (al != null) aliases.Add(al);
                                break;
                        }
                    }
                }
                catch { }
                d.TryAdd(f.Name, ex);
                foreach (var al in aliases) d.TryAdd(al, ex);
            }
        }
        return d;
    }

    public FieldInfoEx? Field(Type t, string name) => FieldsOf(t).TryGetValue(name, out var f) ? f : null;

    public List<FieldInfoEx> HandleFields(Type t)
    {
        if (handleCache.TryGetValue(t, out var l)) return l;
        l = FieldsOf(t).Values.Where(f => f.HandlePriority != null).Distinct()
            .OrderByDescending(f => f.HandlePriority).ToList();
        handleCache[t] = l;
        return l;
    }

    readonly Dictionary<string, (PEReader pe, MetadataReader md, Dictionary<string, TypeDefinitionHandle> types)> peCache = new();

    (PEReader pe, MetadataReader md, Dictionary<string, TypeDefinitionHandle> types)? Pe(string path)
    {
        if (peCache.TryGetValue(path, out var c)) return c;
        try
        {
            var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            var d = new Dictionary<string, TypeDefinitionHandle>();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (!td.GetDeclaringType().IsNil) continue;
                var ns = md.GetString(td.Namespace); var n = md.GetString(td.Name);
                d.TryAdd(ns == "" ? n : ns + "." + n, th);
            }
            return peCache[path] = (pe, md, d);
        }
        catch { return null; }
    }

    /// <summary>Type affecté par défaut dans le constructeur (ex. compClass = typeof(CompX)), lu dans l'IL.</summary>
    public string? DefaultTypeAssigned(Type t, string fieldName)
    {
        for (var b = t; b != null; b = SafeBase(b))
        {
            try
            {
                var loc = b.Assembly.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                var p = Pe(loc);
                if (p == null || b.FullName == null || !p.Value.types.TryGetValue(b.FullName, out var th)) continue;
                var (pe, md, _) = p.Value;
                var td = md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.GetString(m.Name) != ".ctor" || m.RelativeVirtualAddress == 0) continue;
                    if (m.GetParameters().Count != 0) continue;
                    var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                    if (il == null) continue;
                    // Exact pattern "ldtoken T; call GetTypeFromHandle; stfld <fieldName>": a bare 0xD0 byte scan also
                    // matched operand bytes (e.g. a float constant) and returned an unrelated type (ThingComp for CompLaunchable).
                    for (int i = 0; i + 15 <= il.Length; i++)
                    {
                        if (il[i] != 0xD0 || il[i + 5] != 0x28 || il[i + 10] != 0x7D) continue;
                        int tok = BitConverter.ToInt32(il, i + 1);
                        var table = tok >> 24;
                        if (table != 0x01 && table != 0x02) continue;
                        var fh = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i + 11));
                        string? fname = fh.Kind switch
                        {
                            HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)fh).Name),
                            HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)fh).Name),
                            _ => null
                        };
                        if (fname != fieldName) continue;
                        var h = MetadataTokens.EntityHandle(tok);
                        if (h.Kind == HandleKind.TypeDefinition) return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
                        if (h.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    HashSet<string>? gameTKeys;

    /// <summary>
    /// "defName.TKey" known to the game. The table is hardcoded (Verse.TKeySystemHardcodedMapping, used instead of the
    /// TKey attributes of the XML): a TKey defined by a mod does not exist for the game, its key is the real path.
    /// </summary>
    public bool GameKnowsTKey(string tKeyPath)
    {
        if (gameTKeys == null)
        {
            gameTKeys = new();
            try
            {
                var t = Resolve("Verse.TKeySystemHardcodedMapping");
                var p = t == null ? null : Pe(t.Assembly.Location);
                if (p != null && p.Value.types.TryGetValue(t!.FullName!, out var th))
                {
                    var (pe, md, _) = p.Value;
                    foreach (var mh in md.GetTypeDefinition(th).GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        if (md.GetString(m.Name) != ".cctor" || m.RelativeVirtualAddress == 0) continue;
                        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>();
                        // dup ; ldstr key ; ldstr value ; callvirt Add
                        for (int i = 0; i + 11 < il.Length; i++)
                        {
                            if (il[i] != 0x25 || il[i + 1] != 0x72 || il[i + 6] != 0x72 || il[i + 11] != 0x6F) continue;
                            gameTKeys.Add(md.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, i + 2) & 0x00FFFFFF)));
                            i += 11;
                        }
                    }
                }
            }
            catch (Exception ex) { rep.Warn("TKeySystemHardcodedMapping illisible : " + ex.Message); }
            if (gameTKeys.Count == 0) rep.Warn("TKeySystemHardcodedMapping introuvable : tous les TKey sont supposés connus du jeu.");
        }
        return gameTKeys.Count == 0 || gameTKeys.Contains(tKeyPath);
    }

    readonly Dictionary<Type, Dictionary<string, string>> strDefaultCache = new();

    /// <summary>Valeurs littérales affectées aux champs string dans les constructeurs sans paramètre (initialiseurs de champ,
    /// ex. <c>leaderTitle = "leader"</c>), lues dans l'IL : le champ le plus dérivé l'emporte.</summary>
    public Dictionary<string, string> StringDefaults(Type t)
    {
        if (strDefaultCache.TryGetValue(t, out var res)) return res;
        res = new Dictionary<string, string>();
        strDefaultCache[t] = res;
        for (var b = t; b != null; b = SafeBase(b))
        {
            try
            {
                var loc = b.Assembly.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                var p = Pe(loc);
                if (p == null || b.FullName == null || !p.Value.types.TryGetValue(b.FullName, out var th)) continue;
                var (pe, md, _) = p.Value;
                var td = md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.GetString(m.Name) != ".ctor" || m.RelativeVirtualAddress == 0) continue;
                    if (m.GetParameters().Count != 0) continue;
                    var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                    if (il == null) continue;
                    // ldarg.0 ; ldstr <tok> ; stfld <tok>
                    for (int i = 0; i + 10 < il.Length; i++)
                    {
                        if (il[i] != 0x02 || il[i + 1] != 0x72 || il[i + 6] != 0x7D) continue;
                        int sTok = BitConverter.ToInt32(il, i + 2);
                        if ((sTok >> 24) != 0x70) continue;
                        int fTok = BitConverter.ToInt32(il, i + 7);
                        string? fname = null;
                        try
                        {
                            var eh = MetadataTokens.EntityHandle(fTok);
                            if (eh.Kind == HandleKind.FieldDefinition) fname = md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)eh).Name);
                            else if (eh.Kind == HandleKind.MemberReference) fname = md.GetString(md.GetMemberReference((MemberReferenceHandle)eh).Name);
                        }
                        catch { }
                        if (fname == null) continue;
                        string s;
                        try { s = md.GetUserString(MetadataTokens.UserStringHandle(sTok & 0x00FFFFFF)); } catch { continue; }
                        if (s.Length > 0) res.TryAdd(fname, s);
                    }
                }
            }
            catch { }
        }
        return res;
    }

    public void Dispose() { foreach (var v in peCache.Values) v.pe.Dispose(); mlc.Dispose(); }
}

/// <summary>Recherche de chaînes littérales utilisées dans les méthodes d'une DLL (pour retrouver où une clé Keyed est utilisée).</summary>
public static class DllStringScanner
{
    public static Dictionary<string, List<string>> Scan(string dllPath)
    {
        var res = new Dictionary<string, List<string>>();
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return res;
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                var tname = TypeName(md, td);
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (m.RelativeVirtualAddress == 0) continue;
                    byte[] il;
                    try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>(); } catch { continue; }
                    var mname = md.GetString(m.Name);
                    for (int i = 0; i < il.Length - 4; i++)
                    {
                        if (il[i] != 0x72 || il[i + 4] != 0x70) continue;
                        int off = BitConverter.ToInt32(il, i + 1) & 0x00FFFFFF;
                        try
                        {
                            var s = md.GetUserString(MetadataTokens.UserStringHandle(off));
                            if (s.Length is 0 or > 200) continue;
                            if (!res.TryGetValue(s, out var l)) res[s] = l = new();
                            var u = tname + "." + mname;
                            if (!l.Contains(u)) l.Add(u);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return res;
    }

    static string TypeName(MetadataReader md, TypeDefinition td)
    {
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeName(md, md.GetTypeDefinition(decl)) + "+" + name;
        var ns = md.GetString(td.Namespace);
        return ns == "" ? name : ns + "." + name;
    }
}

public static class Probe
{
    public static void Dump(TypeIndex idx, string name)
    {
        var t = idx.Resolve(name);
        if (t == null) { Console.WriteLine("introuvable"); return; }
        for (var b = t; b != null; b = TypeIndex.SafeBase(b))
        {
            Console.WriteLine("== " + b.FullName);
            foreach (var f in b.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var ats = string.Join(",", f.GetCustomAttributesData().Select(a => a.AttributeType.Name));
                if (f.FieldType.FullName == "System.String" || ats != "")
                    Console.WriteLine($"  {f.FieldType.Name} {f.Name}  [{ats}]");
            }
        }
    }
}

public static class Probe2
{
    public static void Ctor(TypeIndex idx, string name)
    {
        var t = idx.Resolve(name);
        if (t == null) { Console.WriteLine("introuvable"); return; }
        foreach (var c in t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            Console.WriteLine("ctor params=" + c.GetParameters().Length);
            try
            {
                var il = c.GetMethodBody()?.GetILAsByteArray();
                Console.WriteLine(il == null ? "no IL" : BitConverter.ToString(il));
                for (int i = 0; il != null && i < il.Length - 4; i++)
                    if (il[i] == 0xD0)
                    {
                        int tok = BitConverter.ToInt32(il, i + 1);
                        Console.WriteLine($"ldtoken {tok:X}");
                        try { Console.WriteLine(" -> " + c.Module.ResolveType(tok).FullName); } catch (Exception e) { Console.WriteLine(" ERR " + e.GetType().Name + e.Message); }
                    }
            }
            catch (Exception e) { Console.WriteLine("ERR " + e.Message); }
        }
    }
}
