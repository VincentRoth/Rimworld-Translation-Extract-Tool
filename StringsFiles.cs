using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// Fichiers de mots du mod (Languages/English/Strings/&lt;dossier&gt;/**/*.txt), lus par les générateurs de texte
/// (RulePack.rulesFiles, Rule_File). Le jeu cherche d'abord le fichier dans la langue active, au même chemin relatif
/// (sans extension), sinon en anglais ; il ignore le texte après "//" et les lignes vides (GenText.LinesFromString).
/// Sortie : out\&lt;mod&gt;\Languages\French\Strings\&lt;même chemin&gt;, une ligne "TODO // EN: mot" par entrée.
/// Une réextraction reprend les traductions par texte anglais ; un fichier restructuré à la main (lignes sans
/// "// EN:") n'est pas réécrit.
/// </summary>
public static class StringsFiles
{
    public sealed class Result { public int Files, Lines, Preserved, Manual, Shipped; }

    /// <summary>Lignes utiles d'un fichier de mots, comme le jeu (GenText.LinesFromString).</summary>
    public static List<string> GameLines(string text)
    {
        var res = new List<string>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.StartsWith("//")) continue;
            t = t.Split("//")[0];
            if (t.Length != 0) res.Add(t);
        }
        return res;
    }

    static readonly Regex EnComment = new(@"//\s*EN:\s?(.*)$");

    public static Result Extract(ModMeta mod, string langDir, string placeholder, bool fresh, List<Entry> entries, Report rep)
    {
        var r = new Result();
        // qui utilise chaque fichier : rulesFiles "symbole->chemin" des defs extraites
        var users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.Items != null && e.Path.EndsWith("rulesFiles")))
            foreach (var it in e.Items!)
            {
                var parts = it.Split("->");
                if (parts.Length != 2) continue;
                var path = parts[1].Trim();
                if (!users.TryGetValue(path, out var l)) users[path] = l = new();
                l.Add($"{e.DefName} ([{parts[0].Trim()}])");
            }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in mod.Folders)
        {
            var sdir = Path.Combine(folder, "Languages", "English", "Strings");
            if (!Directory.Exists(sdir)) continue;
            if (Directory.Exists(Path.Combine(folder, "Languages", "French", "Strings"))) r.Shipped++;
            // le jeu ne lit que les sous-dossiers de Strings (Words/, Names/…), pas les fichiers posés à sa racine
            foreach (var sub in Directory.GetDirectories(sdir))
                foreach (var f in Directory.EnumerateFiles(sub, "*.txt", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(sdir, f);
                    if (!seen.Add(rel)) continue;   // même chemin dans plusieurs dossiers de chargement : le premier
                    WriteOne(f, rel, Path.Combine(langDir, "Strings", rel), placeholder, fresh, users, mod, r, rep);
                }
        }
        if (r.Shipped > 0) rep.Warn("Le mod embarque déjà des fichiers de mots français (Languages/French/Strings) : comparer avant de traduire.");
        if (r.Files > 0)
            rep.Notes.Add($"Fichiers de mots (Strings) : {r.Files} fichiers, {r.Lines} lignes, {r.Preserved} traductions reprises" +
                          (r.Manual > 0 ? $", {r.Manual} fichiers restructurés à la main laissés tels quels" : ""));
        return r;
    }

    static void WriteOne(string src, string rel, string dst, string placeholder, bool fresh, Dictionary<string, List<string>> users,
        ModMeta mod, Result r, Report rep)
    {
        var en = GameLines(File.ReadAllText(src));
        var key = Path.ChangeExtension(rel, null)!.Replace('\\', '/');
        r.Files++; r.Lines += en.Count;

        // traductions existantes, par texte anglais
        var known = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        if (!fresh && File.Exists(dst))
        {
            bool manual = false;
            foreach (var raw in File.ReadAllLines(dst))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("//")) continue;
                var m = EnComment.Match(t);
                if (!m.Success) { manual = true; break; }
                var val = t[..t.IndexOf("//")].Trim();
                var e = m.Groups[1].Value.Trim();
                if (val == placeholder || val == "") continue;
                if (!known.TryGetValue(e, out var q)) known[e] = q = new();
                q.Enqueue(val);
            }
            if (manual)
            {
                // fichier restructuré par le traducteur (lignes sans "// EN:") : ne pas l'écraser
                r.Manual++;
                rep.Warn($"Fichier de mots modifié à la main, non réécrit : Strings/{rel.Replace('\\', '/')}");
                return;
            }
        }

        var sb = new StringBuilder();
        sb.Append("// Fichier de mots : ").Append(key).Append(" (").Append(en.Count).Append(" lignes, mod ").Append(mod.Name).Append(")\n");
        if (users.TryGetValue(key, out var us))
            sb.Append("// Utilisé par : ").Append(string.Join(", ", us.Distinct())).Append('\n');
        sb.Append("// Une ligne = une entrée tirée au hasard par le générateur de texte. Remplacer ").Append(placeholder)
          .Append(" par le français ; le jeu ignore le texte après //.\n");
        sb.Append("// Fichier copié dans le pack seulement s'il ne reste aucun ").Append(placeholder).Append(" (sinon l'anglais est gardé).\n");
        sb.Append("// Noms inventés (syllabes, noms propres) : laisser tel quel, l'anglais reste utilisé (Conventions §1).\n");
        sb.Append("// Accord (genre, élision) : découper en plusieurs fichiers et adapter rulesFiles / rulesStrings du RulePackDef.\n");
        foreach (var e in en)
        {
            string val = placeholder;
            if (known.TryGetValue(e, out var q) && q.Count > 0) { val = q.Count > 1 ? q.Dequeue() : q.Peek(); r.Preserved++; }
            sb.Append(val).Append(" // EN: ").Append(e).Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Pour --check : lignes encore à traduire dans les .txt d'un dossier.</summary>
    public static (int files, int todo) CountTodo(string dir, string placeholder)
    {
        int files = 0, todo = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        foreach (var f in Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories))
        {
            files++;
            foreach (var raw in File.ReadAllLines(f))
            {
                var t = raw.Trim();
                if (t.StartsWith("//")) continue;
                if (t.Split("//")[0].Trim() == placeholder) todo++;
            }
        }
        return (files, todo);
    }
}
