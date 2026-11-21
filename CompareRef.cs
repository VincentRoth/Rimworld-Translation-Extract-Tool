using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TranslationExtractor;

/// <summary>
/// --compare-ref: compares our translation of a mod (out\&lt;packageId&gt;) with a third-party French translation
/// (a translation mod, or a folder). Read-only for the reference; writes out\&lt;packageId&gt;\compare-ref.csv.
///  - keys translated on both sides: same or different text (short different labels are listed: they reveal
///    contexts we missed, ex. "sway" read as recoil);
///  - keys only in the reference: looked up in the target mod, to tell a text our extraction missed from an old key.
/// </summary>
public static class CompareRef
{
    sealed class Val { public string Type = "", Key = "", Text = "", En = ""; }

    public static int Run(string target, string refArg, string outRoot, string lang, string placeholder, GameEnv env)
    {
        var modDir = LangFile.ModDir(target, outRoot);
        var ourLang = Path.Combine(modDir, "Languages", lang);
        if (!Directory.Exists(ourLang)) throw new Exception("Traduction introuvable : " + ourLang);
        var id = Path.GetFileName(modDir);

        string refRoot = Directory.Exists(refArg) ? Path.GetFullPath(refArg) : env.ResolveTarget(refArg).Root;
        // every Languages\French* folder of the reference; the current game version last, so it wins
        var refDirs = (Path.GetFileName(refRoot).StartsWith(lang, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(refRoot)) == "Languages"
                ? new List<string> { refRoot }
                : Directory.EnumerateDirectories(refRoot, lang + "*", SearchOption.AllDirectories)
                    .Where(d => Path.GetFileName(Path.GetDirectoryName(d)) == "Languages").ToList())
            .OrderBy(d => d.Contains(Path.DirectorySeparatorChar + env.Version + Path.DirectorySeparatorChar) ? 1 : 0).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (refDirs.Count == 0) throw new Exception("Aucun dossier Languages\\" + lang + "* dans " + refRoot);

        var ours = Load(new[] { ourLang }, placeholder);
        var theirs = Load(refDirs, placeholder);

        // target mod (if installed): current-version texts and every file, to classify reference-only keys
        string? current = null, everything = null;
        try
        {
            var mod = env.ResolveTarget(id);
            var saved = GameEnv.AssumeAll; GameEnv.AssumeAll = true;
            try { env.ComputeFolders(mod, new HashSet<string>()); } finally { GameEnv.AssumeAll = saved; }
            current = ReadAll(mod.Folders.SelectMany(f => new[] { "Defs", "Patches", Path.Combine("Languages", "English") }.Select(s => Path.Combine(f, s))));
            everything = ReadAll(new[] { mod.Root });
        }
        catch (Exception e) { Console.WriteLine("Mod cible introuvable, clés de la référence non classées : " + e.Message); }

        var rows = new List<string[]>();
        int same = 0, diff = 0, refOnlyCurrent = 0, refOnlyOld = 0, refOther = 0, oursOnly = 0;
        var shortDiff = new List<(string en, string key, string us, string them)>();
        foreach (var (k, o) in ours.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (theirs.TryGetValue(k, out var t))
            {
                bool eq = Norm(o.Text) == Norm(t.Text);
                if (eq) same++; else diff++;
                rows.Add(new[] { eq ? "identique" : "différent", o.Type, o.Key, o.En, o.Text, t.Text });
                if (!eq && o.En != "" && o.En.Length <= 40 && !o.Text.Contains('¶')) shortDiff.Add((o.En, o.Type + "/" + o.Key, o.Text, t.Text));
            }
            else { oursOnly++; rows.Add(new[] { "nous seuls", o.Type, o.Key, o.En, o.Text, "" }); }
        }
        foreach (var (k, t) in theirs.Where(x => !ours.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string state;
            if (current == null) state = "référence seule";
            else
            {
                var name = t.Type == "Keyed" ? t.Key : t.Key.Split('.')[0];
                var probe = t.Type == "Keyed" ? "<" + name + ">" : ">" + name + "<";
                if (Has(current, probe, name)) { state = "référence seule : texte du mod actuel (extraction ?)"; refOnlyCurrent++; }
                else if (Has(everything!, probe, name)) { state = "référence seule : ancienne version du mod"; refOnlyOld++; }
                else { state = "référence seule : absente du mod"; refOther++; }   // another mod of the same translation pack, or a removed key
            }
            if (current == null) refOnlyCurrent++;
            rows.Add(new[] { state, t.Type, t.Key, "", "", t.Text });
        }

        var csv = Path.Combine(modDir, "compare-ref.csv");
        var sb = new StringBuilder("etat;type;cle;anglais;nous;reference\n");
        foreach (var r in rows) sb.Append(string.Join(";", r.Select(Csv))).Append('\n');
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));

