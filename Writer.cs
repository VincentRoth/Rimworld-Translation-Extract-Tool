using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TranslationExtractor;

public sealed class WriterOptions
{
    public string OutDir = "";
    public string Placeholder = "TODO";
    public bool PrefillEn;
    public bool Fresh;
}

public sealed class LanguageWriter
{
    readonly WriterOptions opt;
    readonly ContextResolver ctx;
    readonly OfficialFr official;
    readonly Report rep;
    public int Written, Preserved, Changed, Obsolete;
    readonly HashSet<string> writtenPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files of a previous run that this run did not produce (all their keys disappeared): deleted when they only hold
    /// placeholders, otherwise rewritten with their translations as OBSOLETE comments (kept for later runs).
    /// </summary>
    public void CleanOrphans()
    {
        if (opt.Fresh) return;
        foreach (var sub in new[] { "DefInjected", "Keyed" })
        {
            var dir = Path.Combine(opt.OutDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories).ToList())
            {
                if (writtenPaths.Contains(Path.GetFullPath(path))) continue;
                var existing = LoadExisting(path);
                if (existing.Map.Values.All(v => IsPlaceholder(v.inner)))
                {
                    File.Delete(path);
                    var d = Path.GetDirectoryName(path)!;
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                    rep.Warn($"Fichier plus produit par l'extraction, supprimé (aucune traduction) : {Path.GetRelativePath(opt.OutDir, path)}");
                }
                else
                {
                    var sb = Header();
                    EmitObsolete(sb, existing, new HashSet<string>());
                    Footer(sb);
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                    rep.Warn($"Fichier plus produit par l'extraction, traductions gardées en OBSOLETE : {Path.GetRelativePath(opt.OutDir, path)}");
                }
            }
        }
    }

    public LanguageWriter(WriterOptions opt, ContextResolver ctx, OfficialFr official, Report rep)
    { this.opt = opt; this.ctx = ctx; this.official = official; this.rep = rep; }

    static string San(string c)
    {
        while (c.Contains("--")) c = c.Replace("--", "- -");
        return c;
    }
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");

    sealed class Existing
    {
        public Dictionary<string, (string inner, string? en)> Map = new();
    }

    Existing LoadExisting(string path)
    {
        var ex = new Existing();
        if (!File.Exists(path)) return ex;
        try
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            string? en = null;
            foreach (var n in doc.Root!.Nodes())
            {
                if (n is XComment c)
                {
                    var t = c.Value.Trim();
                    if (t.StartsWith("EN:")) en = t[3..].Trim();
                    else if (t.StartsWith("OBSOLETE"))
                    {
                        // traduction d'une clé disparue : la relire pour qu'elle survive aux réextractions suivantes
                        // (et soit réutilisée si la clé revient)
                        var m = Regex.Match(t, @"<(?<k>[^>\s/]+)>(?<v>.*)</\k<k>>", RegexOptions.Singleline);
                        if (m.Success && !ex.Map.ContainsKey(m.Groups["k"].Value)) ex.Map[m.Groups["k"].Value] = (m.Groups["v"].Value, null);
                    }
                }
                else if (n is XElement e)
                {
                    var inner = string.Concat(e.Nodes().Select(x => x.ToString()));
                    ex.Map[e.Name.LocalName] = (inner, en);
                    en = null;
                }
            }
        }
        catch (Exception e) { rep.Warn($"Fichier existant illisible {path}: {e.Message}"); }
        return ex;
    }

    // -------------------- DefInjected --------------------

    public void WriteDefs(List<Entry> entries, string targetPackageId)
    {
        var groups = entries.GroupBy(e =>
        {
            var file = e.Origin == targetPackageId ? e.File : "Patches_" + e.File.Replace("Patched_", "");
            return (e.Folder, file);
        });
        foreach (var g in groups)
        {
            var path = Path.Combine(opt.OutDir, "DefInjected", g.Key.Folder, g.Key.file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = Header();
            var existing = opt.Fresh ? new Existing() : LoadExisting(path);
            // anciens dossiers (classe exacte au lieu du type à base de données) : reprendre leurs entrées, puis les supprimer
            var legacyPaths = g.Select(e => e.LegacyFolder).Where(f => f != "" && f != g.Key.Folder).Distinct()
                .Select(f => Path.Combine(opt.OutDir, "DefInjected", f, g.Key.file)).Where(File.Exists).ToList();
            if (!opt.Fresh)
                foreach (var lp in legacyPaths)
                    foreach (var kv in LoadExisting(lp).Map)
                        if (!existing.Map.TryGetValue(kv.Key, out var cur) || IsPlaceholder(cur.inner)) existing.Map[kv.Key] = kv.Value;
            var used = new HashSet<string>();
            var seenKeys = new HashSet<string>();
            foreach (var e in g)
            {
                if (!seenKeys.Add(e.Key)) continue;
                used.Add(e.Key);
                var (c, max, isShort) = ctx.ForDef(e);
                if (e.Note != null) c = (string.IsNullOrEmpty(c) ? "" : c + " | ") + "Texte absent du XML : " + e.Note;
                var soft = ctx.IsSoftDef(e);
                var fr = official.Def(e.Folder, e.Key);
                EmitEntry(sb, e.Key, e.Value, e.Items, c, max, isShort, e.Marked, fr, existing,
                    $"{e.DefTypeName}.{e.Path}", soft, e.Prefill);
            }
            EmitObsolete(sb, existing, used);
            Footer(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); writtenPaths.Add(Path.GetFullPath(path));
            foreach (var lp in legacyPaths)
            {
                File.Delete(lp);
                var dir = Path.GetDirectoryName(lp)!;
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                rep.Warn($"Dossier DefInjected corrigé : {Path.GetFileName(dir)} -> {g.Key.Folder} ({g.Key.file}, traductions reprises)");
            }
        }
    }

    bool IsPlaceholder(string inner)
    {
        var t = inner.Trim();
        return t == "" || t == opt.Placeholder || Regex.IsMatch(t, @"^(\s*<li>\s*" + Regex.Escape(opt.Placeholder) + @"\s*</li>\s*)+$");
    }

    // -------------------- Keyed --------------------

    public void WriteKeyed(List<KeyedEntry> entries)
    {
        foreach (var g in entries.GroupBy(k => k.File))
        {
            var path = Path.Combine(opt.OutDir, "Keyed", g.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = Header();
            var existing = opt.Fresh ? new Existing() : LoadExisting(path);
            var used = new HashSet<string>();
            foreach (var k in g)
            {
                used.Add(k.Key);
                var (c, max, isShort) = ctx.ForKeyed(k);
                var usage = k.Usages.Count > 0 ? ContextResolver.UsageHint(k.Usages) : "";
                string? ctxText = c;
                if (!string.IsNullOrEmpty(usage)) ctxText = (ctxText == null ? "" : ctxText + " | ") + "utilisé dans : " + usage;
                var fr = official.Keyed(k.Key);
                EmitEntry(sb, k.Key, k.Value, null, ctxText, max, isShort, true, fr, existing,
                    k.Usages.Count > 0 ? string.Join("; ", k.Usages.Take(3)) : null);
            }
            EmitObsolete(sb, existing, used);
            Footer(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); writtenPaths.Add(Path.GetFullPath(path));
        }
    }

    // -------------------- commun --------------------

    static StringBuilder Header()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n\n");
        return sb;
    }
    static void Footer(StringBuilder sb) => sb.Append("</LanguageData>\n");

    void EmitEntry(StringBuilder sb, string key, string value, List<string>? items, string? context, int? max, bool isShort,
        bool marked, string? frOfficial, Existing existing, string? where, bool soft = false, string? prefill = null)
    {
        if (prefill != null) context = (string.IsNullOrEmpty(context) ? "" : context + " | ") + prefill;
        string enText = items != null ? string.Join(" | ", items) : value;
        sb.Append("  <!-- EN: ").Append(San(items != null
            ? "\n" + string.Join("\n", items.Select(i => "    <li>" + i + "</li>")) + "\n  "
            : value)).Append(" -->\n");

        var ctxParts = new List<string>();
        if (!string.IsNullOrEmpty(context)) ctxParts.Add(context!);
        else if (!marked) ctxParts.Add("champ non marqué [MustTranslate] : probablement du texte, à vérifier");
        if (where != null && context == null) { /* rien */ }
        if (ctxParts.Count > 0)
            sb.Append("  <!-- Contexte: ").Append(San(string.Join(" | ", ctxParts))).Append(" -->\n");

        var ph = ContextResolver.FindPlaceholders(enText);
        if (ph.Count > 0)
            sb.Append("  <!-- Conserver tels quels : ").Append(San(string.Join(" ", ph))).Append(" -->\n");

        int len = enText.Length;
        int? lim = max ?? (isShort ? (int)Math.Ceiling(len * 1.3) + 2 : null);
        if (Regex.IsMatch(enText, "[㐀-鿿]"))
        {
            lim = null;   // texte source en chinois : un nombre de caractères n'a pas de sens pour le français
            sb.Append("  <!-- Texte source en chinois : pas de limite de longueur calculée -->\n");
        }
        if (lim != null)
            sb.Append(soft
                ? $"  <!-- Limite souple (nom d'objet, indicative) : ~{lim} car. (EN: {len}) max={lim} soft -->\n"
                : $"  <!-- Limite: ~{lim} car. (EN: {len}) max={lim} -->\n");

        if (!string.IsNullOrEmpty(frOfficial))
            sb.Append("  <!-- FR du jeu de base : ").Append(San(frOfficial!)).Append(" -->\n");

        // valeur : existante > en > placeholder
        string? keep = null;
        if (existing.Map.TryGetValue(key, out var ex))
        {
            var exInner = ex.inner.Trim();
            bool isPlaceholder = exInner == opt.Placeholder || (opt.PrefillEn && exInner == Esc(enText)) || exInner.Contains("<li>" + opt.Placeholder + "</li>");
            // pré-remplissage jamais modifié : suivre l'anglais s'il a changé (sinon on garderait l'ancien anglais)
            if (prefill != null && items != null && ex.en != null && LiValues(exInner).SequenceEqual(LiValues(ex.en))) isPlaceholder = true;
            if (!isPlaceholder && exInner != "")
            {
                keep = ex.inner;
                Preserved++;
                if (ex.en != null && ex.en != (items != null ? "" : value) && items == null)
                {
                    sb.Append("  <!-- ATTENTION : texte EN modifié depuis la traduction. Ancien EN: ").Append(San(ex.en)).Append(" -->\n");
                    Changed++;
                }
            }
        }
        sb.Append("  <").Append(key).Append('>');
        if (keep != null) sb.Append(keep);
        else if (items != null)
        {
            sb.Append('\n');
            foreach (var i in items)
                sb.Append("    <li>").Append(opt.PrefillEn || prefill != null ? Esc(i) : opt.Placeholder).Append("</li>\n");
            sb.Append("  ");
        }
        else if (value.Trim() == "") { /* empty English text: nothing to translate, left empty (not TODO) */ }
        else sb.Append(opt.PrefillEn || prefill != null ? Esc(value) : opt.Placeholder);
        sb.Append("</").Append(key).Append(">\n\n");
        Written++;
    }

    /// <summary>Valeurs des &lt;li&gt; d'un contenu XML ou d'un commentaire EN (texte brut, entités décodées).</summary>
    static List<string> LiValues(string s) =>
        Regex.Matches(s, @"<li>(.*?)</li>", RegexOptions.Singleline).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).ToList();

    void EmitObsolete(StringBuilder sb, Existing existing, HashSet<string> used)
    {
        foreach (var kv in existing.Map)
        {
            if (used.Contains(kv.Key)) continue;
            var inner = kv.Value.inner.Trim();
            if (inner == opt.Placeholder) continue;
            sb.Append("  <!-- OBSOLETE (clé disparue de l'extraction) : <").Append(kv.Key).Append('>')
              .Append(San(inner)).Append("</").Append(kv.Key).Append("> -->\n\n");
            Obsolete++;
        }
    }
}

