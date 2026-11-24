using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TranslationExtractor;

/// <summary>
/// Translation worksheets: --export-todo writes the TODO entries of one or more mods to a plain text file
/// (one line per entry, English text and context in comments above it); --import writes the filled lines back.
/// Line format: "&lt;packageId&gt; || &lt;file under Languages\French&gt; || &lt;key&gt; || &lt;translation&gt;".
/// A list entry has the key "&lt;key&gt;[]" and its items separated by " ¶ ". Only entries still at TODO are replaced.
/// </summary>
public static class Worksheet
{
    public const string Sep = " || ";
    public const string ItemSep = " ¶ ";

    // comment block + entry whose value is the placeholder, or a list made only of placeholder items
    static Regex EntryRx(string ph) => new(
        @"(?s)((?:[ \t]*<!--(?:(?!-->).)*-->[ \t]*\r?\n)*)([ \t]*)<([^\s<>/!?]+)>(" + Regex.Escape(ph) +
        @"|\s*(?:<li>" + Regex.Escape(ph) + @"</li>\s*)+)</\3>" +
        // key that is not a valid element name (LangXml)
        @"|(?s)((?:[ \t]*<!--(?:(?!-->).)*-->[ \t]*\r?\n)*)[ \t]*<rep><path>([^<]*)</path><trans>" + Regex.Escape(ph) + "</trans></rep>");

    static readonly Regex Li = new(@"<li>(.*?)</li>", RegexOptions.Singleline);

    sealed class Todo { public string Rel = "", Key = "", En = "", Info = ""; public bool List; public int Items; }