        Console.WriteLine($"Référence : {refRoot}  ({refDirs.Count} dossier(s) {lang})");
        Console.WriteLine($"Clés traduites des deux côtés : {same + diff} (identiques {same}, différentes {diff}) ; nous seuls : {oursOnly}");
        Console.WriteLine($"Référence seule : {refOnlyCurrent} dans le mod actuel (à vérifier : extraction manquée ?), {refOnlyOld} d'une ancienne version" +
                          (refOther > 0 ? $", {refOther} absentes du mod (autre mod couvert par la référence, ou clé supprimée)" : ""));
        if (shortDiff.Count > 0)
        {
            Console.WriteLine($"\nLibellés courts traduits différemment ({shortDiff.Count}) : relire, un contexte a pu nous échapper");
            foreach (var d in shortDiff.OrderBy(d => d.en, StringComparer.OrdinalIgnoreCase).Take(60))
                Console.WriteLine($"  {Cut(d.en, 30),-30}  nous : {Cut(d.us, 34),-34}  réf : {Cut(d.them, 34)}");
            if (shortDiff.Count > 60) Console.WriteLine($"  … {shortDiff.Count - 60} autres dans le CSV");
        }
        var missed = rows.Where(r => r[0].Contains("extraction")).Take(20).ToList();
        if (missed.Count > 0)
        {
            Console.WriteLine("\nClés de la référence présentes dans le mod actuel, absentes de notre traduction :");
            foreach (var r in missed) Console.WriteLine($"  {r[1]}/{r[2]}  réf : {Cut(r[5], 50)}");
        }
        Console.WriteLine($"\nDétail : {csv}");
        return 0;
    }

    static Dictionary<string, Val> Load(IEnumerable<string> dirs, string placeholder)
    {
        var d = new Dictionary<string, Val>();
        foreach (var dir in dirs)
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                var m = Regex.Match(f, @"[\\/]DefInjected[\\/]([^\\/]+)[\\/]");
                string type = m.Success ? m.Groups[1].Value : Regex.IsMatch(f, @"[\\/]Keyed[\\/]") ? "Keyed" : "";
                if (type == "") continue;
                var x = new XmlDocument();
                try { x.Load(f); } catch { continue; }
                if (x.DocumentElement == null) continue;
                string en = "";
                foreach (XmlNode n in x.DocumentElement.ChildNodes)
                {
                    if (n is XmlComment c)
                    {
                        var v = c.Value!.Trim();
                        if (v.StartsWith("EN:")) en = Regex.IsMatch(v, "<li>")
                            ? string.Join(Worksheet.ItemSep, Regex.Matches(v, @"<li>(.*?)</li>", RegexOptions.Singleline).Select(q => q.Groups[1].Value.Trim()))
                            : v[3..].Trim();
                        continue;
                    }
                    if (n is not XmlElement e) continue;
                    var lis = e.ChildNodes.OfType<XmlElement>().Where(c2 => c2.Name == "li").Select(c2 => c2.InnerText.Trim()).ToList();
                    var text = lis.Count > 0 ? string.Join(Worksheet.ItemSep, lis) : e.InnerText.Trim();
                    var thisEn = en; en = "";
                    if (text == "" || text == placeholder || lis.Contains(placeholder)) continue;
                    // a DefInjected key may be written with a subclass folder name: keep the key itself as identity
                    d[(type == "Keyed" ? "Keyed" : "Def") + "|" + e.Name] = new Val { Type = type, Key = e.Name, Text = text, En = thisEn };
                }
            }
        return d;
    }

    static string ReadAll(IEnumerable<string> dirs)
    {
        var sb = new StringBuilder();
        foreach (var d in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.EnumerateFiles(d, "*.xml", SearchOption.AllDirectories))
                try { sb.Append(File.ReadAllText(f)).Append('\n'); } catch { }
        return sb.ToString();
    }

    static bool Has(string text, string probe, string name) =>
        text.Contains(probe, StringComparison.Ordinal) || text.Contains("Name=\"" + name + "\"", StringComparison.Ordinal);

    // typography left out: quotes, spaces around punctuation, apostrophes, case
    static string Norm(string s) => Regex.Replace(
        Regex.Replace(s.Replace(' ', ' ').Replace(' ', ' ').Replace('«', '"').Replace('»', '"').Replace('’', '\''), @"\s+", " "),
        @"\s*([:;!?""%])\s*", "$1").Trim().ToLowerInvariant();
    static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
