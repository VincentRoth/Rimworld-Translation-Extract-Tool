using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// Label generator for "material x pattern" mods (Flooring Floors...).
/// A label such as "sapphire crosshatch M tile" is split into a pattern template "{M} crosshatch M tile"
/// and a material "sapphire". Both are translated by hand in two CSV tables; the generator then fills
/// the TODO labels with the French pattern, {M} replaced by the French material.
///
/// The split is inferred from the labels themselves (defNames do not always name the material shown:
/// KS_StoneGravelKObsidian is "schorl gravel paving"): for each label, every span of 1-3 words is tried
/// as the material, and the split whose template is shared by the most materials, and whose material is
/// used by the most templates, wins.
/// </summary>
public static class LabelGen
{
    public const string PatternsFile = "label-patterns.csv", MaterialsFile = "label-materials.csv";
    const int MaxMaterialWords = 3;

    sealed class Label { public LangFile File = null!; public LangFile.Item Item = null!; public string[] Words = Array.Empty<string>(); }

    // ---------- label collection ----------

    static List<Label> Collect(IEnumerable<string> targets, string outRoot, string lang, string placeholder, string folder, out List<LangFile> files)
    {
        files = new();
        var res = new List<Label>();
        foreach (var t in targets)
            foreach (var p in LangFile.FindFiles(t, outRoot, lang))
            {
                var dirName = Path.GetFileName(Path.GetDirectoryName(p)!);
                if (!string.Equals(dirName, folder, StringComparison.OrdinalIgnoreCase)) continue;
                var f = LangFile.Load(p, placeholder);
                files.Add(f);
                foreach (var it in f.Items.Where(i => i.Key.EndsWith(".label") && i.En != null))
                    res.Add(new Label { File = f, Item = it, Words = it.En!.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
            }
        return res;
    }

    static IEnumerable<(string template, string material)> Splits(string[] w)
    {
        for (int len = 1; len <= MaxMaterialWords && len < w.Length; len++)
            for (int i = 0; i + len <= w.Length; i++)
            {
                var mat = string.Join(' ', w, i, len);
                if (mat.Contains('{')) continue;   // never take a placeholder as material
                var tpl = string.Join(' ', w.Take(i).Append("{M}").Concat(w.Skip(i + len)));
                yield return (tpl, mat);
            }
    }

    static string K(string s) => s.ToLowerInvariant();

    /// <summary>Best split of each distinct label (lower case), from template/material co-occurrence statistics.</summary>
    static Dictionary<string, (string template, string material)> Decompose(List<Label> labels, int minShare)
    {
        var distinct = labels.GroupBy(l => K(l.Item.En!)).Select(g => g.First().Words).ToList();
        var tplMats = new Dictionary<string, HashSet<string>>();
        var matTpls = new Dictionary<string, HashSet<string>>();
        foreach (var w in distinct)
            foreach (var (t, m) in Splits(w))
            {
                var kt = K(t); var km = K(m);
                if (!tplMats.TryGetValue(kt, out var a)) tplMats[kt] = a = new();
                a.Add(km);
                if (!matTpls.TryGetValue(km, out var b)) matTpls[km] = b = new();
                b.Add(kt);
            }
        var res = new Dictionary<string, (string, string)>();
        foreach (var w in distinct)
        {
            // score = min(materials sharing the template, templates using the material): a wrong split such as
            // "emerald {M} tile" (material "random") has a pattern word as material, used by many templates,
            // but its template is shared by few "materials". On a tie, the earlier material position wins
            // (Splits yields positions left to right, so only a strictly better score replaces the best).
            (string t, string m)? best = null; long bestScore = 0;
            foreach (var (t, m) in Splits(w))
            {
                int nt = tplMats[K(t)].Count, nm = matTpls[K(m)].Count;
                if (nt < minShare || nm < minShare) continue;
                long score = Math.Min(nt, nm);
                if (score > bestScore) { bestScore = score; best = (t, m); }
            }
            if (best != null) res[K(string.Join(' ', w))] = best.Value;
        }

        // Refinement: re-split each label with the number of labels actually assigned to each template and
        // material by the previous pass. Irregular families (metal tiles: "stainless steel arx tile" next to
        // "copper arx tile") then follow the majority instead of creating one-off templates.
        for (int round = 0; round < 5; round++)
        {
            var tA = res.Values.GroupBy(s => K(s.Item1)).ToDictionary(g => g.Key, g => g.Count());
            var mA = res.Values.GroupBy(s => K(s.Item2)).ToDictionary(g => g.Key, g => g.Count());
            int changed = 0;
            foreach (var w in distinct)
            {
                var key = K(string.Join(' ', w));
                (string t, string m)? best = null; long bestScore = 0;
                foreach (var (t, m) in Splits(w))
                {
                    if (tplMats[K(t)].Count < minShare || matTpls[K(m)].Count < minShare) continue;
                    long score = tA.GetValueOrDefault(K(t)) + mA.GetValueOrDefault(K(m));
                    if (score > bestScore) { bestScore = score; best = (t, m); }
                }
                if (best == null) continue;
                if (!res.TryGetValue(key, out var cur) || cur != best.Value) { res[key] = best.Value; changed++; }
            }
            if (changed == 0) break;
        }
        // drop splits left without support (labels with no material, typos): translated by hand or by the TM
        var tF = res.Values.GroupBy(s => K(s.Item1)).ToDictionary(g => g.Key, g => g.Count());
        var mF = res.Values.GroupBy(s => K(s.Item2)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var k in res.Where(kv => tF[K(kv.Value.Item1)] < minShare || mF[K(kv.Value.Item2)] < minShare).Select(kv => kv.Key).ToList())
            res.Remove(k);
        return res;
    }

    // ---------- tables ----------

    sealed class Row { public string En = "", Fr = "", Extra = "", Example = "", Suggestion = ""; public int Count; }

    static Dictionary<string, Row> ReadTable(string path, bool materials)
    {
        var res = new Dictionary<string, Row>();
        if (!File.Exists(path)) return res;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "") continue;
            var c = Csv.Split(lines[i]);
            string Col(int n) => n < c.Count ? c[n] : "";
            var r = materials
                ? new Row { En = Col(0), Fr = Col(1), Extra = Col(2), Count = int.TryParse(Col(3), out var n1) ? n1 : 0, Example = Col(4), Suggestion = Col(5) }
                : new Row { En = Col(0), Fr = Col(1), Count = int.TryParse(Col(2), out var n2) ? n2 : 0, Example = Col(3) };
            if (r.En != "") res[K(r.En)] = r;
        }
        return res;
    }