/// <summary>Vérification d'un dossier de traduction produit par l'outil.</summary>
public static class Checker
{
    public static int Run(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories).ToList()
            : new List<string> { path };
        int todo = 0, tooLong = 0, missingPh = 0, total = 0, softLong = 0;
        foreach (var f in files)
        {
            XDocument doc;
            try { doc = XDocument.Load(f); } catch { continue; }
            string? en = null; int? max = null; bool soft = false;
            foreach (var n in doc.Root?.Nodes() ?? Enumerable.Empty<XNode>())
            {
                if (n is XComment c)
                {
                    var t = c.Value.Trim();
                    if (t.StartsWith("EN:")) { en = t[3..].Trim(); max = null; soft = false; }
                    var m = Regex.Match(t, @"max=(\d+)( soft)?");
                    if (m.Success) { max = int.Parse(m.Groups[1].Value); soft = m.Groups[2].Success; }
                }
                else if (n is XElement e)
                {
                    total++;
                    var val = e.HasElements ? string.Join(" | ", e.Elements("li").Select(x => x.Value)) : e.Value;
                    var rel = Path.GetFileName(f);
                    if (val.Contains("TODO")) { todo++; Console.WriteLine($"TODO       {rel}: {e.Name.LocalName}"); }
                    else
                    {
                        var shown = DisplayLength(val);
                        if (max != null && shown > max) { if (soft) { softLong++; Console.WriteLine($"LONG (souple) {rel}: {e.Name.LocalName} ({shown}>{max}) {val}"); } else { tooLong++; Console.WriteLine($"TROP LONG  {rel}: {e.Name.LocalName} ({shown}>{max}) {val}"); } }
                        if (en != null)
                            foreach (var ph in ContextResolver.FindPlaceholders(en))
                                if (ph.StartsWith("{") && !val.Contains(ph) && !PronounReplaced(ph, val)) { missingPh++; Console.WriteLine($"PLACEHOLDER {rel}: {e.Name.LocalName} manque {ph}"); }
                    }
                    en = null; max = null; soft = false;
                }
            }
        }
        Console.WriteLine($"\n{total} entrées : {todo} TODO, {tooLong} trop longues, {missingPh} placeholders manquants"
            + (softLong > 0 ? $" ({softLong} noms d'objet au-delà de la limite souple, à apprécier)." : "."));
        // fichiers de mots (Strings/*.txt) : un fichier n'est copié dans le pack que sans TODO
        var (txtFiles, txtTodo) = Directory.Exists(path) ? StringsFiles.CountTodo(path, "TODO") : (0, 0);
        if (txtFiles > 0) Console.WriteLine($"Fichiers de mots : {txtFiles} fichiers, {txtTodo} lignes TODO.");
        return todo + tooLong + missingPh + txtTodo == 0 ? 0 : 1;
    }

    /// <summary>
    /// A pronoun symbol of the English text may legitimately be absent from the French one:
    /// possessives are often dropped (Conventions §6), and {X_pronoun}/{X_objective} are often
    /// replaced by the gender form {X_gender ? il : elle}.
    /// </summary>
    static bool PronounReplaced(string ph, string val)
    {
        var m = Regex.Match(ph, @"^\{(\w+?)_(pronoun|objective|possessive|objectivePronoun|possessivePronoun|reflexive)\}$");
        if (!m.Success) return false;
        if (m.Groups[2].Value.StartsWith("possessive")) return true;
        // replaced by the gender form, or by the name itself to avoid an agreement ({LOVER_nameDef})
        var p = m.Groups[1].Value;
        return val.Contains("{" + p + "_gender") || val.Contains("{" + p + "_nameDef}") || val.Contains("{" + p + "_nameFull}") || val.Contains("{" + p + "_labelShort}");
    }

    /// <summary>Length as displayed: a gender form {X_gender ? a : b : c} counts as its longest variant.</summary>
    static int DisplayLength(string val) =>
        Regex.Replace(val, @"\{\w+_gender\s*\?([^}]*)\}", m => m.Groups[1].Value.Split(':').Select(s => s.Trim()).OrderByDescending(s => s.Length).First()).Length;
}
