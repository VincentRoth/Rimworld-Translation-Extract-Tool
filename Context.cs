using System.Formats.Tar;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TranslationExtractor;

public sealed class Rule
{
    public string Def { get; set; } = "*";
    public string Field { get; set; } = "*";
    public string Context { get; set; } = "";
    public int? Max { get; set; }
    public bool Short { get; set; }
    public bool Soft { get; set; }   // limite indicative (noms d'objets) : avertissement au lieu d'erreur
}

public sealed class RuleFile
{
    public List<Rule> DefRules { get; set; } = new();
    public List<Rule> KeyedRules { get; set; } = new();   // Field = motif sur le nom de clé, Def ignoré
}

public sealed class ContextResolver
{
    readonly RuleFile rules;

    public ContextResolver(string jsonPath)
    {
        rules = File.Exists(jsonPath)
            ? JsonSerializer.Deserialize<RuleFile>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new()
            : new();
    }

    static bool Glob(string pattern, string s) =>
        pattern == "*" || Regex.IsMatch(s, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    public (string? ctx, int? max, bool isShort) ForDef(Entry e)
    {
        foreach (var r in rules.DefRules)
        {
            if (r.Def != "*" && !e.TypeChain.Contains(r.Def)) continue;
            if (!Glob(r.Field, e.Path)) continue;
            return (r.Context, r.Max, r.Short);
        }
        return (null, null, false);
    }

    public bool IsSoftDef(Entry e)
    {
        foreach (var r in rules.DefRules)
        {
            if (r.Def != "*" && !e.TypeChain.Contains(r.Def)) continue;
            if (!Glob(r.Field, e.Path)) continue;
            return r.Soft;
        }
        return false;
    }

    public (string? ctx, int? max, bool isShort) ForKeyed(KeyedEntry k)
    {
        foreach (var r in rules.KeyedRules)
            if (Glob(r.Field, k.Key)) return (r.Context, r.Max, r.Short);
        return (null, null, false);
    }

    static readonly Regex Placeholders = new(@"\{[^{}]+\}|\[[^\[\]]+\]|\$\w+|%\w+|<[^<>]+>", RegexOptions.Compiled);

    public static List<string> FindPlaceholders(string s) =>
        Placeholders.Matches(s).Select(m => m.Value).Distinct().ToList();

    public static string UsageHint(List<string> usages)
    {
        var hints = new HashSet<string>();
        foreach (var u in usages)
        {
            var l = u.ToLowerInvariant();
            if (l.Contains("dowindowcontents") || l.Contains("dialog_") || l.Contains("window_") || l.Contains("dosettingswindow")) hints.Add("fenêtre/dialogue");
            if (l.Contains("gizmo")) hints.Add("bouton de commande (gizmo)");
            if (l.Contains("floatmenu") || l.Contains("getfloatmenuoptions")) hints.Add("menu contextuel");
            if (l.Contains("inspectstring")) hints.Add("texte d'inspection");
            if (l.Contains("itab") || l.Contains("filltab")) hints.Add("onglet");
            if (l.Contains("message")) hints.Add("message à l'écran");
            if (l.Contains("letter")) hints.Add("lettre");
            if (l.Contains("tooltip") || l.Contains("tip")) hints.Add("infobulle");
            if (l.Contains("settings")) hints.Add("options du mod");
        }
        return string.Join(", ", hints);
    }
}

/// <summary>Traductions françaises officielles (Core + DLC), lues en mémoire dans les .tar.</summary>
public sealed class OfficialFr
{
    readonly Dictionary<string, string> defs = new();
    readonly Dictionary<string, string> keyed = new();
    bool loaded;
    readonly string dataDir;
    readonly string? only;
    public OfficialFr(string gameDir, string? only = null) { dataDir = Path.Combine(gameDir, "Data"); this.only = only; }

    void Load()
    {
        if (loaded) return; loaded = true;
        if (!Directory.Exists(dataDir)) return;
        foreach (var dlc in Directory.GetDirectories(dataDir))
        {
            if (only != null && Path.GetFileName(dlc) != only) continue;
            var langDir = Path.Combine(dlc, "Languages");
            if (!Directory.Exists(langDir)) continue;
            foreach (var tar in Directory.GetFiles(langDir, "French*.tar"))
            {
                try
                {
                    using var fs = File.OpenRead(tar);
                    using var tr = new TarReader(fs);
                    TarEntry? te;
                    while ((te = tr.GetNextEntry()) != null)
                    {
                        if (te.DataStream == null || !te.Name.EndsWith(".xml")) continue;
                        var parts = te.Name.Replace('\\', '/').Split('/');
                        bool isDef = parts.Length >= 3 && parts[0] == "DefInjected";
                        bool isKeyed = parts.Length >= 2 && parts[0] == "Keyed";
                        if (!isDef && !isKeyed) continue;
                        XDocument doc;
                        try { doc = XDocument.Load(te.DataStream); } catch { continue; }
                        foreach (var el in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
                        {
                            var val = el.HasElements ? string.Join(" | ", el.Elements("li").Select(x => x.Value)) : el.Value;
                            if (isDef) defs[parts[1] + "/" + el.Name.LocalName] = val;
                            else keyed[el.Name.LocalName] = val;
                        }
                    }
                }
                catch { }
            }
        }
    }

    public string? Def(string folder, string key) { Load(); return defs.GetValueOrDefault(folder + "/" + key); }
    public string? Keyed(string key) { Load(); return keyed.GetValueOrDefault(key); }
    public HashSet<string> AllDefKeys() { Load(); return defs.Keys.ToHashSet(); }
}
