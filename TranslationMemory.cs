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
        public bool Rep;            // <rep><path>…</path><trans>…</trans></rep> (LangXml)
        public int EndLine = -1;    // list entry: last line of the element
    }

    static readonly Regex RepRx = new(@"^(\s*)<rep><path>(.*?)</path><trans>(.*)</trans></rep>\s*$");
    static readonly Regex ListOpen = new(@"^(\s*)<([^\s<>/!?]+)>\s*$");
    static readonly Regex LiLine = new(@"<li>(.*?)</li>", RegexOptions.Singleline);
    const string ListEn = "\u0001list\u0001";
    static string LiText(string s) => ListEn + string.Join("\u0001", LiLine.Matches(s).Select(m => m.Groups[1].Value.Trim()));

    static readonly Regex EntryRx = new(@"^(\s*)<([^\s<>/!?]+)>(.*)</\2>\s*$");
    public string Path = "";
    public List<string> Lines = new();
    public List<Item> Items = new();
    /// <summary>List entries (several lines, &lt;li&gt;): Value = inner XML, En = items of the EN comment.</summary>
    public List<Item> Lists = new();
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
                    changed = false; autoLabel = false; noteLine = -1;
                    if (t.EndsWith("-->")) en = t[8..^3].Trim();
                    else
                    {
                        // list entry: the EN comment holds one <li> per line
                        int j = i; while (j < f.Lines.Count && !f.Lines[j].Contains("-->")) j++;
                        var block = string.Join("\n", f.Lines.Skip(i).Take(j - i + 1));
                        en = block.Contains("<li>") ? LiText(block) : null;
                        i = j;
                    }
                }
                else if (t.StartsWith("<!-- ATTENTION")) changed = true;
                else if (t.StartsWith("<!-- Auto ")) { noteLine = i; autoLabel = t.StartsWith(LabelNotePrefix); }
                continue;
            }
            var rm = RepRx.Match(f.Lines[i]);
            var m = rm.Success ? rm : EntryRx.Match(f.Lines[i]);
            var lo = ListOpen.Match(f.Lines[i]);
            if (lo.Success && lo.Groups[2].Value != "LanguageData")
            {
                int j = i + 1; while (j < f.Lines.Count && f.Lines[j].Trim() != $"</{lo.Groups[2].Value}>") j++;
                if (j < f.Lines.Count)
                {
                    var inner = string.Join("\n", f.Lines.Skip(i + 1).Take(j - i - 1));
                    var vals = LiLine.Matches(inner).Select(x => x.Groups[1].Value.Trim()).ToList();
                    if (vals.Count > 0)
                        f.Lists.Add(new Item { Line = i, EndLine = j, Key = lo.Groups[2].Value, Value = inner, En = en != null && en.StartsWith(ListEn) ? en : null,
                            EnChanged = changed, NoteLine = noteLine, IsTodo = vals.All(x => x == placeholder) });
                    i = j;
                }
            }
            else if (m.Success && m.Groups[2].Value != "li")
            {
                var v = m.Groups[3].Value;
                f.Items.Add(new Item { Line = i, Rep = rm.Success, Key = m.Groups[2].Value, Value = v, En = en != null && en.StartsWith(ListEn) ? null : en, EnChanged = changed, IsTodo = v.Trim() == placeholder, AutoLabel = autoLabel, NoteLine = noteLine });
            }
            if (t.Length > 0 && !t.StartsWith("<!--")) { en = null; changed = false; autoLabel = false; noteLine = -1; }
        }
        return f;
    }

    /// <summary>English text for display (a list shows its items).</summary>
    public static string ShowEn(string en, int max = 200)
    {
        if (en.StartsWith(ListEn)) en = en[ListEn.Length..].Replace("\u0001", " ¶ ");
        return en.Length > max ? en[..max] + "…" : en;
    }

    public const string LabelNotePrefix = "<!-- Auto (libellé généré";

    /// <summary>Replace the value of an entry and put a short review comment above it (replacing a previous "Auto" comment).</summary>
    public void Set(Item it, string value, string? note)
    {
        var indent = Regex.Match(Lines[it.Line], @"^\s*").Value;
        if (it.EndLine > it.Line)
        {
            // list: the whole block goes on its first line, the following ones are dropped by Save (line indexes stay valid)
            Lines[it.Line] = $"{indent}<{it.Key}>\n{value}\n{indent}</{it.Key}>";
            for (int l = it.Line + 1; l <= it.EndLine; l++) Lines[l] = null!;
        }
        else Lines[it.Line] = it.Rep ? $"{indent}<rep><path>{it.Key}</path><trans>{value}</trans></rep>" : $"{indent}<{it.Key}>{value}</{it.Key}>";
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
        File.WriteAllText(Path, string.Join("\n", Lines.Where(l => l != null)), new UTF8Encoding(true));
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
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(target)) return RunAll(outRoot, lang, placeholder);
        var files = LangFile.FindFiles(target, outRoot, lang).Select(p => LangFile.Load(p, placeholder)).ToList();
        var targetPaths = files.Select(f => System.IO.Path.GetFullPath(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sameDef = SameDefCopies(files, outRoot);
        if (sameDef > 0) Console.WriteLine($"Même texte qu'un autre champ de la def (implicit-rules.json, sameText) : {sameDef} entrées remplies");

        var local = BuildMemory(files);
        Dictionary<string, List<Entry>>? other = null;
        if (global)
        {
            var others = Directory.EnumerateDirectories(outRoot)
                .SelectMany(d => { var l = System.IO.Path.Combine(d, "Languages", lang); return Directory.Exists(l) ? Directory.EnumerateFiles(l, "*.xml", SearchOption.AllDirectories) : Enumerable.Empty<string>(); })
                .Where(p => !targetPaths.Contains(System.IO.Path.GetFullPath(p)))
                .Select(p => LangFile.Load(p, placeholder)).ToList();
            other = BuildMemory(others);
        }

        int todo = 0, fromLocal = 0, fromOther = 0, conflicts = 0, skipped = 0;
        var conflictList = new List<string>();
        foreach (var f in files)
        {
            foreach (var it in f.Items.Concat(f.Lists).Where(i => i.IsTodo).ToList())
            {
                todo++;
                if (it.En == null) continue;
                string scope = "même mod";
                var c = Find(local, f, it, true, ref skipped);
                if (c != null) fromLocal++;
                else if (other != null && (c = Find(other, f, it, false, ref skipped)) != null) { scope = "autre mod : " + c.Source; fromOther++; }
                if (c == null) continue;
                if (c.Variants > 1) { conflicts++; if (conflictList.Count < 60) conflictList.Add($"{LangFile.ShowEn(it.En)}  →  {c.Value.Trim()}  ({c.Variants} traductions différentes, la plus fréquente est prise)"); }
                f.Set(it, c.Value, $"Auto (mémoire de traduction, {scope}) : à relire");
            }
            f.Save();
        }

        Console.WriteLine($"Mémoire de traduction : {local.Count} textes anglais déjà traduits dans le mod" + (other != null ? $", {other.Count} dans les autres mods de out\\" : ""));
        Console.WriteLine($"Entrées TODO : {todo}  ->  remplies : {fromLocal + fromOther} (même mod : {fromLocal}" + (other != null ? $", autres mods : {fromOther}" : "") + $"), restantes : {todo - fromLocal - fromOther}");
        // what is left: how many distinct English texts must be typed to clear the remaining TODOs with this command
        var left = files.SelectMany(f => f.Items.Concat(f.Lists)).Where(i => i.IsTodo).ToList();
        if (left.Count > 0)
        {
            var groups = left.Where(i => i.En != null).GroupBy(i => i.En!).OrderByDescending(g => g.Count()).ToList();
            Console.WriteLine($"Reste : {left.Count} TODO pour {groups.Count} textes anglais distincts" + (left.Count(i => i.En == null) is int nl && nl > 0 ? $" (+{nl} sans texte anglais)" : "") +
                              " : traduire une occurrence de chacun puis relancer --fill-tm remplit les autres.");
            foreach (var g in groups.Take(10)) Console.WriteLine($"  {g.Count(),5} x  {LangFile.ShowEn(g.Key, 90)}");
        }
        if (skipped > 0) Console.WriteLine($"{skipped} recherches sans suite : même anglais connu ailleurs (autre type ou champ, texte court) ou traductions divergentes.");
        if (conflicts > 0)
        {
            Console.WriteLine($"{conflicts} entrées remplies alors que le même anglais a plusieurs traductions (à harmoniser) :");
            foreach (var s in conflictList) Console.WriteLine("  " + s);
        }
        return 0;
    }

    /// <summary>
    /// "--fill-tm all": every mod under out\ at once, with one memory built from all of them
    /// (same as running --fill-tm --tm-global on each mod, without reloading out\ for every mod).
    /// The memory is built before filling, so a value written by this run is never reused in the same run.
    /// </summary>
    static int RunAll(string outRoot, string lang, string placeholder)
    {
        var byMod = Directory.EnumerateDirectories(outRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => (mod: System.IO.Path.GetFileName(d), dir: System.IO.Path.Combine(d, "Languages", lang)))
            .Where(x => Directory.Exists(x.dir))
            .Select(x => (x.mod, files: Directory.EnumerateFiles(x.dir, "*.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => LangFile.Load(p, placeholder)).ToList()))
            .ToList();
        var global = BuildMemory(byMod.SelectMany(m => m.files).ToList());

        int todo = 0, fromLocal = 0, fromOther = 0, conflicts = 0, skipped = 0, mods = 0;
        int sameDefTotal = 0;
        var conflictList = new List<string>();
        foreach (var (mod, files) in byMod)
        {
            sameDefTotal += SameDefCopies(files, outRoot);
            var local = BuildMemory(files);
            int before = fromLocal + fromOther;
            foreach (var f in files)
            {
                foreach (var it in f.Items.Concat(f.Lists).Where(i => i.IsTodo).ToList())
                {
                    todo++;
                    if (it.En == null) continue;
                    string scope = "même mod";
                    var c = Find(local, f, it, true, ref skipped);
                    if (c != null) fromLocal++;
                    else if ((c = Find(global, f, it, false, ref skipped)) != null) { scope = "autre mod : " + c.Source; fromOther++; }
                    if (c == null) continue;
                    if (c.Variants > 1) { conflicts++; if (conflictList.Count < 60) conflictList.Add($"{LangFile.ShowEn(it.En)}  →  {c.Value.Trim()}  ({c.Variants} traductions différentes, la plus fréquente est prise)"); }
                    f.Set(it, c.Value, $"Auto (mémoire de traduction, {scope}) : à relire");
                }
                f.Save();
            }
            if (fromLocal + fromOther > before) mods++;
        }

        Console.WriteLine($"Même texte qu'un autre champ de la def (implicit-rules.json, sameText) : {sameDefTotal} entrées remplies");
        Console.WriteLine($"Mémoire de traduction (tout out\\) : {global.Count} textes anglais déjà traduits, {byMod.Count} mods");
        Console.WriteLine($"Entrées TODO : {todo}  ->  remplies : {fromLocal + fromOther} dans {mods} mods (même mod : {fromLocal}, autres mods : {fromOther}), restantes : {todo - fromLocal - fromOther}");
        var left = byMod.SelectMany(m => m.files).SelectMany(f => f.Items.Concat(f.Lists)).Where(i => i.IsTodo).ToList();
        var groups = left.Where(i => i.En != null).GroupBy(i => i.En!).OrderByDescending(g => g.Count()).ToList();
        Console.WriteLine($"Reste : {left.Count} TODO pour {groups.Count} textes anglais distincts.");
        foreach (var g in groups.Take(15)) Console.WriteLine($"  {g.Count(),5} x  {LangFile.ShowEn(g.Key, 90)}");
        if (skipped > 0) Console.WriteLine($"{skipped} recherches sans suite : même anglais connu ailleurs (autre type ou champ, texte court) ou traductions divergentes.");
        if (conflicts > 0)
        {
            Console.WriteLine($"{conflicts} entrées remplies alors que le même anglais a plusieurs traductions (à harmoniser) :");
            foreach (var s in conflictList) Console.WriteLine("  " + s);
        }
        return 0;
    }

    /// <summary>
    /// Same-def copies (rules of implicit-rules.json with "sameText"): a TODO entry whose English is copied by the game
    /// from another field of the same def (scenario.name = label, SkillDef.label = skillLabel, label of a single verb =
    /// item name) takes the translation of that field when the English texts are identical.
    /// </summary>
    static int SameDefCopies(List<LangFile> files, string outRoot)
    {
        var rules = CompanionFile.Load(System.IO.Path.Combine(outRoot, "..", "implicit-rules.json")).Companions
            .Where(r => r.SameText && !r.From.Contains('|')).ToList();
        if (rules.Count == 0) return 0;
        // pattern of the target key and the source key, from the def name
        string Seg(string s) => s == "" ? "" : Regex.Escape(s) + @"\.";
        var compiled = rules.Select(r =>
        {
            var obj = Seg(r.Path) + (r.List == "" ? "" : Regex.Escape(r.List) + @"\.[^.]+\.");
            var target = new Regex(@"^(?<def>[^.]+)\." + obj + Regex.Escape(r.Field) + "$");
            Func<string, string, string> source = (def, key) => r.From.StartsWith('^')
                ? def + "." + r.From[1..]
                : key[..^r.Field.Length] + r.From;
            return (type: r.Type, target, source, label: r.From.TrimStart('^'));
        }).ToList();
        var done = new Dictionary<(string type, string key, string en), string>();
        foreach (var f in files)
            foreach (var it in f.Items)
                if (!it.IsTodo && it.En != null && !it.EnChanged && !it.Value.Contains("TODO"))
                    done[(Context(f.Path, it.Key).type, it.Key, it.En)] = it.Value;
        int n = 0;
        foreach (var f in files)
        {
            var type = Context(f.Path, "").type;
            foreach (var it in f.Items.Where(i => i.IsTodo && i.En != null).ToList())
                foreach (var c in compiled.Where(c => c.type == type))
                {
                    var m = c.target.Match(it.Key);
                    if (!m.Success || !done.TryGetValue((type, c.source(m.Groups["def"].Value, it.Key), it.En!), out var v)) continue;
                    f.Set(it, v, $"Auto (même texte que {c.label} de la def) : à relire");
                    n++;
                    break;
                }
        }
        return n;
    }

    // where a text sits: def type + place in the def ("label", "tools/label"); Keyed texts are one context
    internal static (string type, string shape) Context(string path, string key)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
        int di = Array.LastIndexOf(parts, "DefInjected");
        return di >= 0 && di + 1 < parts.Length ? (parts[di + 1], OfficialFill.Shape(key)) : ("Keyed", "");
    }

    sealed class Candidate { public string Value = ""; public int Variants; public string Source = ""; }
    sealed class Entry { public string Type = "", Shape = "", Value = "", Source = ""; public int N; }

    static Dictionary<string, List<Entry>> BuildMemory(List<LangFile> files)
    {
        var mem = new Dictionary<string, List<Entry>>();
        foreach (var f in files)
            foreach (var it in f.Items.Concat(f.Lists))
            {
                if (it.IsTodo || it.En == null || it.EnChanged || it.Value.Trim() == "" || it.Value.Contains("TODO")) continue;
                var (type, shape) = Context(f.Path, it.Key);
                if (!mem.TryGetValue(it.En, out var l)) mem[it.En] = l = new();
                var e = l.FirstOrDefault(x => x.Type == type && x.Shape == shape && x.Value == it.Value);
                if (e == null) l.Add(new Entry { Type = type, Shape = shape, Value = it.Value, Source = ModName(f.Path), N = 1 });
                else e.N++;
            }
        return mem;
    }

    static HashSet<(string, string)>? excluded;
    const int LongText = 5; // words: from there on a text means the same thing wherever it sits

    /// <summary>
    /// Translation of the same English text: same def type and field first; a text of another place only when it is
    /// long enough, or in the same mod for the same field name. Nothing when the known translations disagree
    /// (the most frequent one must cover two thirds).
    /// </summary>
    static Candidate? Find(Dictionary<string, List<Entry>> mem, LangFile f, LangFile.Item it, bool sameMod, ref int skipped)
    {
        if (!mem.TryGetValue(it.En!, out var all)) return null;
        var (type, shape) = Context(f.Path, it.Key);
        if ((excluded ??= OfficialFill.Exclusions(Program.PackDir, "memoire")).Contains((type, it.En!))) { skipped++; return null; }
        var l = all.Where(e => e.Type == type && e.Shape == shape).ToList();
        if (l.Count == 0 && sameMod && type != "Keyed") l = all.Where(e => e.Type != "Keyed" && e.Shape == shape).ToList();
        if (l.Count == 0 && LangFile.ShowEn(it.En!, 100000).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= LongText) l = all;
        if (l.Count == 0) { skipped++; return null; }
        var g = l.GroupBy(e => e.Value).Select(x => (v: x.Key, n: x.Sum(e => e.N), src: x.First().Source)).OrderByDescending(x => x.n).ToList();
        if (g[0].n * 3 < g.Sum(x => x.n) * 2) { skipped++; return null; }
        return new Candidate { Value = g[0].v, Variants = g.Count, Source = g[0].src };
    }

    static string ModName(string path)
    {
        // ...\out\<packageId>\Languages\...
        var parts = path.Replace('/', '\\').Split('\\');
        var i = Array.IndexOf(parts, "Languages");
        return i > 0 ? parts[i - 1] : "";
    }
}
