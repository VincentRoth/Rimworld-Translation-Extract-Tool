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
                    $"{e.DefTypeName}.{e.Path}", soft);
            }
            EmitObsolete(sb, existing, used);
            Footer(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
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
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
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
        bool marked, string? frOfficial, Existing existing, string? where, bool soft = false)
    {
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
                sb.Append("    <li>").Append(opt.PrefillEn ? Esc(i) : opt.Placeholder).Append("</li>\n");
            sb.Append("  ");
        }
        else sb.Append(opt.PrefillEn ? Esc(value) : opt.Placeholder);
        sb.Append("</").Append(key).Append(">\n\n");
        Written++;
    }

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
                        if (max != null && val.Length > max) { if (soft) { softLong++; Console.WriteLine($"LONG (souple) {rel}: {e.Name.LocalName} ({val.Length}>{max}) {val}"); } else { tooLong++; Console.WriteLine($"TROP LONG  {rel}: {e.Name.LocalName} ({val.Length}>{max}) {val}"); } }
                        if (en != null)
                            foreach (var ph in ContextResolver.FindPlaceholders(en))
                                if (ph.StartsWith("{") && !val.Contains(ph)) { missingPh++; Console.WriteLine($"PLACEHOLDER {rel}: {e.Name.LocalName} manque {ph}"); }
                    }
                    en = null; max = null; soft = false;
                }
            }
        }
        Console.WriteLine($"\n{total} entrées : {todo} TODO, {tooLong} trop longues, {missingPh} placeholders manquants"
            + (softLong > 0 ? $" ({softLong} noms d'objet au-delà de la limite souple, à apprécier)." : "."));
        return todo + tooLong + missingPh == 0 ? 0 : 1;
    }
}
