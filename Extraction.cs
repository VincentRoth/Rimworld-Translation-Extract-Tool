using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace TranslationExtractor;

public sealed class Entry
{
    public string Folder = "";        // dossier DefInjected (type de def)
    public string LegacyFolder = "";  // ancien nom de dossier (classe exacte), pour reprendre les traductions déjà saisies
    public string File = "";
    public string DefName = "";
    public string Path = "";
    public string Value = "";
    public List<string>? Items;       // collection
    public string DefTypeName = "";
    public List<string> TypeChain = new();
    public string Origin = "";        // packageId du mod d'origine de la def
    public bool Marked;               // champ [MustTranslate]
    public string FieldType = "";
    public string? Note;              // origine d'une valeur absente du XML (défaut du code, forme optionnelle)
    public string? Prefill;           // nouvelle entrée pré-remplie avec l'anglais (pas de TODO) ; texte = explication en commentaire
    public string Key => DefName + "." + Path;
    public string Leaf => Path.Contains('.') ? Path[(Path.LastIndexOf('.') + 1)..] : Path;
    public string Sig => Items != null ? string.Join("\u0001", Items) : Value;
}

public sealed class KeyedEntry
{
    public string File = "";
    public string Key = "";
    public string Value = "";
    public List<string> Usages = new();
}

public sealed class Extraction
{
    readonly TypeIndex types;
    readonly Report rep;
    public bool IncludeUnmarked = true;
    public bool ImplicitDefaults = true;
    public CompanionFile Companions = new();
    public string? CountOrigin;
    string curOrigin = "";
    readonly HashSet<string> unknownTypes = new();

    static readonly string[] FallbackFields =
        { "label", "labelShort", "description", "labelPlural", "jobString", "reportString", "verb", "gerund", "labelNoun", "letterLabel", "letterText", "helpText" };

    public Extraction(TypeIndex types, Report rep) { this.types = types; this.rep = rep; }

    // -------------------- Defs --------------------

