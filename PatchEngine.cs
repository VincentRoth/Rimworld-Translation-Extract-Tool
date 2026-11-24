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

    /// <summary>Run every branch of the patches that depend on a mod option, not only the default one.</summary>
    public bool Options;

    // child elements holding the operations of a custom conditional patch (ModSettingsFramework, TooManyMods, Vehicles, VEF…)
    static readonly string[] Containers = { "operations", "patch", "patches", "match", "apply", "caseTrue" };
    static readonly string[] OptionalBranches = { "nomatch", "caseFalse" };
    static readonly HashSet<string> Vanilla = new() { "PatchOperationSequence", "PatchOperationConditional", "PatchOperationFindMod" };

    public static bool UsesOptions(XmlElement op) =>
        op.SelectNodes("descendant-or-self::*[@Class]")!.OfType<XmlElement>().Any(e => !Vanilla.Contains(Cls(e)) && Containers.Concat(OptionalBranches).Any(n => Nested(e, n).Count > 0));

    /// <summary>Operations of a container child: the child itself when it is an operation, else its children.</summary>
    static List<XmlElement> Nested(XmlElement op, string name)
    {
        var c = op[name];
        if (c == null) return new();
        if (c.HasAttribute("Class")) return new() { c };
        return c.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("Class")).ToList();
    }

    bool RunAll(IEnumerable<XmlElement> ops)
    {
        foreach (var o in ops) if (!Apply(o)) return false;
        return true;
    }

    static bool True(string s) => s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>XML Extensions PatchOperationSafeAdd: add what is missing, go down into what already exists.</summary>
    void SafeAdd(XmlNode parent, XmlNode add, int depth, int max)
    {
        var found = parent.ChildNodes.Cast<XmlNode>().FirstOrDefault(c => c.Name == add.Name);
        if (found == null || depth >= max) { parent.AppendChild(doc.ImportNode(add, true)); return; }
        if (add.FirstChild == null || !add.FirstChild.HasChildNodes) return;
        foreach (XmlNode c in add.ChildNodes) SafeAdd(found, c, depth + 1, max);
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
            case "FindMod":     // XmlExtensions.FindMod
            {
                var names = op["mods"]?.ChildNodes.OfType<XmlElement>().Select(x => x.InnerText.Trim().ToLowerInvariant()).ToList() ?? new();
                var set = True(Txt(op, "packageId")) ? activeIds : activeNames;
                bool and = Txt(op, "logic").Trim().Equals("and", StringComparison.OrdinalIgnoreCase);
                bool found = GameEnv.AssumeAll || (and ? names.All(set.Contains) : names.Any(set.Contains));
                return RunAll(Nested(op, found ? "caseTrue" : "caseFalse"));
            }
            case "OptionalPatch":   // XmlExtensions.OptionalPatch: branch of the option's default value
            {
                var defaults = op["defaultValues"]?.ChildNodes.OfType<XmlElement>().Select(x => True(x.InnerText)).ToList();
                bool on = defaults == null ? True(Txt(op, "defaultValue"))
                    : Txt(op, "logic").Trim().Equals("or", StringComparison.OrdinalIgnoreCase) ? defaults.Any(x => x) : defaults.All(x => x);
                var res = RunAll(Nested(op, on ? "caseTrue" : "caseFalse"));
                if (Options) RunAll(Nested(op, on ? "caseFalse" : "caseTrue"));
                return res;
            }
            case "UseSetting":   // XmlExtensions.UseSetting: {key} replaced by the setting's default value
            {
                var apply = op["apply"];
                if (apply == null) return true;
                var br = Txt(op, "brackets"); if (br.Length != 2) br = "{}";
                var d = new XmlDocument();
                d.LoadXml(apply.OuterXml.Replace(br[0] + Txt(op, "key") + br[1], System.Security.SecurityElement.Escape(Txt(op, "defaultValue"))));
                return RunAll(d.DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("Class")).ToList());
            }
            case "PatchOperationSafeAdd":   // XML Extensions
            {
                var nodes = Select(op);
                int max = int.TryParse(Txt(op, "safetyDepth"), out var sd) && sd >= 0 ? sd : 99999;
                foreach (var n in nodes)
                    foreach (var v in ValueNodes(op).OfType<XmlElement>()) SafeAdd(n, v, 0, max);
                return nodes.Count > 0;
            }
            default:
                // other custom class wrapping operations (mod option, feature toggle): run by default when its default
                // value is true, else only with the options
                var nested = Containers.SelectMany(n => Nested(op, n)).ToList();
                if (nested.Count > 0)
                {
                    bool on = new[] { "default", "defaultValue" }.Any(n => True(Txt(op, n)));
                    if (on || Options) RunAll(nested);
                    if (Options) foreach (var n in OptionalBranches) RunAll(Nested(op, n));
                    return true;
                }
                if (log) rep.UnsupportedPatches.Add($"{curMod.Name}/{curFile}: {op.GetAttribute("Class")}" + (Txt(op, "xpath") is { Length: > 0 } x ? $"  xpath={x}" : ""));
                return true;
        }
    }
}
