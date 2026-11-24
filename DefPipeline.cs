using System.Xml;

namespace TranslationExtractor;

public sealed record PatchItem(ModMeta Mod, string File, XmlElement Op);

/// <summary>Charge les Defs des mods actifs, applique les patchs XML, résout l'héritage.</summary>
public sealed class Pipeline
{
    public readonly GameEnv Env;
    public readonly List<ModMeta> Order;
    public readonly ModMeta Target;
    public readonly Report Rep;
    public readonly HashSet<string> ActiveIds;
    public readonly HashSet<string> ActiveNames;
    public XmlDocument BaseDoc = new();
    public readonly List<PatchItem> Patches = new();

    public Pipeline(GameEnv env, List<ModMeta> order, ModMeta target, Report rep)
    {
        Env = env; Order = order; Target = target; Rep = rep;
        ActiveIds = order.Select(o => o.PackageId).ToHashSet();
        ActiveNames = order.Select(o => o.Name.ToLowerInvariant()).ToHashSet();
        foreach (var m in order) env.ComputeFolders(m, ActiveIds);
    }

    public void Load()
    {
        BaseDoc = new XmlDocument();
        BaseDoc.AppendChild(BaseDoc.CreateElement("Defs"));
        foreach (var mod in Order)
        {
            foreach (var folder in mod.Folders)
            {
                var defs = Path.Combine(folder, "Defs");
                if (Directory.Exists(defs))
                    foreach (var f in Directory.EnumerateFiles(defs, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        LoadDefsFile(mod, f);
                var pat = Path.Combine(folder, "Patches");
                if (Directory.Exists(pat))
                    foreach (var f in Directory.EnumerateFiles(pat, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        LoadPatchFile(mod, f);
            }
        }
    }

    void LoadDefsFile(ModMeta mod, string file)
    {
        try
        {
            var d = new XmlDocument();
            d.Load(file);
            var root = d.DocumentElement;
            if (root == null) return;
            var fname = Path.GetFileName(file);
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is not XmlElement e) continue;
                var imp = (XmlElement)BaseDoc.ImportNode(e, true);
                imp.SetAttribute("__mod", mod.PackageId);
                imp.SetAttribute("__file", fname);
                BaseDoc.DocumentElement!.AppendChild(imp);
            }
        }
        catch (Exception ex) { Rep.Warn($"Defs illisible {file}: {ex.Message}"); }
    }

    void LoadPatchFile(ModMeta mod, string file)
    {
        try
        {
            var d = new XmlDocument();
            d.Load(file);
            var root = d.DocumentElement;
            if (root == null) return;
            foreach (XmlNode n in root.ChildNodes)
                if (n is XmlElement e) Patches.Add(new PatchItem(mod, Path.GetFileName(file), e));
        }
        catch (Exception ex) { Rep.Warn($"Patch illisible {file}: {ex.Message}"); }
    }

    /// <summary>The target mod has patches depending on one of its options (both branches are run with options: true).</summary>
    public bool TargetHasOptions => Patches.Any(p => p.Mod == Target && PatchEngine.UsesOptions(p.Op));

    public XmlDocument Build(bool includeTargetPatches, bool options = false)
    {
        var doc = (XmlDocument)BaseDoc.CloneNode(true);
        var engine = new PatchEngine(doc, ActiveIds, ActiveNames, Rep);
        foreach (var p in Patches)
        {
            if (!includeTargetPatches && p.Mod == Target) continue;
            engine.Options = options && p.Mod == Target;
            engine.Run(p.Op, p.Mod, p.File, includeTargetPatches && !options);
        }
        PruneMayRequire(doc);
        ResolveInheritance(doc);
        return doc;
    }

    void PruneMayRequire(XmlDocument doc)
    {
        var nodes = doc.SelectNodes("//*[@MayRequire or @MayRequireAnyOf]");
        if (nodes == null) return;
        foreach (var n in nodes.Cast<XmlElement>().ToList())
        {
            var all = n.GetAttribute("MayRequire");
            var any = n.GetAttribute("MayRequireAnyOf");
            bool ok = true;
            if (all != "" && !GameEnv.IsActive(all, ActiveIds, false)) ok = false;
            if (any != "" && !GameEnv.IsActive(any, ActiveIds, true)) ok = false;
            if (!ok) n.ParentNode?.RemoveChild(n);
        }
    }

    // ---------------- héritage ----------------

    void ResolveInheritance(XmlDocument doc)
    {
        var root = doc.DocumentElement!;
        var byName = new Dictionary<string, XmlElement>();
        foreach (var e in root.ChildNodes.OfType<XmlElement>())
        {
            var n = e.GetAttribute("Name");
            if (n != "") byName[n] = e;
        }
        var done = new HashSet<XmlElement>();
        var inProgress = new HashSet<XmlElement>();

        XmlElement Resolve(XmlElement node)
        {
            if (done.Contains(node) || node.ParentNode == null) return node;
            var parentName = node.GetAttribute("ParentName");
            if (parentName == "") { done.Add(node); return node; }
            if (!inProgress.Add(node)) { Rep.Warn($"Héritage cyclique : {node.Name} {node.GetAttribute("Name")}"); return node; }
            if (!byName.TryGetValue(parentName, out var parentRaw))
            {
                Rep.Warn($"ParentName introuvable : {parentName} (def {DefLabel(node)})");
                done.Add(node);
                return node;
            }
            var parent = Resolve(parentRaw);
            var merged = doc.CreateElement(node.Name);
            // enfants du parent
            foreach (XmlNode c in parent.ChildNodes) merged.AppendChild(c.CloneNode(true));
            // attributs : parent (Class) puis enfant
            foreach (XmlAttribute a in parent.Attributes!)
                if (a.Name == "Class") merged.SetAttribute(a.Name, a.Value);
            foreach (XmlAttribute a in node.Attributes!) merged.SetAttribute(a.Name, a.Value);
            MergeInto(merged, node);
            node.ParentNode!.ReplaceChild(merged, node);
            var nm = merged.GetAttribute("Name");
            if (nm != "") byName[nm] = merged;
            done.Add(merged);
            return merged;
        }

        foreach (var e in root.ChildNodes.OfType<XmlElement>().ToList()) { if (e.ParentNode != null) Resolve(e); }
    }

    static string DefLabel(XmlElement e)
    {
        var dn = e.SelectSingleNode("defName")?.InnerText;
        return $"{e.Name}/{dn ?? e.GetAttribute("Name")}";
    }

    static bool IsList(XmlElement e) => e.ChildNodes.OfType<XmlElement>().Any(c => c.Name == "li");

    /// <summary>Reproduit XmlInheritance.RecursiveNodeCopyOverwriteElements : applique child sur dst (déjà copie du parent).
    /// Les éléments &lt;li&gt; du child s'ajoutent à ceux du parent (sauf Inherit="False") ; les autres éléments se fusionnent.</summary>
    static void MergeInto(XmlElement dst, XmlElement child, bool top = true)
    {
        if (string.Equals(child.GetAttribute("Inherit"), "false", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var x in dst.ChildNodes.Cast<XmlNode>().ToList()) dst.RemoveChild(x);
            foreach (XmlNode x in child.ChildNodes) dst.AppendChild(dst.OwnerDocument.ImportNode(x, true));
            foreach (XmlAttribute a in child.Attributes!) if (a.Name != "Inherit") dst.SetAttribute(a.Name, a.Value);
            return;
        }
        if (!top)
        {
            dst.Attributes!.RemoveAll();
            foreach (XmlAttribute a in child.Attributes!) dst.SetAttribute(a.Name, a.Value);
        }
        var text = child.ChildNodes.Cast<XmlNode>().LastOrDefault(x => x.NodeType == XmlNodeType.Text);
        if (text != null)
        {
            foreach (var x in dst.ChildNodes.Cast<XmlNode>().ToList()) dst.RemoveChild(x);
            dst.AppendChild(dst.OwnerDocument.ImportNode(text, true));
            return;
        }
        var elems = child.ChildNodes.OfType<XmlElement>().ToList();
        if (elems.Count == 0)
        {
            if (dst.ChildNodes.OfType<XmlElement>().Any()) return;
            foreach (var x in dst.ChildNodes.Cast<XmlNode>().ToList()) dst.RemoveChild(x);
            return;
        }
        foreach (var n in elems)
        {
            if (n.Name == "li") { dst.AppendChild(dst.OwnerDocument.ImportNode(n, true)); continue; }
            var existing = dst.ChildNodes.OfType<XmlElement>().FirstOrDefault(x => x.Name == n.Name);
            if (existing != null) MergeInto(existing, n, false);
            else dst.AppendChild(dst.OwnerDocument.ImportNode(n, true));
        }
    }
}
