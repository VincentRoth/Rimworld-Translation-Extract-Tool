using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// Line-level view of a language file written by <see cref="LanguageWriter"/>.
/// Only single-line entries are editable; list entries (&lt;li&gt;) and comments are left untouched.
/// </summary>
public sealed class LangFile
{
    public sealed class Item
    {
        public int Line;            // index of the element line
        public string Key = "";
        public string Value = "";   // raw inner XML (escaped)
        public string? En;          // text of the preceding "EN:" comment
        public bool EnChanged;      // an "ATTENTION" comment says the EN text changed since translation
        public bool AutoLabel;      // written by --fill-labels (review comment still present)
        public int NoteLine = -1;   // line of an "Auto" review comment, if any
        public bool IsTodo;
    }

    static readonly Regex EntryRx = new(@"^(\s*)<([^\s<>/!?]+)>(.*)</\2>\s*$");
    public string Path = "";
    public List<string> Lines = new();
    public List<Item> Items = new();
    public bool Dirty;

    public static LangFile Load(string path, string placeholder)
    {
        var f = new LangFile { Path = path };
        f.Lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
        string? en = null; bool changed = false, autoLabel = false; int noteLine = -1;
        for (int i = 0; i < f.Lines.Count; i++)
        {
            var t = f.Lines[i].Trim();
            if (t.StartsWith("<!--"))
            {
                if (t.StartsWith("<!-- EN:"))
                {
                    // single-line EN comment only; list entries span several lines and are skipped
                    en = t.EndsWith("-->") ? t[8..^3].Trim() : null;
                    changed = false; autoLabel = false; noteLine = -1;
                }
                else if (t.StartsWith("<!-- ATTENTION")) changed = true;
                else if (t.StartsWith("<!-- Auto ")) { noteLine = i; autoLabel = t.StartsWith(LabelNotePrefix); }
                continue;
            }
            var m = EntryRx.Match(f.Lines[i]);
            if (m.Success && m.Groups[2].Value != "li")
            {
                var v = m.Groups[3].Value;
                f.Items.Add(new Item { Line = i, Key = m.Groups[2].Value, Value = v, En = en, EnChanged = changed, IsTodo = v.Trim() == placeholder, AutoLabel = autoLabel, NoteLine = noteLine });
            }
            if (t.Length > 0 && !t.StartsWith("<!--")) { en = null; changed = false; autoLabel = false; noteLine = -1; }
        }
        return f;
    }

    public const string LabelNotePrefix = "<!-- Auto (libellé généré";

    /// <summary>Replace the value of an entry and put a short review comment above it (replacing a previous "Auto" comment).</summary>
    public void Set(Item it, string value, string? note)
    {
        var m = EntryRx.Match(Lines[it.Line]);
        var indent = m.Groups[1].Value;
        Lines[it.Line] = $"{indent}<{it.Key}>{value}</{it.Key}>";
        if (note != null)
        {
            var c = $"{indent}<!-- {San(note)} -->";
            if (it.NoteLine >= 0) Lines[it.NoteLine] = c;
            else Lines[it.Line] = c + "\n" + Lines[it.Line];
        }
        it.Value = value; it.IsTodo = false; Dirty = true;
    }

    static string San(string c) { while (c.Contains("--")) c = c.Replace("--", "- -"); return c; }

    public void Save()
    {
        if (!Dirty) return;
        File.WriteAllText(Path, string.Join("\n", Lines), new UTF8Encoding(true));
        Dirty = false;
    }

