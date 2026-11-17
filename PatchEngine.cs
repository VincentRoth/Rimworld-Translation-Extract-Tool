using System.Xml;

namespace TranslationExtractor;

/// <summary>Reproduit les PatchOperation XML du jeu (hors classes C# personnalisées).</summary>
public sealed class PatchEngine
{
    readonly XmlDocument doc;
    readonly HashSet<string> activeIds, activeNames;
    readonly Report rep;
    ModMeta curMod = null!;
    string curFile = "";
    bool log;

    public PatchEngine(XmlDocument doc, HashSet<string> ids, HashSet<string> names, Report rep)
    { this.doc = doc; activeIds = ids; activeNames = names; this.rep = rep; }

    public void Run(XmlElement op, ModMeta mod, string file, bool logUnsupported)
    {
        curMod = mod; curFile = file; log = logUnsupported;
        try { Apply(op); }
        catch (Exception ex) { rep.Warn($"Patch {mod.Name}/{file}: {ex.Message}"); }
        TagNewDefs();
    }

    void TagNewDefs()
    {
        foreach (var e in doc.DocumentElement!.ChildNodes.OfType<XmlElement>())
            if (!e.HasAttribute("__mod"))
            {
                e.SetAttribute("__mod", curMod.PackageId);
                e.SetAttribute("__file", "Patched_" + curFile);
            }
    }

    static string Cls(XmlElement op)
    {
        var c = op.GetAttribute("Class");
        var i = c.LastIndexOf('.');
        return i >= 0 ? c[(i + 1)..] : c;
    }

    static string Txt(XmlElement op, string name) => op[name]?.InnerText ?? "";

    bool Apply(XmlElement op)
    {
        var mr = op.GetAttribute("MayRequire");
        if (mr != "" && !GameEnv.IsActive(mr, activeIds, false)) return true;
        var mra = op.GetAttribute("MayRequireAnyOf");
        if (mra != "" && !GameEnv.IsActive(mra, activeIds, true)) return true;

        bool res = Core(op);
        switch (Txt(op, "success").ToLowerInvariant())
        {
            case "always": return true;
            case "never": return false;
            case "invert": return !res;
            default: return res;
        }
    }

    List<XmlNode> Select(XmlElement op)
    {
        var xp = Txt(op, "xpath");
        var list = new List<XmlNode>();
        try
        {
            var r = doc.SelectNodes(xp);
            if (r != null) foreach (XmlNode n in r) list.Add(n);
        }
        catch (Exception ex) { rep.Warn($"XPath invalide '{xp}' ({curMod.Name}/{curFile}): {ex.Message}"); }
        return list;
    }

    IEnumerable<XmlNode> ValueNodes(XmlElement op)
    {
        var v = op["value"];
        if (v == null) return Enumerable.Empty<XmlNode>();
        return v.ChildNodes.Cast<XmlNode>().ToList();
    }

    bool Core(XmlElement op)
    {
        var cls = Cls(op);
        switch (cls)
        {
            case "PatchOperationSequence":
            {
                var ops = op["operations"];
                if (ops == null) return true;
                foreach (var o in ops.ChildNodes.OfType<XmlElement>())
                    if (!Apply(o)) return false;
                return true;
            }
            case "PatchOperationConditional":
            {
                var m = Select(op);
                var branch = m.Count > 0 ? op["match"] : op["nomatch"];
                return branch == null || Apply(branch);
            }
            case "PatchOperationFindMod":
            {
                var mods = op["mods"]?.ChildNodes.OfType<XmlElement>().Select(x => x.InnerText.Trim().ToLowerInvariant()) ?? Enumerable.Empty<string>();
                bool found = GameEnv.AssumeAll || mods.Any(activeNames.Contains);
                var branch = found ? op["match"] : op["nomatch"];
                return branch == null || Apply(branch);
            }
            case "PatchOperationTest":
                return Select(op).Count > 0;
            case "PatchOperationSuccess":
                return true;
            case "PatchOperationAdd":
            {
                var nodes = Select(op);
                bool prepend = Txt(op, "order").Equals("Prepend", StringComparison.OrdinalIgnoreCase);
                foreach (var n in nodes)
                    foreach (var v in ValueNodes(op))
                    {
                        var imp = doc.ImportNode(v, true);
                        if (prepend && n.FirstChild != null) n.InsertBefore(imp, n.FirstChild); else n.AppendChild(imp);
                    }
                return nodes.Count > 0;
            }
            case "PatchOperationInsert":
            {
                var nodes = Select(op);
                bool append = Txt(op, "order").Equals("Append", StringComparison.OrdinalIgnoreCase);
                foreach (var n in nodes)
                {
                    var parent = n.ParentNode; if (parent == null) continue;
                    XmlNode anchor = n;
                    foreach (var v in ValueNodes(op))
                    {
                        var imp = doc.ImportNode(v, true);
                        if (append) { parent.InsertAfter(imp, anchor); anchor = imp; }
                        else parent.InsertBefore(imp, n);
                    }
                }
                return nodes.Count > 0;
            }
            case "PatchOperationRemove":
            {
                var nodes = Select(op);
                foreach (var n in nodes) n.ParentNode?.RemoveChild(n);
                return nodes.Count > 0;
            }
            case "PatchOperationReplace":
            {
                var nodes = Select(op);
                foreach (var n in nodes)
                {
                    var parent = n.ParentNode; if (parent == null) continue;
                    foreach (var v in ValueNodes(op)) parent.InsertBefore(doc.ImportNode(v, true), n);
                    parent.RemoveChild(n);
                }
                return nodes.Count > 0;
            }
            case "PatchOperationAttributeAdd":
            case "PatchOperationAttributeSet":
            {
                var nodes = Select(op);
                var attr = Txt(op, "attribute"); var val = Txt(op, "value");
                foreach (var n in nodes.OfType<XmlElement>())
                    if (cls == "PatchOperationAttributeSet" || !n.HasAttribute(attr)) n.SetAttribute(attr, val);
                return nodes.Count > 0;
            }
            case "PatchOperationAttributeRemove":
            {
                var nodes = Select(op);
                var attr = Txt(op, "attribute");
                foreach (var n in nodes.OfType<XmlElement>()) n.RemoveAttribute(attr);
                return nodes.Count > 0;
            }
            case "PatchOperationSetName":
            {
                var nodes = Select(op);
                var name = Txt(op, "name");
                foreach (var n in nodes.OfType<XmlElement>().ToList())
                {
                    var ne = doc.CreateElement(name);
                    foreach (XmlAttribute a in n.Attributes) ne.SetAttribute(a.Name, a.Value);
                    while (n.FirstChild != null) ne.AppendChild(n.FirstChild);
                    n.ParentNode!.ReplaceChild(ne, n);
                }
                return nodes.Count > 0;
            }
            case "PatchOperationAddModExtension":
            {
                var nodes = Select(op);
                foreach (var n in nodes)
                {
                    var ext = n["modExtensions"];
                    if (ext == null) { ext = doc.CreateElement("modExtensions"); n.AppendChild(ext); }
                    foreach (var v in ValueNodes(op)) ext.AppendChild(doc.ImportNode(v, true));
                }
                return nodes.Count > 0;
            }
            default:
                if (log) rep.UnsupportedPatches.Add($"{curMod.Name}/{curFile}: {op.GetAttribute("Class")}" + (Txt(op, "xpath") is { Length: > 0 } x ? $"  xpath={x}" : ""));
                return true;
        }
    }
}
