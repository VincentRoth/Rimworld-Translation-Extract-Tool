using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// "--fill-official": fills TODO entries with the official translation (Officiel.csv) of the same English text.
/// Strict on purpose, the same English word changes meaning with its use: same def type and same place in the def (Shape)
/// (or same key for Keyed), one single official translation, and no glossary term that differs from it.
/// </summary>
public static class OfficialFill
{
    public static int Run(string target, string outRoot, string lang, string placeholder, string packDir, string? reportFile, bool dryRun)
    {
        var csvPath = Path.Combine(packDir, "Officiel.csv");
        if (!File.Exists(csvPath)) throw new Exception("Officiel.csv introuvable : " + csvPath);
        var official = new Dictionary<(string type, string field, string en), HashSet<string>>();
        foreach (var c in Rows(csvPath))
        {
            if (c.Count < 5 || c[1].Contains('\n')) continue;
            // rule lists (index fields) depend on the rules around them
            var field = c[3] == "Keyed" ? c[4] : Shape(c[4]);
            if (field == "" || int.TryParse(c[2], out _)) continue;
            var k = (c[3], field, c[0]);
            if (!official.TryGetValue(k, out var set)) official[k] = set = new();
            set.Add(c[1]);
        }
        // glossary terms validated with another translation than the official one
        var glossary = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var glossPath = Path.Combine(packDir, "Glossaire.csv");
        if (File.Exists(glossPath))
            foreach (var c in Rows(glossPath))
                if (c.Count > 1 && c[0] != "")
                {
                    if (!glossary.TryGetValue(c[0], out var set)) glossary[c[0]] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(c[1]);
                }

        // official texts that are wrong outside their own def (type;english;reason)
        var excluded = Exclusions(packDir, "officiel");

        var mods = target.Equals("all", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(target)
            ? Directory.EnumerateDirectories(outRoot).Where(d => Directory.Exists(Path.Combine(d, "Languages", lang))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { target };
        var report = new StringBuilder("\uFEFFmod;type;cle;anglais;francais\n");
        int todo = 0, filled = 0, several = 0, gloss = 0, excl = 0, modCount = 0;
        foreach (var mod in mods)
        {
            int before = filled;
            foreach (var path in LangFile.FindFiles(mod, outRoot, lang))
            {
                var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
                int di = Array.LastIndexOf(parts, "DefInjected");
                var type = di >= 0 ? parts[di + 1] : parts.Contains("Keyed") ? "Keyed" : "";
                if (type == "") continue;
                var f = LangFile.Load(path, placeholder);
                foreach (var it in f.Items.Where(i => i.IsTodo).ToList())
                {
                    todo++;
                    if (string.IsNullOrEmpty(it.En)) continue;
                    var field = type == "Keyed" ? it.Key : Shape(it.Key);
                    if (!official.TryGetValue((type, field, it.En), out var fr)) continue;
                    if (excluded.Contains((type, it.En))) { excl++; continue; }
                    if (fr.Count > 1) { several++; continue; }
                    var v = fr.First();
                    if (glossary.TryGetValue(it.En, out var g) && !g.Contains(v)) { gloss++; continue; }
                    f.Set(it, v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), "Auto (officiel) : à relire");
                    filled++;
                    report.Append(string.Join(";", new[] { Path.GetFileName(LangFile.ModDir(mod, outRoot)), type, it.Key, it.En, v }.Select(Csv.Quote))).Append('\n');
                }
                if (!dryRun) f.Save();
            }
            if (filled > before) modCount++;
        }
        if (reportFile != null) File.WriteAllText(reportFile, report.ToString());
        Console.WriteLine($"Officiel : {official.Count} textes (type, champ, anglais){(dryRun ? " - essai, rien n'est écrit" : "")}");
        Console.WriteLine($"Entrées TODO : {todo}  ->  remplies : {filled} dans {modCount} mods, restantes : {todo - filled}");
        Console.WriteLine($"Laissées à TODO : {several} (plusieurs traductions officielles), {gloss} (le glossaire donne un autre terme), {excl} (Remplissage_exclusions.csv)");
        return 0;
    }

    /// <summary>Where the text sits: "label" (the def itself) or "tools/label" (a melee tool), without the def name and list handles.</summary>
    internal static string Shape(string key)
    {
        var p = key.Split('.');
        return p.Length <= 2 ? p[^1] : p[1] + "/" + p[^1];
    }

    /// <summary>(def type, English) pairs that must not be filled automatically: Remplissage_exclusions.csv (source;type;english;reason), source = officiel or memoire.</summary>
    internal static HashSet<(string, string)> Exclusions(string packDir, string source)
    {
        var res = new HashSet<(string, string)>();
        var p = Path.Combine(packDir, "Remplissage_exclusions.csv");
        if (File.Exists(p)) foreach (var c in Rows(p)) if (c.Count > 2 && c[0] == source) res.Add((c[1], c[2]));
        return res;
    }

    static IEnumerable<List<string>> Rows(string path) =>
        File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l => Csv.Split(l).ToList());
}
