using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslationExtractor;

/// <summary>
/// Defs whose defName contains a dot: the game cuts a DefInjected key at its first dot, so no key can reach them.
/// XML Extensions menus are translated another way: out\&lt;mod&gt;\Patches\ gets a patch adding tKey / tKeyTip / tKeyMessage
/// to the elements (found by their English text), and the texts become Keyed entries (the menu shows the Keyed text of
/// the active language when there is one, else its own text: nothing changes in the other languages).
/// The texts of other dotted defs are left out and counted in report.txt.
/// </summary>
public static class DottedDefs
{
    const string MenuType = "XmlExtensions.SettingsMenuDef";
    const string KeyedFile = "DFP_XmlExtensionsMenu.xml";
    const string PatchFile = "DFP_XmlExtensionsMenu_tKeys.xml";

    public static void Convert(List<Entry> entries, List<KeyedEntry> keyed, LanguageWriter writer, string outDir, Report rep)
    {
        var patchPath = Path.Combine(outDir, "Patches", PatchFile);
        var dotted = entries.Where(e => LangXml.Unreachable(e.DefName)).ToList();
        if (dotted.Count == 0) { Remove(patchPath); return; }
        entries.RemoveAll(e => LangXml.Unreachable(e.DefName));

        var menu = dotted.Where(e => e.Folder == MenuType && e.TKeyField != null && e.Items == null && e.Raw.Trim() != "").ToList();
        foreach (var g in dotted.Except(menu).GroupBy(e => e.Folder))
            rep.Notes.Add($"defName contenant un point, texte impossible à traduire par DefInjected, non extrait : {g.Count()} {g.Key} (ex. {g.First().Key})");
        if (menu.Count == 0) { Remove(patchPath); return; }

        var carry = new Dictionary<string, string>();
        foreach (var f in menu.Select(e => e.File).Distinct())
            foreach (var kv in writer.ByEnglish(Path.Combine("DefInjected", MenuType, f))) carry.TryAdd(kv.Key, kv.Value);

        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n");
        sb.Append("  <!-- Généré par TranslationExtractTool (DottedDefs) : clés Keyed des menus XML Extensions dont le defName contient un point. -->\n");
        var ops = new HashSet<string>();
        var byKey = new Dictionary<string, KeyedEntry>();
        foreach (var e in menu)
        {
            var key = "DFP_XE_" + Hash(e.DefName, 6) + "_" + Hash(e.Value, 10);
            if (!byKey.TryGetValue(key, out var k))
            {
                k = byKey[key] = new KeyedEntry { File = KeyedFile, Key = key, Value = e.Value, Note = $"menu XML Extensions {e.DefName} ({e.Leaf}, ex. {e.Path})" };
                if (carry.TryGetValue(LanguageWriter.EnKey(e.Value), out var c)) k.Carry = c;
                keyed.Add(k);
            }
            if (!ops.Add(e.DefName + "\n" + e.TKeyField + "\n" + e.Leaf + "\n" + key)) continue;
            var xpath = $"Defs/{MenuType}[defName={Literal(e.DefName)}]/descendant-or-self::*[normalize-space({e.Leaf})={Literal(Regex.Replace(e.Raw.Trim(), @"[ \t\r\n]+", " "))}][not({e.TKeyField})]";
            sb.Append("  <Operation Class=\"PatchOperationAdd\">\n    <success>Always</success>\n    <xpath>").Append(SecurityElement.Escape(xpath))
              .Append("</xpath>\n    <value><").Append(e.TKeyField).Append('>').Append(key).Append("</").Append(e.TKeyField).Append("></value>\n  </Operation>\n");
        }
        sb.Append("</Patch>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
        File.WriteAllText(patchPath, sb.ToString(), new UTF8Encoding(true));
        rep.Notes.Add($"Menus XML Extensions au defName à point : {menu.Count} textes en {byKey.Count} clés Keyed ({KeyedFile}) et {ops.Count} opérations de patch (Patches\\{PatchFile})");
        Console.WriteLine($"Menus XML Extensions au defName à point : {byKey.Count} clés Keyed, {ops.Count} opérations de patch.");
    }

    static void Remove(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        var d = Path.GetDirectoryName(path)!;
        if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
    }

    static string Hash(string s, int n) => System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..n].ToLowerInvariant();

    /// <summary>XPath 1.0 string literal (no escape exists: both quotes need concat).</summary>
    static string Literal(string s)
    {
        if (!s.Contains('"')) return "\"" + s + "\"";
        if (!s.Contains('\'')) return "'" + s + "'";
        return "concat(" + string.Join(", '\"', ", s.Split('"').Select(p => "\"" + p + "\"")) + ")";
    }
}
