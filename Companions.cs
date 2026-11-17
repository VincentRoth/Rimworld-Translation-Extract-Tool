using System.Text.Json;

namespace TranslationExtractor;

/// <summary>Texte absent du XML mais traduisible, dont l'anglais se déduit d'un autre champ
/// (ex. BackstoryDef.titleFemale reprend title ; PawnKindDef.labelPlural reprend label).</summary>
public sealed class Companion
{
    public string Type { get; set; } = "";        // type de def (ou parent) concerné
    public string Path { get; set; } = "";        // objet imbriqué relatif à la def (ex. stuffProps), vide = la def
    public string List { get; set; } = "";        // liste d'objets sous Path (ex. lifeStages, verbs) : une entrée par élément
    public bool OnlySingle { get; set; }          // List : seulement si la liste a exactement un élément
    public string Field { get; set; } = "";
    public string From { get; set; } = "";        // champ(s) source "a|b" ; préfixe ^ = champ de la def racine
    public string Fallback { get; set; } = "";    // texte si aucune source
    public string Note { get; set; } = "";
}

public sealed class CompanionFile
{
    public List<Companion> Companions { get; set; } = new();

    public static CompanionFile Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<CompanionFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
    }
}
