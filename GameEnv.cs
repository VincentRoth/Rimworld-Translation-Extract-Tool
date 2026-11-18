using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TranslationExtractor;

public sealed class ModMeta
{
    public string Name = "";
    public string PackageId = "";   // minuscule, sans suffixe _steam
    public string Root = "";
    public bool Official;
    public List<string> Deps = new();
    public List<string> LoadAfter = new();
    public List<string> Folders = new();  // dossiers de chargement calculés
    public List<string> SupportedVersions = new();  // <supportedVersions> de About.xml

    public override string ToString() => $"{Name} [{PackageId}]";
}

public sealed class Report
{
    public readonly List<string> Warnings = new();
    public readonly List<string> UnsupportedPatches = new();
    public readonly List<string> UnknownTypes = new();
    public readonly List<string> MissingMods = new();
    public readonly Dictionary<string, int> SkippedUnmarked = new();
    public readonly List<string> Notes = new();
    public string? ShippedText;
    public void Warn(string s) { if (!Warnings.Contains(s)) Warnings.Add(s); }
}

public sealed class GameEnv
{
    public string GameDir = @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld";
    public string WorkshopDir = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100";
    public string Version = "";
    public readonly List<ModMeta> Index = new();

    static readonly string[] OfficialOrder = { "Core", "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey" };
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "Languages", "Textures", "Defs", "Sounds", "Assemblies", "Patches", "Source", "obj", "bin", "Common", "Presets" };

    public string ManagedDir => Path.Combine(GameDir, "RimWorldWin64_Data", "Managed");

    public static string NormId(string id)
    {
        id = id.Trim().ToLowerInvariant();
        if (id.EndsWith("_steam")) id = id[..^6];
        if (id.EndsWith("_copy")) id = id[..^5];
        return id;
    }

    public void Scan()
    {
        var vf = Path.Combine(GameDir, "Version.txt");
        if (File.Exists(vf))
        {
            var m = Regex.Match(File.ReadAllText(vf), @"^(\d+\.\d+)");
            if (m.Success && Version == "") Version = m.Groups[1].Value;
        }
        var dataDir = Path.Combine(GameDir, "Data");
        foreach (var name in OfficialOrder)
        {
            var d = Path.Combine(dataDir, name);
            if (File.Exists(Path.Combine(d, "About", "About.xml")))
            {
                var mm = ParseAbout(d);
                if (mm != null) { mm.Official = true; Index.Add(mm); }
            }
        }
        var local = Path.Combine(GameDir, "Mods");
        if (Directory.Exists(local))
            foreach (var d in Directory.GetDirectories(local)) ScanDir(d, 0);
        if (Directory.Exists(WorkshopDir))
            foreach (var d in Directory.GetDirectories(WorkshopDir)) ScanDir(d, 0);
    }

    void ScanDir(string dir, int depth)
    {
        if (File.Exists(Path.Combine(dir, "About", "About.xml")))
        {
            var m = ParseAbout(dir);
            if (m != null && !Index.Any(x => x.Root == m.Root)) Index.Add(m);
        }
        if (depth >= 4) return;
        IEnumerable<string> subs;
        try { subs = Directory.GetDirectories(dir); } catch { return; }
        foreach (var s in subs)
        {
            var n = Path.GetFileName(s);
            if (SkipDirs.Contains(n) || n == "About") continue;
            ScanDir(s, depth + 1);
        }
    }

    public ModMeta? ParseAbout(string root)
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(root, "About", "About.xml"));
            var r = doc.Root!;
            var m = new ModMeta
            {
                Root = Path.GetFullPath(root),
                Name = (string?)r.Element("name") ?? Path.GetFileName(root),
                PackageId = NormId((string?)r.Element("packageId") ?? ""),
            };
            void AddIds(XElement? list, List<string> into, bool deps)
            {
                if (list == null) return;
                foreach (var li in list.Elements("li"))
                {
                    var id = deps ? (string?)li.Element("packageId") : li.Value;
                    if (!string.IsNullOrWhiteSpace(id)) into.Add(NormId(id));
                }
            }
            AddIds(r.Element("modDependencies"), m.Deps, true);
            AddIds(r.Element("modDependenciesByVersion")?.Element("v" + Version), m.Deps, true);
            AddIds(r.Element("loadAfter"), m.LoadAfter, false);
            AddIds(r.Element("loadAfterByVersion")?.Element("v" + Version), m.LoadAfter, false);
            AddIds(r.Element("forceLoadAfter"), m.LoadAfter, false);
            foreach (var li in r.Element("supportedVersions")?.Elements("li") ?? Enumerable.Empty<XElement>()) m.SupportedVersions.Add(li.Value.Trim());
            return m;
        }
        catch { return null; }
    }

    public ModMeta? FindByPackageId(string id)
    {
        id = NormId(id);
        return BestCopy(Index.Where(m => m.PackageId == id));
    }

    /// <summary>Plusieurs copies d'un même mod (même packageId, ex. DMS 1.6 et DMS 1.7 publiés séparément) :
    /// garder celle qui déclare la version visée, sinon la plus haute version déclarée &lt;= visée, sinon la première.</summary>
    public ModMeta? BestCopy(IEnumerable<ModMeta> copies)
    {
        var list = copies.ToList();
        if (list.Count <= 1 || !System.Version.TryParse(Version, out var cur)) return list.FirstOrDefault();
        System.Version? Best(ModMeta m) => m.SupportedVersions
            .Select(s => System.Version.TryParse(s, out var v) ? v : null).Where(v => v != null && v <= cur).Max();
        return list.Where(m => m.Official).FirstOrDefault()
            ?? list.OrderByDescending(m => m.SupportedVersions.Contains(Version) ? 1 : 0).ThenByDescending(m => Best(m)).First();
    }

    public ModMeta ResolveTarget(string arg)
    {
        ModMeta? Pick(string dir)
        {
            if (File.Exists(Path.Combine(dir, "About", "About.xml")))
                return Index.FirstOrDefault(m => m.Root == Path.GetFullPath(dir)) ?? ParseAbout(dir);
            var found = new List<ModMeta>();
            void Walk(string d, int depth)
            {
                if (File.Exists(Path.Combine(d, "About", "About.xml")))
                { var mm = ParseAbout(d); if (mm != null) found.Add(mm); return; }
                if (depth >= 4) return;
                foreach (var s in Directory.GetDirectories(d))
                    if (!SkipDirs.Contains(Path.GetFileName(s))) Walk(s, depth + 1);
            }
            Walk(dir, 0);
            if (found.Count == 1) return found[0];
            if (found.Count > 1)
                throw new Exception("Plusieurs mods dans ce dossier, précisez le chemin ou le packageId :\n  " +
                    string.Join("\n  ", found.Select(f => $"{f.Name} [{f.PackageId}] {f.Root}")));
            return null;
        }

        if (Directory.Exists(arg)) { var r = Pick(arg); if (r != null) return r; }
        var byId = FindByPackageId(arg);
        if (byId != null) return byId;
        if (Regex.IsMatch(arg, @"^\d+$"))
        {
            var d = Path.Combine(WorkshopDir, arg);
            if (Directory.Exists(d)) { var r = Pick(d); if (r != null) return r; }
        }
        var exact = Index.Where(m => m.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1 || exact.Count > 1 && (exact.Any(x => x.Official) || exact.Select(x => x.PackageId).Distinct().Count() == 1)) return BestCopy(exact);
        var byName = Index.Where(m => m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1 || byName.Count > 1 && byName.Select(x => x.PackageId).Distinct().Count() == 1) return BestCopy(byName);
        if (byName.Count > 1)
            throw new Exception("Nom ambigu :\n  " + string.Join("\n  ", byName.Select(f => $"{f.Name} [{f.PackageId}]")));
        throw new Exception($"Mod introuvable : {arg}");
    }

    /// <summary>Calcule l'ordre de chargement : officiel, puis dépendances, puis cible.</summary>
    public List<ModMeta> BuildLoadOrder(ModMeta target, IEnumerable<string> extra, Report rep)
    {
        var active = new List<ModMeta>();
        var seen = new HashSet<string>();
        void Add(ModMeta m) { if (seen.Add(m.Root)) active.Add(m); }

        if (target.Official && target.PackageId == "ludeon.rimworld")
        {
            Add(target);
        }
        else
        {
            foreach (var o in Index.Where(i => i.Official)) Add(o);
        }

        void Closure(ModMeta m)
        {
            foreach (var d in m.Deps)
            {
                var dm = FindByPackageId(d);
                if (dm == null) { rep.MissingMods.Add($"{m.Name} dépend de {d} (introuvable)"); continue; }
                if (seen.Contains(dm.Root)) continue;
                Closure(dm);
                Add(dm);
            }
        }
        Closure(target);
        foreach (var e in extra)
        {
            var em = FindByPackageId(e) ?? Index.FirstOrDefault(i => i.Name.Equals(e, StringComparison.OrdinalIgnoreCase));
            if (em == null) { rep.MissingMods.Add($"--with {e} introuvable"); continue; }
            Closure(em);
            Add(em);
        }
        Add(target);

        // tri topologique léger sur loadAfter / dépendances (stable)
        var ordered = new List<ModMeta>();
        var visited = new HashSet<string>();
        void Visit(ModMeta m)
        {
            if (!visited.Add(m.Root)) return;
            foreach (var d in m.Deps.Concat(m.LoadAfter))
            {
                var dm = active.FirstOrDefault(a => a.PackageId == d);
                if (dm != null && !dm.Official) Visit(dm);
            }
            ordered.Add(m);
        }
        foreach (var o in active.Where(a => a.Official)) { visited.Add(o.Root); ordered.Add(o); }
        foreach (var m in active.Where(a => !a.Official && a != target)) Visit(m);
        if (!target.Official || !ordered.Contains(target)) Visit(target);
        return ordered;
    }

    /// <summary>Mode par défaut : toutes les conditions (IfModActive, MayRequire, FindMod...) sont supposées remplies, pour extraire tous les textes du mod.</summary>
    public static bool AssumeAll = true;

    public static bool IsActive(string expr, HashSet<string> activeIds, bool any)
    {
        if (AssumeAll) return true;
        var ids = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormId).ToList();
        if (ids.Count == 0) return true;
        return any ? ids.Any(activeIds.Contains) : ids.All(activeIds.Contains);
    }

    public void ComputeFolders(ModMeta m, HashSet<string> activeIds)
    {
        m.Folders.Clear();
        var lf = Path.Combine(m.Root, "LoadFolders.xml");
        if (File.Exists(lf))
        {
            try
            {
                var doc = XDocument.Load(lf);
                // comme ModContentPack.InitLoadFolders : la version courante, sinon la plus haute version déclarée <= courante,
                // sinon "default" (ex. un LoadFolders limité à <v1.5> est utilisé tel quel en 1.6)
                var node = doc.Root!.Element("v" + Version);
                if (node == null && System.Version.TryParse(Version, out var curV))
                    node = doc.Root.Elements()
                        .Select(e => (e, ok: e.Name.LocalName.StartsWith("v") && System.Version.TryParse(e.Name.LocalName[1..], out var v) ? v : null))
                        .Where(x => x.ok != null && x.ok <= curV).OrderByDescending(x => x.ok).Select(x => x.e).FirstOrDefault();
                node ??= doc.Root.Element("default");
                if (node != null)
                {
                    foreach (var li in node.Elements("li"))
                    {
                        var ifa = (string?)li.Attribute("IfModActive");
                        var ifn = (string?)li.Attribute("IfModNotActive");
                        var ifany = (string?)li.Attribute("IfModActiveAny");
                        if (!AssumeAll) {
                        if (ifa != null && !IsActive(ifa, activeIds, false)) continue;
                        if (ifany != null && !IsActive(ifany, activeIds, true)) continue;
                        if (ifn != null && IsActive(ifn, activeIds, true)) continue;
                        }
                        var p = li.Value.Trim();
                        var full = (p == "" || p == "/" || p == ".") ? m.Root : Path.GetFullPath(Path.Combine(m.Root, p));
                        if (Directory.Exists(full) && !m.Folders.Contains(full)) m.Folders.Add(full);
                    }
                    return;
                }
            }
            catch { }
        }
        m.Folders.Add(m.Root);
        var common = Path.Combine(m.Root, "Common");
        if (Directory.Exists(common)) m.Folders.Add(common);
        // meilleure version <= courante
        var cur = Version.Split('.').Select(int.Parse).ToArray();
        string? best = null; Version? bestV = null;
        foreach (var d in Directory.GetDirectories(m.Root))
        {
            var n = Path.GetFileName(d).TrimStart('v');
            if (System.Version.TryParse(n, out var v) && v.Major == cur[0] && v.Minor <= cur[1])
                if (bestV == null || v > bestV) { bestV = v; best = d; }
        }
        if (best != null) m.Folders.Add(best);
    }
}