    public List<Entry> ExtractDefs(XmlDocument doc, Func<string, bool> originFilter)
    {
        var res = new List<Entry>();
        foreach (var e in doc.DocumentElement!.ChildNodes.OfType<XmlElement>())
        {
            if (string.Equals(e.GetAttribute("Abstract"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            var origin = e.GetAttribute("__mod");
            if (!originFilter(origin)) continue;
            curOrigin = origin;
            var defName = e["defName"]?.InnerText.Trim();
            if (string.IsNullOrEmpty(defName)) continue;
            var typeName = e.GetAttribute("Class");
            if (typeName == "") typeName = e.Name;
            var t = types.Resolve(typeName);
            var ctx = new Ctx
            {
                Defs = res, DefName = defName, File = e.GetAttribute("__file"), Origin = origin,
            };
            if (t == null)
            {
                if (unknownTypes.Add(typeName)) rep.UnknownTypes.Add($"{typeName} (mod {origin}) : type introuvable, repli sur les champs usuels");
                ctx.Folder = typeName; ctx.LegacyFolder = typeName; ctx.TypeName = typeName; ctx.Chain = new() { typeName };
                foreach (var f in FallbackFields)
                    if (e[f] != null && !e[f]!.ChildNodes.OfType<XmlElement>().Any())
                        ctx.AddString(f, e[f]!.InnerText, true, "string");
                continue;
            }
            ctx.Folder = TypeIndex.FolderName(t);
            ctx.LegacyFolder = TypeIndex.LegacyFolderName(t);
            ctx.TypeName = t.Name;
            ctx.Chain = TypeIndex.BaseChainNames(t).ToList();
            WalkClass(e, t, "", ctx, 0);
            if (ImplicitDefaults) ApplyCompanions(e, t, ctx);
        }
        return res;
    }


    // -------------------- Formes optionnelles / déduites --------------------

    static string? Text(XmlElement el, string name)
    {
        var c = el[name];
        if (c == null || c.ChildNodes.OfType<XmlElement>().Any()) return null;
        var t = c.InnerText.Trim();
        return t.Length == 0 ? null : t;
    }

    void ApplyCompanions(XmlElement root, Type rootType, Ctx ctx)
    {
        foreach (var r in Companions.Companions)
        {
            if (!ctx.Chain.Contains(r.Type)) continue;
            // naviguer jusqu'à l'objet (ou la liste)
            XmlElement? el = root; Type t = rootType; string path = "";
            bool ok = true;
            foreach (var seg in r.Path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = types.Field(t, seg);
                el = el?[seg];
                if (f == null || el == null) { ok = false; break; }
                t = f.Field.FieldTypeSafe(); path = path == "" ? f.Field.Name : path + "." + f.Field.Name;
            }
            if (!ok || el == null) continue;
            var targets = new List<(XmlElement el, Type type, string path)>();
            if (r.List == "") targets.Add((el, t, path));
            else
            {
                var lf = types.Field(t, r.List);
                var le = el[r.List];
                if (lf == null || le == null || !IsListType(lf.Field.FieldTypeSafe(), out var et) || !CanRecurse(et)) continue;
                var lis = le.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "li").ToList();
                if (r.OnlySingle && lis.Count != 1) continue;
                var lp = path == "" ? lf.Field.Name : path + "." + lf.Field.Name;
                foreach (var (li, itemType, h) in ItemHandles(lis, et)) targets.Add((li, itemType, lp + "." + h));
            }
            foreach (var (obj, objType, objPath) in targets)
            {
                var ff = types.Field(objType, r.Field);
                if (ff == null || ff.NoTranslate || !IsString(ff.Field.FieldTypeSafe())) continue;
                if (Text(obj, r.Field) != null) continue;
                string? src = null;
                foreach (var s in r.From.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    src = s.StartsWith('^') ? Text(root, s[1..]) : Text(obj, s);
                    if (src != null) break;
                }
                src ??= r.Fallback == "" ? null : r.Fallback;
                if (src == null) continue;
                var p = objPath == "" ? ff.Field.Name : objPath + "." + ff.Field.Name;
                ctx.AddString(p, src, true, "string", r.Note == "" ? "forme absente du XML (repris de " + r.From + ")" : r.Note);
            }
        }
    }

    sealed class Ctx
    {
        public List<Entry> Defs = null!;
        public string Folder = "", LegacyFolder = "", TypeName = "", DefName = "", File = "", Origin = "";
        public List<string> Chain = new();

        public void AddString(string path, string raw, bool marked, string ftype, string? note = null)
        {
            var v = Norm(raw);
            Defs.Add(new Entry
            {
                Folder = Folder, LegacyFolder = LegacyFolder, File = File, DefName = DefName, Path = path, Value = v,
                DefTypeName = TypeName, TypeChain = Chain, Origin = Origin, Marked = marked, FieldType = ftype, Note = note,
            });
        }
        public void AddList(string path, List<string> items, bool marked, string? prefill = null)
        {
            Defs.Add(new Entry
            {
                Folder = Folder, LegacyFolder = LegacyFolder, File = File, DefName = DefName, Path = path, Items = items.Select(Norm).ToList(),
                DefTypeName = TypeName, TypeChain = Chain, Origin = Origin, Marked = marked, FieldType = "List<string>", Prefill = prefill,
            });
        }
    }

    static string Norm(string s) => s.Replace("\r\n", "\n").Trim().Replace("\n", "\\n");

    public const string RulesFilesHint = "fichiers de mots (symbole->chemin sous Strings/, sans .txt). Pré-rempli avec l'anglais : " +
        "les listes traduites vont dans Strings/ au même chemin. À modifier seulement pour découper une liste (féminin, élision, pluriel), " +
        "comme l'officiel (GlobalUtility : BusinessType_Feminine->Words/Nouns/BusinessTypes_Feminine), en adaptant aussi rulesStrings";

    static bool IsString(Type t) => t.FullName == "System.String";
    static bool IsSlateRefString(Type t)
    {
        try { return t.IsGenericType && t.Name.StartsWith("SlateRef") && t.GetGenericArguments() is [var a] && IsString(a); }
        catch { return false; }
    }
    static bool IsListType(Type t, out Type elem)
    {
        elem = null!;
        if (t.IsArray) { elem = t.GetElementType()!; return true; }
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.List`1")
        { elem = t.GetGenericArguments()[0]; return true; }
        return false;
    }

    static readonly Regex TextyName = new(@"^(label|description|desc|text|title|gerund|verb|message|tooltip|tip|noun|adjective|letter|header)|(Label|Description|Desc|Text|Title|Gerund|Verb|Message|Tooltip|Tip|Noun|Adjective|Letter|Header|Msg)(_[A-Za-z0-9]+)?$", RegexOptions.Compiled);
    static bool LooksLikeText(string v, string field = "")
    {
        v = v.Trim();
        if (v.Length == 0 || !v.Any(char.IsLetter)) return false;
        if (v.Count(c => c == '*' || c == '|') >= 6) return false;   // données sérialisées (ex. préréglage de colon), pas du texte
        if (v.EndsWith(".png") || v.EndsWith(".dll")) return false;
        // un chemin (Things/Item/X) n'a pas d'espace ; "price / kg" dans un champ nommé comme un texte en est un.
        // Les sauts de ligne écrits "\n" ne sont pas des séparateurs de chemin (Research Reinvented : shortDesc_Direct).
        var vp = v.Replace("\\n", " ");
        if ((vp.Contains('/') || vp.Contains('\\')) && !(field != "" && TextyName.IsMatch(field) && v.Contains(' '))) return false;
        if (Regex.IsMatch(v, @"^[\w\-\.]+$"))                      // un seul mot
            return field != "" && TextyName.IsMatch(field) && !v.Contains('_') && !v.Contains('.');
        return v.Contains(' ') || v.Contains('{') || v.Contains('[');
    }

    bool Accept(FieldInfoEx f, string value, string path)
    {
        if (f.NoTranslate) return false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (f.MustTranslate) return true;
        if (f.Field.Name.StartsWith("debug")) return false;
        if (IncludeUnmarked && LooksLikeText(value, f.Field.Name)) return true;
        var k = f.Field.DeclaringType?.Name + "." + f.Field.Name;
        if (curOrigin == CountOrigin) rep.SkippedUnmarked[k] = rep.SkippedUnmarked.GetValueOrDefault(k) + 1;
        return false;
    }

    void WalkClass(XmlElement el, Type type, string path, Ctx ctx, int depth)
    {
        if (depth > 7) return;
        var present = new HashSet<string>();
        foreach (var c in el.ChildNodes.OfType<XmlElement>())
        {
            if (c.Name == "li") continue;
            var f = types.Field(type, c.Name);
            if (f == null) continue;
            present.Add(f.Field.Name);
            var ft = f.Field.FieldTypeSafe();
            var p = path == "" ? c.Name : path + "." + c.Name;
            // utiliser le nom réel du champ si alias
            if (f.Field.Name != c.Name) p = path == "" ? f.Field.Name : path + "." + f.Field.Name;
            // BackstoryDef.PostLoad copies baseDesc into description before translations are injected:
            // a "baseDesc" key would change a field the game no longer reads (official French uses "description").
            if (path == "" && f.Field.Name == "baseDesc" && ctx.Chain.Contains("BackstoryDef") && el["description"] == null) p = "description";

            // SlateRef<string> (textes des quêtes : lettres, infobulles…) : clé "<TKey>.slateRef" si le XML donne un TKey
            // (forme de la traduction officielle, résolue par TKeySystem), sinon "<chemin>.slateRef" ; les références
            // de variables ($asker…) ne sont pas du texte.
            if (IsSlateRefString(ft))
            {
                if (c.ChildNodes.OfType<XmlElement>().Any()) continue;
                var sv = c.InnerText.Trim();
                if (sv.StartsWith("$") || !LooksLikeText(sv) || (!sv.Contains(' ') && c.GetAttribute("TKey") == "")) continue;
                var tkey = c.GetAttribute("TKey");
                ctx.AddString((tkey != "" ? tkey : p) + ".slateRef", c.InnerText, true, "SlateRef");
                continue;
            }
            if (IsString(ft))
            {
                if (c.ChildNodes.OfType<XmlElement>().Any()) continue;
                if (Accept(f, c.InnerText, p)) ctx.AddString(p, c.InnerText, f.MustTranslate, "string");
            }
            else if (IsListType(ft, out var et))
            {
                var lis = c.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "li").ToList();
                if (IsString(et))
                {
                    var items = lis.Select(x => x.InnerText).ToList();
                    // RulePack.rulesFiles ("symbole->Words/…") : chemins des fichiers de mots, remplaçables en entier par
                    // DefInjected ([TranslationCanChangeCount]) comme le fait l'officiel pour découper une liste (genre,
                    // élision). Pré-rempli avec l'anglais : à modifier seulement pour restructurer les listes.
                    if (items.Count > 0 && f.CanChangeCount && f.Field.Name == "rulesFiles" && f.Field.DeclaringType?.Name == "RulePack")
                    {
                        ctx.AddList(p, items, true, RulesFilesHint);
                        continue;
                    }
                    if (items.Count > 0 && !f.NoTranslate && (f.MustTranslate || (IncludeUnmarked && items.Any(x => LooksLikeText(x)))))
                    {
                        // The game accepts a whole <li> list only on [TranslationCanChangeCount] fields; elsewhere
                        // (GeneDef.customEffectDescriptions, TipSetDef.tips…) it rejects it: one key per index, as in the official French.
                        if (f.CanChangeCount) ctx.AddList(p, items, f.MustTranslate);
                        else for (int i = 0; i < items.Count; i++) ctx.AddString(p + "." + i, lis[i].InnerText, f.MustTranslate, "string");
                    }
                    else if (items.Count > 0 && !f.NoTranslate)
                    {
                        var k = f.Field.DeclaringType?.Name + "." + f.Field.Name;
                        if (curOrigin == CountOrigin) rep.SkippedUnmarked[k] = rep.SkippedUnmarked.GetValueOrDefault(k) + 1;
                    }
                }
                else if (CanRecurse(et))
                {
                    foreach (var (li, itemType, h) in ItemHandles(lis, et))
                        WalkClass(li, itemType, p + "." + h, ctx, depth + 1);
                }
            }
            else if (CanRecurse(ft))
            {
                // objet polymorphe (ex. <root Class="QuestNode_Sequence">) : parcourir la classe réelle
                var cls = c.GetAttribute("Class");
                var real = cls != "" ? types.Resolve(cls) ?? ft : ft;
                WalkClass(c, real, p, ctx, depth + 1);
            }
        }
        AddCodeDefaults(type, path, present, ctx);
    }

    /// <summary>Éléments d'une liste d'objets avec leur handle de traduction (suffixé -N si plusieurs identiques).</summary>
    List<(XmlElement li, Type type, string handle)> ItemHandles(List<XmlElement> lis, Type et)
    {
        var infos = new List<(XmlElement li, Type type, string handle)>();
        int idx = 0;
        foreach (var li in lis)
        {
            var itemType = et;
            var cls = li.GetAttribute("Class");
            if (cls != "") itemType = types.Resolve(cls) ?? et;
            infos.Add((li, itemType, HandleFor(li, itemType, idx)));
            idx++;
        }
        var counts = infos.GroupBy(i => i.handle).ToDictionary(g => g.Key, g => g.Count());
        var occ = new Dictionary<string, int>();
        var res = new List<(XmlElement, Type, string)>();
        foreach (var (li, itemType, handle) in infos)
        {
            var h = handle;
            if (counts[handle] > 1) { occ[handle] = occ.GetValueOrDefault(handle); h = handle + "-" + occ[handle]; occ[handle]++; }
            res.Add((li, itemType, h));
        }
        return res;
    }

    /// <summary>Champs [MustTranslate] absents du XML dont le constructeur fixe un texte par défaut (ex. leaderTitle = "leader") :
    /// le jeu affiche ce texte anglais, il doit donc pouvoir être traduit.</summary>
    void AddCodeDefaults(Type type, string path, HashSet<string> present, Ctx ctx)
    {
        if (!ImplicitDefaults) return;
        foreach (var kv in types.StringDefaults(type))
        {
            if (present.Contains(kv.Key)) continue;
            var f = types.Field(type, kv.Key);
            if (f == null || f.Field.Name != kv.Key || !f.MustTranslate || f.NoTranslate || !IsString(f.Field.FieldTypeSafe())) continue;
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            var p = path == "" ? kv.Key : path + "." + kv.Key;
            ctx.AddString(p, kv.Value, true, "string", "valeur par défaut fixée par le code (champ absent du XML)");
        }
    }

    bool CanRecurse(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t.IsPointer) return false;
        if (IsString(t)) return false;
        var ns = t.Namespace ?? "";
        if (ns.StartsWith("System") || ns.StartsWith("UnityEngine")) return false;
        if (t.IsGenericType) return false;
        if (t.IsInterface) return true;
        if (TypeIndex.IsDef(t)) return false;
        if (t.FullName == "System.Type") return false;
        return true;
    }

    static string Sanitize(string v)
    {
        v = Regex.Replace(v, @"{[^{}]*}", "");
        v = v.Replace(' ', '_');
        v = Regex.Replace(v, @"[^A-Za-z0-9_]", "");
        v = Regex.Replace(v, "_+", "_").Trim('_');
        return v == "" ? "x" : v;
    }

    string HandleFor(XmlElement li, Type itemType, int idx)
    {
        foreach (var hf in types.HandleFields(itemType))
        {
            var name = hf.Field.Name;
            string? val = li[name]?.InnerText.Trim();
            if (val == null && name.StartsWith("untranslated") && name.Length > 12)
            {
                var alt = char.ToLowerInvariant(name[12]) + name[13..];
                val = li[alt]?.InnerText.Trim();
            }
            if (string.IsNullOrEmpty(val) && hf.Field.FieldTypeSafe().FullName == "System.Type")
                val = types.DefaultTypeAssigned(itemType, name);
            if (!string.IsNullOrEmpty(val))
            {
                if (hf.Field.FieldTypeSafe().FullName == "System.Type" && val.Contains('.')) val = val[(val.LastIndexOf('.') + 1)..];
                return Sanitize(val);
            }
        }
        return idx.ToString();
    }

    // -------------------- Keyed --------------------

    public List<KeyedEntry> ExtractKeyed(ModMeta mod, Dictionary<string, List<string>> usages)
    {
        var res = new List<KeyedEntry>();
        var seen = new HashSet<string>();
        foreach (var folder in mod.Folders)
        {
            var dir = Path.Combine(folder, "Languages", "English", "Keyed");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var d = new XmlDocument(); d.Load(f);
                    foreach (var e in d.DocumentElement!.ChildNodes.OfType<XmlElement>())
                    {
                        if (!seen.Add(e.Name)) continue;
                        var ke = new KeyedEntry { File = System.IO.Path.GetFileName(f), Key = e.Name, Value = Norm(e.InnerText) };
                        if (usages.TryGetValue(e.Name, out var u)) ke.Usages = u;
                        res.Add(ke);
                    }
                }
                catch (Exception ex) { rep.Warn($"Keyed illisible {f}: {ex.Message}"); }
            }
        }
        return res;
    }
}

static class FieldInfoExt
{
    /// <summary>Type du champ ; si son assembly est absent (mod non installé), un type neutre qui n'est ni texte, ni liste, ni objet à explorer.</summary>
    public static Type FieldTypeSafe(this System.Reflection.FieldInfo f)
    {
        try { return f.FieldType; } catch { return typeof(object); }
    }
}