    static List<Todo> Scan(string langDir, string ph)
    {
        var res = new List<Todo>();
        var rx = EntryRx(ph);
        foreach (var f in Directory.EnumerateFiles(langDir, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var t = File.ReadAllText(f);
            var rel = Path.GetRelativePath(langDir, f);
            foreach (Match m in rx.Matches(t))
            {
                bool rep = m.Groups[6].Success;
                var c = m.Groups[rep ? 5 : 1].Value;
                bool list = m.Groups[4].Value.Contains("<li>");
                var en = Regex.Match(c, @"<!-- EN:(.*?)-->", RegexOptions.Singleline).Groups[1].Value;
                if (list) en = string.Join(ItemSep, Li.Matches(en).Select(x => x.Groups[1].Value.Trim()));
                else en = Regex.Replace(en.Trim(), @"\s*\r?\n\s*", " ");
                var info = new List<string>();
                var ctx = Regex.Match(c, @"<!-- Contexte: (.*?) -->").Groups[1].Value;
                if (ctx != "") info.Add(ctx);
                var lim = Regex.Match(c, @"max=(\d+)( soft)?");
                if (lim.Success) info.Add("max " + lim.Groups[1].Value + (lim.Groups[2].Success ? " (souple)" : ""));
                var keep = Regex.Match(c, @"<!-- Conserver tels quels : (.*?) -->").Groups[1].Value;
                if (keep != "") info.Add("garder : " + keep);
                var off = Regex.Match(c, @"<!-- FR du jeu de base : (.*?) -->").Groups[1].Value;
                if (off != "") info.Add("officiel : " + off);
                if (c.Contains("<!-- ATTENTION")) info.Add("texte anglais modifié");
                res.Add(new Todo { Rel = rel, Key = rep ? System.Net.WebUtility.HtmlDecode(m.Groups[6].Value) : m.Groups[3].Value, En = en, Info = string.Join(" | ", info), List = list, Items = list ? Li.Matches(m.Groups[4].Value).Count : 0 });
            }
        }
        return res;
    }

    public static int Export(string[] targets, string outRoot, string lang, string ph, string? toFile, int batch, string toolDir)
    {
        var mods = targets.Length == 1 && targets[0].Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Directory.EnumerateDirectories(outRoot).Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()!
            : targets;
        var all = new List<(string id, Todo t)>();
        foreach (var id in mods)
        {
            var modDir = LangFile.ModDir(id!, outRoot);
            var langDir = Path.Combine(modDir, "Languages", lang);
            if (!Directory.Exists(langDir)) { if (targets.Length > 1 || mods != targets) continue; throw new Exception("Dossier introuvable : " + langDir); }
            all.AddRange(Scan(langDir, ph).Select(t => (Path.GetFileName(modDir), t)));
        }
        if (all.Count == 0) { Console.WriteLine("Aucune entrée TODO."); return 0; }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = toFile ?? Path.Combine(toolDir, "feuilles",
            $"todo_{(mods.Length == 1 ? mods[0] : mods[0] + "+" + (mods.Length - 1))}_{stamp}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(baseName))!);
        int size = batch > 0 ? batch : all.Count;
        int parts = (all.Count + size - 1) / size;
        for (int p = 0; p < parts; p++)
        {
            var file = parts == 1 ? baseName : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baseName))!,
                Path.GetFileNameWithoutExtension(baseName) + $"_{p + 1}" + Path.GetExtension(baseName));
            if (File.Exists(file)) throw new Exception("Le fichier existe déjà (une feuille remplie serait écrasée) : " + file);
            var sb = new StringBuilder();
            sb.Append("# Feuille de traduction, ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).Append(parts > 1 ? $" (partie {p + 1}/{parts})" : "").Append('\n');
            sb.Append("# Écrire la traduction après le dernier « || », sans toucher au reste de la ligne. Lignes laissées vides : ignorées.\n");
            sb.Append("# Listes (clé[]) : éléments séparés par « ¶ » ; le nombre d'éléments peut changer pour les règles (rulesStrings).\n");
            sb.Append("# Texte brut : <color>, & et \\n s'écrivent tels quels. Import : extract.cmd --import <ce fichier>\n");
            string? lastMod = null;
            foreach (var (id, t) in all.Skip(p * size).Take(size))
            {
                if (id != lastMod) { sb.Append("\n# ===== ").Append(id).Append(" =====\n"); lastMod = id; }
                sb.Append("\n# EN: ").Append(t.En).Append('\n');
                if (t.Info != "") sb.Append("#     ").Append(t.Info).Append('\n');
                sb.Append(id).Append(Sep).Append(t.Rel).Append(Sep).Append(t.Key).Append(t.List ? "[]" : "").Append(Sep).Append('\n');
            }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"Feuille : {file}");
        }
        Console.WriteLine($"{all.Count} entrées TODO exportées ({all.Count(x => x.t.List)} listes), {all.Select(x => x.id).Distinct().Count()} mods, {parts} fichier(s).");
        return 0;
    }

    public static int Import(string sheet, string outRoot, string lang, string ph)
    {
        if (!File.Exists(sheet)) throw new Exception("Feuille introuvable : " + sheet);
        var lines = new List<(string id, string rel, string key, string fr, int n)>();
        int n = 0, empty = 0, bad = 0;
        foreach (var raw in File.ReadAllLines(sheet, Encoding.UTF8))
        {
            n++;
            var line = raw.TrimEnd();
            if (line.Trim() == "" || line.TrimStart().StartsWith("#")) continue;
            var p = line.Split(Sep.Trim(), 4);
            if (p.Length < 4) { if (p.Length == 3) { empty++; continue; } Console.WriteLine($"  ligne {n} illisible : {line}"); bad++; continue; }
            var fr = p[3].Trim();
            if (fr == "") { empty++; continue; }
            lines.Add((p[0].Trim(), p[1].Trim(), p[2].Trim(), fr, n));
        }

        int done = 0, notFound = 0, errors = 0;
        var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in lines.GroupBy(l => (l.id, l.rel)))
        {
            var path = Path.Combine(LangFile.ModDir(g.Key.id, outRoot), "Languages", lang, g.Key.rel);
            if (!File.Exists(path)) { foreach (var l in g) Console.WriteLine($"  ligne {l.n} : fichier introuvable {g.Key.id}\\{g.Key.rel}"); notFound += g.Count(); continue; }
            var text = File.ReadAllText(path);
            var orig = text;
            var applied = new List<int>();
            foreach (var l in g)
            {
                bool list = l.key.EndsWith("[]");
                var key = list ? l.key[..^2] : l.key;
                var rx = new Regex("(" + Regex.Escape(LangXml.Open(key)) + ")(" + Regex.Escape(ph) + @"|(\s*<li>" + Regex.Escape(ph) + @"</li>)+(\s*))(" + Regex.Escape(LangXml.Close(key)) + ")");
                var m = rx.Match(text);
                if (!m.Success) { Console.WriteLine($"  ligne {l.n} : {key} n'est plus à TODO ou absent ({g.Key.rel})"); notFound++; continue; }
                string inner;
                if (list)
                {
                    var indent = Regex.Match(m.Value, @"\n([ \t]*)<li>").Groups[1].Value;
                    var outdent = Regex.Match(m.Value, @"\n([ \t]*)</" + Regex.Escape(key) + ">$").Groups[1].Value;
                    inner = string.Concat(l.fr.Split('¶').Select(i => "\n" + indent + "<li>" + Esc(i.Trim()) + "</li>")) + "\n" + outdent;
                }
                else
                {
                    if (l.fr.Contains('¶')) { Console.WriteLine($"  ligne {l.n} : « ¶ » dans une entrée simple ({key}), ignorée"); errors++; continue; }
                    inner = Esc(l.fr);
                }
                text = text[..m.Index] + m.Groups[1].Value + inner + m.Groups[5].Value + text[(m.Index + m.Length)..];
                applied.Add(l.n);
            }
            if (text == orig) continue;
            try { new XmlDocument().LoadXml(text.TrimStart('﻿')); }
            catch (XmlException e) { Console.WriteLine($"  {g.Key.id}\\{g.Key.rel} : XML invalide après import ({e.Message}), fichier non modifié"); errors += applied.Count; continue; }
            File.WriteAllText(path, text, new UTF8Encoding(true));
            done += applied.Count;
            touched.Add(g.Key.id);
        }
        Console.WriteLine($"Importées : {done} ; vides (ignorées) : {empty} ; introuvables ou déjà traduites : {notFound} ; erreurs : {errors + bad}.");
        if (touched.Count > 0)
            Console.WriteLine("Mods modifiés : " + string.Join(",", touched) +
                              "\nEnsuite : Fix-Typography.ps1 -Path out\\<mod>, --check, puis Sync-Translations.ps1 -Only " + string.Join(",", touched));
        return errors + bad > 0 ? 1 : 0;
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");
}
