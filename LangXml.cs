using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TranslationExtractor;

/// <summary>
/// Clé de traduction sous forme XML. Une clé qui n'est pas un nom de balise valide (defName commençant par un chiffre…)
/// s'écrit &lt;rep&gt;&lt;path&gt;clé&lt;/path&gt;&lt;trans&gt;texte&lt;/trans&gt;&lt;/rep&gt;, forme lue par le jeu
/// (DefInjectionPackage.AddDataFromFile, texte simple seulement) ; sinon le jeu rejetterait tout le fichier.
/// </summary>
public static class LangXml
{
    public static bool ValidName(string key)
    {
        try { XmlConvert.VerifyNCName(key); return true; } catch (XmlException) { return false; } catch (ArgumentNullException) { return false; }
    }

    /// <summary>defName contenant un point : le jeu coupe la clé au premier point et ne trouve jamais la def.</summary>
    public static bool Unreachable(string defName) => defName.Contains('.');

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");

    public static string Open(string key) => ValidName(key) ? $"<{key}>" : $"<rep><path>{Esc(key)}</path><trans>";
    public static string Close(string key) => ValidName(key) ? $"</{key}>" : "</trans></rep>";

    /// <summary>Clé et élément portant la valeur d'une entrée (forme normale ou rep).</summary>
    public static (string key, XElement value) Read(XElement e)
    {
        if (e.Name.LocalName == "rep" && e.Element("path") is { } p && e.Element("trans") is { } t) return (p.Value.Trim(), t);
        return (e.Name.LocalName, e);
    }
    public static (string key, XmlElement value) Read(XmlElement e)
    {
        if (e.Name == "rep" && e["path"] is { } p && e["trans"] is { } t) return (p.InnerText.Trim(), t);
        return (e.Name, e);
    }

    static readonly Regex Entry = new(@"<rep><path>(?<k>.*?)</path><trans>(?<v>.*?)</trans></rep>|<(?<k>[^>\s/]+)>(?<v>.*)</\k<k>>", RegexOptions.Singleline);

    /// <summary>Première entrée d'un texte (commentaire OBSOLETE) et texte qui la suit.</summary>
    public static (string key, string inner, string rest)? Parse(string s)
    {
        var m = Entry.Match(s);
        return m.Success ? (System.Net.WebUtility.HtmlDecode(m.Groups["k"].Value), m.Groups["v"].Value, s[(m.Index + m.Length)..]) : null;
    }
}