    static void WriteTable(string path, IEnumerable<Row> rows, bool materials)
    {
        var sb = new StringBuilder();
        sb.Append(materials ? "en;fr;elision;count;example;suggestion\n" : "en;fr;count;example\n");
        foreach (var r in rows.OrderByDescending(r => r.Count).ThenBy(r => r.En, StringComparer.OrdinalIgnoreCase))
        {
            var cols = materials
                ? new[] { r.En, r.Fr, r.Extra, r.Count.ToString(), r.Example, r.Suggestion }
                : new[] { r.En, r.Fr, r.Count.ToString(), r.Example };
            sb.Append(string.Join(";", cols.Select(Csv.Quote))).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>EN label (lower case) -> French, from every translated .label under out\ (to suggest material names).</summary>
    static Dictionary<string, string> KnownLabels(string outRoot, string lang, string placeholder)
    {
        var res = new Dictionary<string, Dictionary<string, int>>();
        foreach (var d in Directory.EnumerateDirectories(outRoot))
        {
            var l = Path.Combine(d, "Languages", lang, "DefInjected");
            if (!Directory.Exists(l)) continue;
            foreach (var p in Directory.EnumerateFiles(l, "*.xml", SearchOption.AllDirectories))
            {
                LangFile f;
                try { f = LangFile.Load(p, placeholder); } catch { continue; }
                foreach (var it in f.Items.Where(i => !i.IsTodo && i.En != null && !i.EnChanged && (i.Key.EndsWith(".label") || i.Key.EndsWith(".stuffAdjective"))))
                {
                    if (!res.TryGetValue(K(it.En!), out var c)) res[K(it.En!)] = c = new();
                    c[it.Value] = c.GetValueOrDefault(it.Value) + 1;
                }
            }
        }
        return res.ToDictionary(kv => kv.Key, kv => string.Join(" / ", kv.Value.OrderByDescending(x => x.Value).Select(x => x.Key)));
    }

    // ---------- commands ----------

    public static int Tables(string[] targets, string outRoot, string lang, string placeholder, string? tablesDir, string folder, int minShare)
    {
        tablesDir ??= LangFile.ModDir(targets[0], outRoot);
        var labels = Collect(targets, outRoot, lang, placeholder, folder, out _);
        var split = Decompose(labels, minShare);
        var pPath = Path.Combine(tablesDir, PatternsFile); var mPath = Path.Combine(tablesDir, MaterialsFile);
        var pats = ReadTable(pPath, false); var mats = ReadTable(mPath, true);
        foreach (var r in pats.Values.Concat(mats.Values)) r.Count = 0;
        var known = KnownLabels(outRoot, lang, placeholder);

        int unsplit = 0; var unsplitEx = new List<string>();
        foreach (var l in labels)
        {
            if (!split.TryGetValue(K(l.Item.En!), out var s)) { unsplit++; if (unsplitEx.Count < 25) unsplitEx.Add(l.Item.En!); continue; }
            if (!pats.TryGetValue(K(s.template), out var p)) pats[K(s.template)] = p = new Row { En = s.template };
            if (!mats.TryGetValue(K(s.material), out var m)) mats[K(s.material)] = m = new Row { En = s.material };
            p.Count++; m.Count++;
            if (p.Example == "") p.Example = l.Item.En!;
            if (m.Example == "") m.Example = l.Item.En!;
        }
        foreach (var m in mats.Values) m.Suggestion = known.GetValueOrDefault(K(m.En)) ?? "";
        Directory.CreateDirectory(tablesDir);
        WriteTable(pPath, pats.Values, false);
        WriteTable(mPath, mats.Values, true);

        Console.WriteLine($"Libellés {folder} : {labels.Count} (TODO : {labels.Count(l => l.Item.IsTodo)}), décomposés : {labels.Count - unsplit}, non décomposés : {unsplit}");
        Console.WriteLine($"Motifs : {pats.Values.Count(r => r.Count > 0)} (traduits : {pats.Values.Count(r => r.Count > 0 && r.Fr != "")})  ->  {pPath}");
        Console.WriteLine($"Matériaux : {mats.Values.Count(r => r.Count > 0)} (traduits : {mats.Values.Count(r => r.Count > 0 && r.Fr != "")}, suggestion trouvée dans out\\ : {mats.Values.Count(r => r.Count > 0 && r.Suggestion != "")})  ->  {mPath}");
        if (unsplitEx.Count > 0) Console.WriteLine("Exemples non décomposés (à traduire à la main ou par la mémoire de traduction) : " + string.Join(" | ", unsplitEx.Distinct()));
        return 0;
    }

    static readonly Regex Elide = new(@"\b([Dd])e ([aeiouyàâäéèêëîïôöûùüœæAEIOUYÀÂÄÉÈÊËÎÏÔÖÛÙÜŒÆ])");

    public static int Fill(string[] targets, string outRoot, string lang, string placeholder, string? tablesDir, string folder, int minShare, bool relabel = false)
    {
        tablesDir ??= LangFile.ModDir(targets[0], outRoot);
        var pPath = Path.Combine(tablesDir, PatternsFile); var mPath = Path.Combine(tablesDir, MaterialsFile);
        if (!File.Exists(pPath) || !File.Exists(mPath)) { Console.Error.WriteLine($"Tables absentes dans {tablesDir} : lancer d'abord --label-tables."); return 2; }
        var pats = ReadTable(pPath, false).Where(kv => kv.Value.Fr != "").ToDictionary(kv => kv.Key, kv => kv.Value);
        var mats = ReadTable(mPath, true).Where(kv => kv.Value.Fr != "").ToDictionary(kv => kv.Key, kv => kv.Value);
        var labels = Collect(targets, outRoot, lang, placeholder, folder, out var files);
        var split = Decompose(labels, minShare);

        int todo = 0, filled = 0, noSplit = 0, noPat = 0, noMat = 0;
        var missP = new Dictionary<string, int>(); var missM = new Dictionary<string, int>();
        foreach (var l in labels.Where(l => l.Item.IsTodo || (relabel && l.Item.AutoLabel)))
        {
            todo++;
            if (!split.TryGetValue(K(l.Item.En!), out var s)) { noSplit++; continue; }
            bool hp = pats.TryGetValue(K(s.template), out var p), hm = mats.TryGetValue(K(s.material), out var m);
            if (!hp) { noPat++; missP[s.template] = missP.GetValueOrDefault(s.template) + 1; }
            if (!hm) { noMat++; missM[s.material] = missM.GetValueOrDefault(s.material) + 1; }
            if (!hp || !hm) continue;
            if (!p!.Fr.Contains("{M}")) { Console.Error.WriteLine($"Motif sans {{M}} ignoré : {p.En} -> {p.Fr}"); continue; }
            var fr = p.Fr.Replace("{M}", m!.Fr);
            var el = m.Extra.Trim().ToLowerInvariant();
            if (el is "oui" or "yes" or "o" or "y") fr = Regex.Replace(fr, @"\b([Dd])e " + Regex.Escape(m.Fr), "$1'" + m.Fr.Replace("$", "$$"));
            else if (el is not ("non" or "no" or "n")) fr = Elide.Replace(fr, "$1'$2");
            l.File.Set(l.Item, Esc(fr), $"Auto (libellé généré : motif « {s.template} », matériau « {s.material} ») : à relire");
            filled++;
        }
        foreach (var f in files) f.Save();

        Console.WriteLine($"Libellés TODO{(relabel ? " ou déjà générés" : "")} ({folder}) : {todo}  ->  générés : {filled}, restants : {todo - filled}");
        Console.WriteLine($"  non décomposés : {noSplit} ; motif non traduit : {noPat} ; matériau non traduit : {noMat}");
        if (missP.Count > 0) Console.WriteLine("  Motifs manquants (top 15) : " + string.Join(" | ", missP.OrderByDescending(x => x.Value).Take(15).Select(x => $"{x.Key} ({x.Value})")));
        if (missM.Count > 0) Console.WriteLine("  Matériaux manquants (top 15) : " + string.Join(" | ", missM.OrderByDescending(x => x.Value).Take(15).Select(x => $"{x.Key} ({x.Value})")));
        return 0;
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");
}

/// <summary>Minimal CSV with ';' separator and "..." quoting (Excel, French locale).</summary>
static class Csv
{
    public static string Quote(string s) => s.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static List<string> Split(string line)
    {
        var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ';') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res.Select(x => x.Trim()).ToList();
    }
}
