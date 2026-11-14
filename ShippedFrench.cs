using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TranslationExtractor;

/// <summary>
/// Traduction déjà embarquée par le mod cible (Languages\&lt;lang&gt;*). Sert à avertir et à mesurer
/// la couverture : clés extraites déjà traduites, manquantes, ou probablement couvertes par d'anciennes clés indexées.
/// </summary>
public sealed class ShippedFrench
{
    public readonly List<string> Dirs = new();
    public readonly HashSet<string> DefKeys = new(StringComparer.Ordinal);
    public readonly HashSet<string> KeyedKeys = new(StringComparer.Ordinal);
    public bool Any => Dirs.Count > 0;

    static readonly Regex NumericSegment = new(@"\.\d+(\.|$)", RegexOptions.Compiled);

    public static ShippedFrench Load(ModMeta mod, string lang, Report rep)
    {
        var s = new ShippedFrench();
        if (mod.Official) return s;
        foreach (var folder in mod.Folders.Distinct())
        {
            var langRoot = Path.Combine(folder, "Languages");
            if (!Directory.Exists(langRoot)) continue;
            foreach (var d in Directory.GetDirectories(langRoot))
            {
                var name = Path.GetFileName(d);
                if (!name.StartsWith(lang, StringComparison.OrdinalIgnoreCase)) continue;
                s.Dirs.Add(d);
                foreach (var f in Directory.EnumerateFiles(d, "*.xml", SearchOption.AllDirectories))
                {
                    var rel = f[(d.Length + 1)..].Replace('\\', '/');
                    bool isKeyed = rel.StartsWith("Keyed/", StringComparison.OrdinalIgnoreCase);
                    bool isDef = rel.StartsWith("DefInjected/", StringComparison.OrdinalIgnoreCase);
                    if (!isKeyed && !isDef) continue;
                    try
                    {
                        var doc = XDocument.Load(f);
                        foreach (var el in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
                            (isKeyed ? s.KeyedKeys : s.DefKeys).Add(el.Name.LocalName);
                    }
                    catch (Exception ex) { rep.Warn($"Traduction fournie illisible {f}: {ex.Message}"); }
                }
            }
        }
        return s;
    }

    public enum State { Translated, MaybeLegacy, Missing }

    Dictionary<string, List<string>>? legacyByDef;

    /// <summary>Anciennes clés indexées (ex. Def.comps.0.tools.0.label) groupées par defName.</summary>
    public State Classify(Entry e)
    {
        if (DefKeys.Contains(e.Key)) return State.Translated;
        if (legacyByDef == null)
        {
            legacyByDef = new();
            foreach (var k in DefKeys.Where(k => NumericSegment.IsMatch(k)))
            {
                var dot = k.IndexOf('.');
                if (dot < 0) continue;
                var def = k[..dot];
                if (!legacyByDef.TryGetValue(def, out var l)) legacyByDef[def] = l = new();
                l.Add(k);
            }
        }
        // clé manquante, mais le mod a des clés indexées pour cette def avec le même champ final
        if (legacyByDef.TryGetValue(e.DefName, out var cands) && cands.Any(k => k.EndsWith("." + e.Leaf, StringComparison.Ordinal)) && e.Path.Contains('.'))
            return State.MaybeLegacy;
        return State.Missing;
    }

    /// <summary>Construit le texte du rapport et affiche un avertissement console. Retourne les entrées/keyed déjà traduits.</summary>
    public (List<Entry> translated, List<KeyedEntry> translatedKeyed) Evaluate(List<Entry> entries, List<KeyedEntry> keyed, Report rep, StringBuilder? outText)
    {
        var tr = new List<Entry>(); var maybe = new List<Entry>(); var miss = new List<Entry>();
        foreach (var e in entries)
            switch (Classify(e)) { case State.Translated: tr.Add(e); break; case State.MaybeLegacy: maybe.Add(e); break; default: miss.Add(e); break; }
        var ktr = keyed.Where(k => KeyedKeys.Contains(k.Key)).ToList();
        var kmiss = keyed.Where(k => !KeyedKeys.Contains(k.Key)).ToList();
        var extractedKeys = entries.Select(e => e.Key).ToHashSet();
        var shippedUnused = DefKeys.Where(k => !extractedKeys.Contains(k) && !NumericSegment.IsMatch(k)).OrderBy(k => k).ToList();
        var shippedLegacy = DefKeys.Count(k => NumericSegment.IsMatch(k));

        int total = entries.Count + keyed.Count;
        int done = tr.Count + ktr.Count;
        string pct = total == 0 ? "-" : $"{100.0 * done / total:F0} %";
        var head = $"Le mod embarque déjà une traduction ({string.Join(", ", Dirs.Select(d => Path.GetFileName(d)))}) : " +
                   $"{DefKeys.Count} clés defs + {KeyedKeys.Count} Keyed. Couverture des textes extraits : {done}/{total} ({pct}) ; " +
                   $"défs probablement couvertes par d'anciennes clés indexées : {maybe.Count} ; manquantes : {miss.Count + kmiss.Count}.";
        rep.Notes.Add("ATTENTION traduction déjà fournie par le mod. " + head);
        Console.WriteLine("ATTENTION : " + head);
        Console.WriteLine("  Vérifier la couverture avant de traduire (rapport : section « Traduction déjà fournie »). Option --skip-shipped : ne pas ré-extraire les entrées déjà traduites.");

        if (outText != null)
        {
            outText.AppendLine("\n== Traduction déjà fournie par le mod ==");
            outText.AppendLine("  " + head);
            outText.AppendLine($"  Anciennes clés indexées dans la traduction du mod : {shippedLegacy} (le jeu les accepte, mais elles ne suivent pas les noms de handle actuels).");
            outText.AppendLine($"  Clés du mod ne correspondant à aucune entrée extraite (obsolètes ou noms différents) : {shippedUnused.Count}");
            foreach (var k in shippedUnused.Take(30)) outText.AppendLine("    ~ " + k);
            outText.AppendLine($"  Entrées extraites sans traduction dans le mod : {miss.Count} defs, {kmiss.Count} Keyed");
            foreach (var g in miss.GroupBy(e => e.Folder).OrderByDescending(g => g.Count()))
                outText.AppendLine($"    {g.Count(),5}  {g.Key}  ex: {g.First().Key}");
            foreach (var k in kmiss.Take(20)) outText.AppendLine("    Keyed : " + k.Key);
            if (maybe.Count > 0)
            {
                outText.AppendLine($"  Entrées dont la clé exacte manque mais que d'anciennes clés indexées couvrent peut-être : {maybe.Count}");
                foreach (var e in maybe.Take(20)) outText.AppendLine("    ? " + e.Key);
            }
        }
        return (tr, ktr);
    }
}