    /// <summary>Language files of a target: a packageId under out\, a mod output folder or a Languages\French folder.</summary>
    public static List<string> FindFiles(string target, string outRoot, string lang)
    {
        string dir = target;
        if (!Directory.Exists(dir) && Directory.Exists(System.IO.Path.Combine(outRoot, target))) dir = System.IO.Path.Combine(outRoot, target);
        if (!Directory.Exists(dir)) throw new Exception("Dossier introuvable : " + target);
        var langDir = System.IO.Path.Combine(dir, "Languages", lang);
        if (Directory.Exists(langDir)) dir = langDir;
        return Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Output folder of a target (out\packageId), used for the label tables.</summary>
    public static string ModDir(string target, string outRoot)
    {
        var dir = Directory.Exists(target) ? target : System.IO.Path.Combine(outRoot, target);
        var d = new DirectoryInfo(dir);
        // ...\out\<id>\Languages\French -> ...\out\<id>
        if (d.Name.Length > 0 && d.Parent?.Name == "Languages") d = d.Parent.Parent!;
        return d.FullName;
    }
}

/// <summary>
/// Translation memory: fills TODO entries with the translation of another entry that has exactly the same English text.
/// Same mod first, then (optionally) every mod under out\.
/// </summary>
public static class TranslationMemory
{
    public static int Run(string target, string outRoot, string lang, string placeholder, bool global)
    {
        var files = LangFile.FindFiles(target, outRoot, lang).Select(p => LangFile.Load(p, placeholder)).ToList();
        var targetPaths = files.Select(f => System.IO.Path.GetFullPath(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var local = BuildMemory(files);
        Dictionary<string, Candidate>? other = null;
        if (global)
        {
            var others = Directory.EnumerateDirectories(outRoot)
                .SelectMany(d => { var l = System.IO.Path.Combine(d, "Languages", lang); return Directory.Exists(l) ? Directory.EnumerateFiles(l, "*.xml", SearchOption.AllDirectories) : Enumerable.Empty<string>(); })
                .Where(p => !targetPaths.Contains(System.IO.Path.GetFullPath(p)))
                .Select(p => LangFile.Load(p, placeholder)).ToList();
            other = BuildMemory(others);
        }

        int todo = 0, fromLocal = 0, fromOther = 0, conflicts = 0;
        var conflictList = new List<string>();
        foreach (var f in files)
        {
            foreach (var it in f.Items.Where(i => i.IsTodo).ToList())
            {
                todo++;
                if (it.En == null) continue;
                Candidate? c = null; string scope = "";
                if (local.TryGetValue(it.En, out var lc)) { c = lc; scope = "même mod"; fromLocal++; }
                else if (other != null && other.TryGetValue(it.En, out var oc)) { c = oc; scope = "autre mod : " + oc.Source; fromOther++; }
                if (c == null) continue;
                if (c.Variants > 1) { conflicts++; if (conflictList.Count < 30) conflictList.Add($"{it.En}  →  {c.Value}  ({c.Variants} traductions différentes, la plus fréquente est prise)"); }
                f.Set(it, c.Value, $"Auto (mémoire de traduction, {scope}) : à relire");
            }
            f.Save();
        }

        Console.WriteLine($"Mémoire de traduction : {local.Count} textes anglais déjà traduits dans le mod" + (other != null ? $", {other.Count} dans les autres mods de out\\" : ""));
        Console.WriteLine($"Entrées TODO : {todo}  ->  remplies : {fromLocal + fromOther} (même mod : {fromLocal}" + (other != null ? $", autres mods : {fromOther}" : "") + $"), restantes : {todo - fromLocal - fromOther}");
        // what is left: how many distinct English texts must be typed to clear the remaining TODOs with this command
        var left = files.SelectMany(f => f.Items).Where(i => i.IsTodo).ToList();
        if (left.Count > 0)
        {
            var groups = left.Where(i => i.En != null).GroupBy(i => i.En!).OrderByDescending(g => g.Count()).ToList();
            Console.WriteLine($"Reste : {left.Count} TODO pour {groups.Count} textes anglais distincts" + (left.Count(i => i.En == null) is int nl && nl > 0 ? $" (+{nl} listes <li>)" : "") +
                              " : traduire une occurrence de chacun puis relancer --fill-tm remplit les autres.");
            foreach (var g in groups.Take(10)) Console.WriteLine($"  {g.Count(),5} x  {(g.Key.Length > 90 ? g.Key[..90] + "…" : g.Key)}");
        }
        if (conflicts > 0)
        {
            Console.WriteLine($"{conflicts} entrées remplies alors que le même anglais a plusieurs traductions (à harmoniser) :");
            foreach (var s in conflictList) Console.WriteLine("  " + s);
        }
        return 0;
    }

    sealed class Candidate { public string Value = ""; public int Variants; public string Source = ""; }

    static Dictionary<string, Candidate> BuildMemory(List<LangFile> files)
    {
        var counts = new Dictionary<string, Dictionary<string, (int n, string src)>>();
        foreach (var f in files)
            foreach (var it in f.Items)
            {
                if (it.IsTodo || it.En == null || it.EnChanged || it.Value.Trim() == "" || it.Value.Contains("TODO")) continue;
                if (!counts.TryGetValue(it.En, out var d)) counts[it.En] = d = new();
                d[it.Value] = d.TryGetValue(it.Value, out var x) ? (x.n + 1, x.src) : (1, ModName(f.Path));
            }
        return counts.ToDictionary(kv => kv.Key, kv =>
        {
            var best = kv.Value.OrderByDescending(v => v.Value.n).First();
            return new Candidate { Value = best.Key, Variants = kv.Value.Count, Source = best.Value.src };
        });
    }

    static string ModName(string path)
    {
        // ...\out\<packageId>\Languages\...
        var parts = path.Replace('/', '\\').Split('\\');
        var i = Array.IndexOf(parts, "Languages");
        return i > 0 ? parts[i - 1] : "";
    }
}
