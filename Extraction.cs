using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace TranslationExtractor;

public sealed class Entry
{
    public string Folder = "";        // dossier DefInjected (type de def)
    public string LegacyFolder = "";  // ancien nom de dossier (classe exacte), pour reprendre les traductions déjà saisies
    public string? LegacyPath;        // ancienne forme de la clé (TKey inconnu du jeu), pour reprendre les traductions déjà saisies
    public string? TKeyField;         // menu XML Extensions : champ tKey* qui permet de traduire ce texte par Keyed
    public string File = "";
    public string DefName = "";
    public string Path = "";
    public string Value = "";
    public string Raw = "";           // texte tel qu'écrit dans le XML (avant normalisation des sauts de ligne)
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
    public string? Carry;             // traduction reprise d'une ancienne clé DefInjected (contenu XML)
    public string? Note;
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
                if (r.When != "" && !r.When.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(w => (w.StartsWith('^') ? Text(root, w[1..]) : Text(obj, w)) is { } wv && !Regex.IsMatch(wv, @"^[-+]?0*([.,]0*)?$"))) continue;
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

        public string? TKeyField;

        public void AddString(string path, string raw, bool marked, string ftype, string? note = null, string? legacyPath = null, string? prefill = null)
        {
            var v = Norm(raw);
            Defs.Add(new Entry
            {
                Folder = Folder, LegacyFolder = LegacyFolder, File = File, DefName = DefName, Path = path, Value = v,
                DefTypeName = TypeName, TypeChain = Chain, Origin = Origin, Marked = marked, FieldType = ftype, Note = note,
                Raw = raw, LegacyPath = legacyPath, Prefill = prefill, TKeyField = TKeyField,
            });
        }
        public void AddList(string path, List<string> items, bool marked, string? prefill = null)
        {
            // forme <rep> : texte simple seulement, donc une clé par élément
            if (!LangXml.ValidName(DefName + "." + path))
            {
                for (int i = 0; i < items.Count; i++) AddString(path + "." + i, items[i], marked, "string", prefill: prefill);
                return;
            }
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

    public const string RuleFileHint = "fichier de mots (chemin sous Strings/, sans .txt). Pré-rempli avec l'anglais : la liste traduite va dans " +
        "Strings/ au même chemin. À modifier seulement pour viser un autre fichier";

    /// <summary>
    /// XML Extensions settings element: name of its field holding the Keyed key of this text (label, text: tKey ;
    /// tooltip: tKeyTip ; message: tKeyMessage), null when there is none or the mod already sets it.
    /// </summary>
    string? TKeyFor(XmlElement el, Type type, string field)
    {
        if (!(type.Namespace ?? "").StartsWith("XmlExtensions")) return null;
        var tk = field switch { "label" or "text" => "tKey", "tooltip" => "tKeyTip", "message" => "tKeyMessage", _ => null };
        return tk != null && types.Field(type, tk) != null && el[tk] == null ? tk : null;
    }

    static bool IsString(Type t) => t.FullName == "System.String";
    static bool IsSlateRefString(Type t)
    {
        try { return t.IsGenericType && t.Name.StartsWith("SlateRef") && t.GetGenericArguments() is [var a] && IsString(a); }
        catch { return false; }
    }
    // SlateRef<object>: QuestNode_Set.value, PrefixCapturedVar.value (QuestNode_SubScript/Prefix parms). Holds text, numbers,
    // defs or "$" expressions: text only with a TKey or a sentence that is not an expression.
    static bool IsSlateRefObject(Type t)
    {
        try { return t.IsGenericType && t.Name.StartsWith("SlateRef") && t.GetGenericArguments() is [var a] && a.FullName == "System.Object"; }
        catch { return false; }
    }

    /// <summary>Quest text held by a SlateRef (string or object): key "TKey.slateRef" when the game knows the TKey, else "path.slateRef".</summary>
    void AddSlateText(XmlElement c, string p, Ctx ctx, bool objectValue, string tkeySuffix = ".slateRef")
    {
        if (c.ChildNodes.OfType<XmlElement>().Any()) return;
        var sv = c.InnerText.Trim();
        var tkey = c.GetAttribute("TKey");
        // slate references in brackets ("[../joiner_nameDef]") are not file paths
        var probe = Regex.Replace(sv, @"\[[^\]]*\]", "X");
        if (sv.StartsWith("$") || !(LooksLikeText(sv) || LooksLikeText(probe)) || (!sv.Contains(' ') && tkey == "")) return;
        // object values also carry expressions and def lists: a sentence (letters, at least two words) or a TKey
        if (objectValue && tkey == "" && (sv.Contains("$(") || !Regex.IsMatch(sv, @"\p{L}+\s+\p{L}+"))) return;
        // the game only knows the hardcoded TKeys (Core and DLC quests): elsewhere the real path is required
        if (tkey != "" && types.GameKnowsTKey(ctx.DefName + "." + tkey)) ctx.AddString(tkey + tkeySuffix, c.InnerText, true, "SlateRef");
        else ctx.AddString(p + ".slateRef", c.InnerText, true, "SlateRef", legacyPath: tkey != "" ? tkey + tkeySuffix : null);
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

    static readonly Regex Identifier = new(@"^[\w\-\.]+$", RegexOptions.Compiled);

    bool Accept(FieldInfoEx f, string value, string path)
    {
        if (f.NoTranslate) return false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        // XML Extensions setting ("{Key.SubKey}"): replaced by the text the player typed, a translation would overwrite it
        if (Regex.IsMatch(value.Trim(), @"^\{[A-Za-z]\w*(\.\w+)+\}$")) return false;
        if (f.MustTranslate) return true;
        if (f.Field.Name.StartsWith("debug")) return false;
        if (!IncludeUnmarked || !LooksLikeText(value, f.Field.Name))
        {
            var k = f.Field.DeclaringType?.Name + "." + f.Field.Name;
            if (curOrigin == CountOrigin) rep.SkippedUnmarked[k] = rep.SkippedUnmarked.GetValueOrDefault(k) + 1;
            return false;
        }
        // Unmarked field used by the code as an identifier (FieldUsageIndex): translating it breaks the lookup
        // (Facial Animation RightEyeLabel, VEF TextureVariantOverride.groupName). A Keyed key ("labelKey": PackAnimal)
        // only when the value is one word: a sentence passed to Translate() is shown as is when the key is missing.
        var oneWord = Identifier.IsMatch(value.Trim());
        string? reason = null;
        if (oneWord && Regex.IsMatch(f.Field.Name, "Key$")) reason = "nom en *Key, valeur d'un mot (clé Keyed)";
        else if (types.LookupUsage(f.Field) is { } uses)
        {
            var hard = uses.FirstOrDefault(u => u.Kind is not ("transkey" or "compare-tr"));
            var tk = uses.FirstOrDefault(u => u.Kind == "transkey");
            if (hard != null) reason = $"{hard.Kind} : {hard.Where}";
            else if (tk != null && oneWord) reason = $"transkey : {tk.Where}";
        }
        if (reason == null) return true;
        // exception chosen by the user (implicit-rules.json, "translateAnyway"): translated knowing the lookup may break
        if (Companions.TranslateAnywayKeys().Contains(FieldUsageIndex.Key(f.Field))) return true;
        if (curOrigin == CountOrigin)
        {
            var fk = FieldUsageIndex.Key(f.Field);
            rep.LookupSkipped.TryGetValue(fk, out var cur);
            rep.LookupSkipped[fk] = (cur.Count + 1, reason, cur.Example ?? path + " = " + value.Trim());
        }
        return false;
    }

    void WalkClass(XmlElement el, Type type, string path, Ctx ctx, int depth)
    {
        if (depth > 20) return;
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
            if (IsSlateRefString(ft) || IsSlateRefObject(ft))
            {
                AddSlateText(c, p, ctx, objectValue: !IsSlateRefString(ft));
                continue;
            }
            // QuestNode_SubScript / QuestNode_Prefix parms: List<PrefixCapturedVar> read by LoadDataFromXmlCustom, one element
            // per variable (<customLetterLabel TKey="...">), indexed as a list (TKeySystem.MarkTreatAsList). Official key
            // "TKey.value.slateRef" (hardcoded mapping to parms.N), else parms.N.value.slateRef.
            if (IsListType(ft, out var pcv) && pcv.Name == "PrefixCapturedVar")
            {
                int n = 0;
                foreach (var v in c.ChildNodes.OfType<XmlElement>())
                    AddSlateText(v, p + "." + n++ + ".value", ctx, objectValue: true, tkeySuffix: ".value.slateRef");
                continue;
            }
            if (IsString(ft))
            {
                if (c.ChildNodes.OfType<XmlElement>().Any()) continue;
                // Rule_File d'un rulesRaw : chemin d'un fichier de mots ([MayTranslate]), comme rulesFiles
                if (f.Field.Name == "path" && f.Field.DeclaringType?.Name == "Rule_File")
                {
                    if (c.InnerText.Trim() != "") ctx.AddString(p, c.InnerText, true, "string", prefill: RuleFileHint);
                    continue;
                }
                ctx.TKeyField = TKeyFor(el, type, f.Field.Name);
                if (Accept(f, c.InnerText, p)) ctx.AddString(p, c.InnerText, f.MustTranslate, "string");
                ctx.TKeyField = null;
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
                    if (items.Count > 0 && f.Field.Name == "pathList" && f.Field.DeclaringType?.Name == "Rule_File")
                    {
                        ctx.AddList(p, items, true, RuleFileHint);
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
        // like TranslationHandleUtility.GetBestHandleWithIndexForListElement: the handle is the value of the handle field
        // of highest priority; its number counts every element whose same field has that value, whatever its own best handle
        var infos = lis.Select(li =>
        {
            var cls = li.GetAttribute("Class");
            var itemType = cls != "" ? types.Resolve(cls) ?? et : et;
            return (li, itemType, vals: HandleValues(li, itemType));
        }).ToList();
        var res = new List<(XmlElement, Type, string)>();
        for (int i = 0; i < infos.Count; i++)
        {
            var (li, itemType, vals) = infos[i];
            string h = i.ToString();   // no handle, or nothing left of it once normalized (Chinese label): index
            if (vals.Count > 0 && vals[0].handle != "")
            {
                var (field, handle) = vals[0];
                var same = Enumerable.Range(0, infos.Count).Where(j => infos[j].vals.Any(v => v.field == field && v.handle == handle)).ToList();
                h = same.Count > 1 ? handle + "-" + same.IndexOf(i) : handle;
            }
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
        // TranslationHandleUtility.NormalizedHandle
        v = Regex.Replace(v.Trim(), "{.*?}", "");
        v = Regex.Replace(v, @"[ \n\t]", "_");
        v = Regex.Replace(v, @"[^A-Za-z0-9_]", "");
        v = Regex.Replace(v, "_+", "_").Trim('_');
        return v != "" && v.All(char.IsDigit) ? "_" + v : v;
    }

    /// <summary>Handle fields of a list element with a value, best first: (field, normalized value).</summary>
    List<(string field, string handle)> HandleValues(XmlElement li, Type itemType)
    {
        var res = new List<(string, string)>();
        foreach (var hf in types.HandleFields(itemType))
        {
            var id = hf.Field.DeclaringType?.FullName + "." + hf.Field.Name;
            // QuestNode: set by the constructor from the class name
            if (hf.Field.Name == "myTypeShort" && hf.Field.DeclaringType?.Name == "QuestNode")
            {
                var n = itemType.Name;
                res.Add((id, Sanitize(n.StartsWith("QuestNode_") ? n["QuestNode_".Length..] : n)));
                continue;
            }
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
                res.Add((id, Sanitize(val)));
            }
        }
        return res;
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
