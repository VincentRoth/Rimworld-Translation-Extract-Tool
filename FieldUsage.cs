using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace TranslationExtractor;

/// <summary>
/// String fields whose value the code uses as an identifier rather than as displayed text: compared (==, Equals),
/// used as a dictionary key, a defName (GetNamed), a Keyed key (Translate), a texture path, a type name or a mod id.
/// Translating such an unmarked field breaks the lookup (Facial Animation RightEyeLabel compared with
/// BodyPartRecord.untranslatedCustomLabel: faces missing on load; VEF TextureVariantOverride.groupName compared with
/// GraphicPart.name).
///
/// IL scan, no execution. The value is followed through: a local (stloc/ldloc), a closure field (lambda captures),
/// and small methods that only return a field (getter, GetRightEyeLabel()), across all scanned assemblies.
/// </summary>
public sealed class FieldUsageIndex
{
    public sealed record Usage(string Kind, string Where);

    /// <summary>Field key "Namespace.Type+Nested.field" -> usages.</summary>
    readonly Dictionary<string, List<Usage>> usages = new();

    public static string Key(System.Reflection.FieldInfo f) => (f.DeclaringType?.FullName ?? "") + "." + f.Name;

    public IReadOnlyList<Usage>? Get(System.Reflection.FieldInfo f) => usages.TryGetValue(Key(f), out var l) ? l : null;
    public IEnumerable<KeyValuePair<string, List<Usage>>> All => usages;

    // per-assembly results, shared by the two extraction passes and by --audit-keys
    static readonly ConcurrentDictionary<string, AsmScan> cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Skip(string path)
    {
        var n = Path.GetFileName(path);
        return n.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) || n.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
            || n.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase);
    }

    public static FieldUsageIndex Build(IEnumerable<string> paths)
    {
        var scans = paths.Where(p => !Skip(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)
            .AsParallel().Select(p => cache.GetOrAdd(p, AsmScan.Run)).ToList();
        // sources: "F:<field>" or "M:<method>"; proxies (method returning a field) and closure fields resolve to fields
        var proxies = new Dictionary<string, string>();
        var aliases = new Dictionary<string, string>();
        var marked = new HashSet<string>();
        foreach (var s in scans)
        {
            marked.UnionWith(s.Marked);
            foreach (var kv in s.Proxies) proxies.TryAdd(kv.Key, kv.Value);
            foreach (var kv in s.Aliases) aliases.TryAdd(kv.Key, kv.Value);
        }
        string? Resolve(string src)
        {
            if (src.Length <= 2) return null;
            for (int depth = 0; depth < 6; depth++)
            {
                if (src.StartsWith("M:")) { if (!proxies.TryGetValue(src[2..], out var p)) return null; src = p; continue; }
                if (aliases.TryGetValue(src[2..], out var a)) { src = a; continue; }
                return src[2..];
            }
            return null;
        }
        var idx = new FieldUsageIndex();
        foreach (var s in scans)
            foreach (var (src, k0, where, partners) in s.Hits)
            {
                var f = Resolve(src);
                if (f == null || AsmScan.IsCompilerType(f)) continue;
                // compared with a field the game translates (MVCF AdditionalVerbProps.label with VerbProperties.label): both must be
                // translated alike, so the field stays extracted
                if (k0 == "compare" && partners.Select(Resolve).Contains(f)) continue;   // same field on both sides (through a getter): a.label == b.Label
                var kind = k0 == "compare" && partners.Select(Resolve).Any(p => p != null && marked.Contains(p)) ? "compare-tr" : k0;
                if (!idx.usages.TryGetValue(f, out var l)) idx.usages[f] = l = new();
                if (l.Count < 20 && !l.Any(u => u.Kind == kind && u.Where == where)) l.Add(new Usage(kind, where));
            }
        return idx;
    }

    sealed class AsmScan
    {
        public readonly Dictionary<string, string> Proxies = new();   // method -> source
        public readonly Dictionary<string, string> Aliases = new();   // closure field -> source
        public readonly List<(string src, string kind, string where, string[] partners)> Hits = new();
        public readonly HashSet<string> Marked = new();   // [MustTranslate] fields of this assembly

        const int CmpDist = 6;   // two operands, each a short load chain (closure: ldarg.0; ldfld; ldfld)
        // (type simple name, method, kind, max distance in instructions between the value and the call)
        static readonly (string type, string name, string kind, int dist)[] Sinks =
        {
            ("String", "op_Equality", "compare", CmpDist), ("String", "op_Inequality", "compare", CmpDist), ("String", "Equals", "compare", CmpDist),
            ("String", "Compare", "compare", CmpDist), ("String", "CompareOrdinal", "compare", CmpDist),
            ("Dictionary`2", "get_Item", "dictkey", 1), ("Dictionary`2", "ContainsKey", "dictkey", 1), ("Dictionary`2", "TryGetValue", "dictkey", 2),
            ("HashSet`1", "Contains", "dictkey", 1),
            ("DefDatabase`1", "GetNamed", "defref", 2), ("DefDatabase`1", "GetNamedSilentFail", "defref", 1),
            ("Translator", "Translate", "transkey", 1), ("Translator", "CanTranslate", "transkey", 1), ("Translator", "TryTranslate", "transkey", 2),
            ("TranslatorFormattedStringExtensions", "Translate", "transkey", 1),
            ("ContentFinder`1", "Get", "path", 2),
            ("GenTypes", "GetTypeInAnyAssembly", "type", 2), ("AccessTools", "TypeByName", "type", 1), ("Type", "GetType", "type", 1),
            ("ModsConfig", "IsActive", "modid", 1), ("ModLister", "GetActiveModWithIdentifier", "modid", 2), ("ModLister", "GetModWithIdentifier", "modid", 2),
        };
        static readonly Dictionary<string, (string kind, int dist)> SinkMap =
            Sinks.ToDictionary(s => s.type + "::" + s.name, s => (s.kind, s.dist));

        public static bool IsCompilerType(string key) => key.Contains("<>c") || key.Contains("DisplayClass") || key.Contains("<");

        MetadataReader md = null!;

        public static AsmScan Run(string path)
        {
            var r = new AsmScan();
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return r;
                r.md = pe.GetMetadataReader();
                var bodies = new List<(string method, byte[] il)>();
                foreach (var th in r.md.TypeDefinitions)
                {
                    var td = r.md.GetTypeDefinition(th);
                    var tn = r.TypeDefName(th);
                    foreach (var fh in td.GetFields())
                    {
                        var fd = r.md.GetFieldDefinition(fh);
                        foreach (var ah in fd.GetCustomAttributes())
                            if (r.AttrName(r.md.GetCustomAttribute(ah).Constructor) == "MustTranslateAttribute") { r.Marked.Add(tn + "." + r.md.GetString(fd.Name)); break; }
                    }
                    foreach (var mh in td.GetMethods())
                    {
                        var m = r.md.GetMethodDefinition(mh);
                        if (m.RelativeVirtualAddress == 0) continue;
                        byte[]? il;
                        try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
                        if (il != null) bodies.Add((tn + "::" + r.md.GetString(m.Name), il));
                    }
                }
                foreach (var (method, il) in bodies) r.Method(method, il);
            }
            catch { }   // unreadable assembly: no usage known, fields extracted as before
            r.md = null!;
            return r;
        }

        readonly struct Ins
        {
            public readonly ushort Op; public readonly int Tok; public readonly int Pos;
            public Ins(ushort op, int tok, int pos) { Op = op; Tok = tok; Pos = pos; }
        }

        void Method(string method, byte[] il)
        {
            var list = Decode(il);
            if (list == null) return;
            // proxy: a short method ending with "ldfld/ldsfld <string field> ; ret" (or returning another proxy)
            if (list.Count <= 8 && list.Count >= 2 && list[^1].Op == 0x2A)
            {
                var src = Source(list[^2]);
                if (src != null) Proxies.TryAdd(method, src);
            }
            var isConfigErrors = method.Contains("ConfigErrors") || method.EndsWith("::GroupsWith") || method.EndsWith("::MergeWith");
            var locals = new Dictionary<int, string>();
            var recent = new List<(string src, int at)>();
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                var s = Source(ins);
                if (s != null) { recent.Add((s, i)); continue; }
                if (ins.Op == 0x72) { recent.Add(("L:", i)); continue; }   // string literal: an operand of a comparison
                int loc = LocIndex(ins.Op, ins.Tok, store: false);
                if (loc >= 0) { if (locals.TryGetValue(loc, out var ls)) recent.Add((ls, i)); continue; }
                int st = LocIndex(ins.Op, ins.Tok, store: true);
                if (st >= 0)
                {
                    if (recent.Count > 0 && recent[^1].at == i - 1) locals[st] = recent[^1].src; else locals.Remove(st);
                    continue;
                }
                if (ins.Op == 0x7D || ins.Op == 0x80)   // stfld / stsfld into a closure field: alias
                {
                    var fk = FieldKey(ins.Tok, out var isString);
                    if (fk != null && isString && IsCompilerType(fk) && recent.Count > 0 && recent[^1].at == i - 1 && recent[^1].src != "L:")
                        Aliases.TryAdd(fk, recent[^1].src);
                    continue;
                }
                if (ins.Op is 0x28 or 0x6F)   // call / callvirt
                {
                    var (tn, mn) = MethodName(ins.Tok);
                    if (tn == null) continue;
                    var simple = tn[(Math.Max(tn.LastIndexOf('.'), tn.LastIndexOf('+')) + 1)..];
                    if (!SinkMap.TryGetValue(simple + "::" + mn, out var sink)) continue;
                    if (sink.kind == "compare" && i > 0 && IsEmptyOrNull(list[i - 1])) continue;   // x == "" / x == null
                    // consistency checks (duplicate labels) compare translated values with each other, not with English data
                    if (sink.kind == "compare" && isConfigErrors) continue;
                    // operands: a comparison takes the last two values loaded (a literal counts), other calls their last one
                    var window = new List<string>();
                    for (int k = recent.Count - 1; k >= 0 && i - recent[k].at <= sink.dist && window.Count < (sink.kind == "compare" ? 2 : 1); k--)
                        window.Add(recent[k].src);
                    foreach (var src in window.Distinct())
                    {
                        if (src == "L:") continue;
                        if (sink.kind == "compare" && window.Count(w => w == src) > 1) continue;   // a.label == b.label
                        Hits.Add((src, sink.kind, method, sink.kind == "compare" ? window.Where(w => w != src && w != "L:").ToArray() : Array.Empty<string>()));
                    }
                }
            }
        }

        bool IsEmptyOrNull(Ins p)
        {
            if (p.Op == 0x14) return true;   // ldnull
            if (p.Op == 0x72) { try { return md.GetUserString(MetadataTokens.UserStringHandle(p.Tok & 0x00FFFFFF)).Length == 0; } catch { return false; } }
            if (p.Op == 0x7E) { var k = FieldKey(p.Tok, out _); return k == "System.String.Empty"; }
            return false;
        }

        /// <summary>A string value read from a field, or returned by a call (resolved later if the method is a proxy).</summary>
        string? Source(Ins ins)
        {
            if (ins.Op is 0x7B or 0x7E)   // ldfld / ldsfld
            {
                var k = FieldKey(ins.Tok, out var isString);
                return k != null && isString && !k.StartsWith("System.") ? "F:" + k : null;
            }
            if (ins.Op is 0x28 or 0x6F)
            {
                var (tn, mn) = MethodName(ins.Tok);
                if (tn == null || tn.StartsWith("System.") || !ReturnsString(ins.Tok)) return null;
                return "M:" + tn + "::" + mn;
            }
            return null;
        }

        static int LocIndex(ushort op, int tok, bool store) => store
            ? op switch { 0x0A => 0, 0x0B => 1, 0x0C => 2, 0x0D => 3, 0x13 => tok, 0xFE0E => tok, _ => -1 }
            : op switch { 0x06 => 0, 0x07 => 1, 0x08 => 2, 0x09 => 3, 0x11 => tok, 0xFE0C => tok, _ => -1 };

        string AttrName(EntityHandle ctor)
        {
            try
            {
                if (ctor.Kind == HandleKind.MemberReference && md.GetMemberReference((MemberReferenceHandle)ctor).Parent is { Kind: HandleKind.TypeReference } pr)
                    return md.GetString(md.GetTypeReference((TypeReferenceHandle)pr).Name);
                if (ctor.Kind == HandleKind.MethodDefinition)
                    return md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()).Name);
            }
            catch { }
            return "";
        }

        string? FieldKey(int tok, out bool isString)
        {
            isString = false;
            try
            {
                var h = MetadataTokens.EntityHandle(tok);
                BlobHandle sig; string name; string? type;
                if (h.Kind == HandleKind.FieldDefinition)
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                    sig = f.Signature; name = md.GetString(f.Name); type = TypeDefName(f.GetDeclaringType());
                }
                else if (h.Kind == HandleKind.MemberReference)
                {
                    var m = md.GetMemberReference((MemberReferenceHandle)h);
                    if (m.GetKind() != MemberReferenceKind.Field) return null;
                    sig = m.Signature; name = md.GetString(m.Name); type = ParentName(m.Parent);
                }
                else return null;
                var b = md.GetBlobReader(sig);
                b.ReadByte();   // FIELD
                isString = b.ReadByte() == 0x0E;
                return type == null ? null : type + "." + name;
            }
            catch { return null; }
        }

        (string? type, string name) MethodName(int tok)
        {
            try
            {
                var h = MetadataTokens.EntityHandle(tok);
                if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
                if (h.Kind == HandleKind.MethodDefinition)
                {
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                    return (TypeDefName(m.GetDeclaringType()), md.GetString(m.Name));
                }
                if (h.Kind == HandleKind.MemberReference)
                {
                    var m = md.GetMemberReference((MemberReferenceHandle)h);
                    return (ParentName(m.Parent), md.GetString(m.Name));
                }
            }
            catch { }
            return (null, "");
        }

        bool ReturnsString(int tok)
        {
            try
            {
                var h = MetadataTokens.EntityHandle(tok);
                if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
                BlobHandle sig = h.Kind switch
                {
                    HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)h).Signature,
                    HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)h).Signature,
                    _ => default
                };
                if (sig.IsNil) return false;
                var b = md.GetBlobReader(sig);
                var header = b.ReadSignatureHeader();
                if (header.IsGeneric) b.ReadCompressedInteger();
                b.ReadCompressedInteger();   // parameter count
                return b.ReadByte() == 0x0E;
            }
            catch { return false; }
        }

        // "Namespace.Outer+Inner", as System.Type.FullName (MetadataLoadContext) writes it
        string TypeDefName(TypeDefinitionHandle th)
        {
            var td = md.GetTypeDefinition(th);
            var name = md.GetString(td.Name);
            var decl = td.GetDeclaringType();
            if (!decl.IsNil) return TypeDefName(decl) + "+" + name;
            var ns = md.GetString(td.Namespace);
            return ns == "" ? name : ns + "." + name;
        }

        string? ParentName(EntityHandle p)
        {
            switch (p.Kind)
            {
                case HandleKind.TypeDefinition: return TypeDefName((TypeDefinitionHandle)p);
                case HandleKind.TypeReference:
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)p);
                    var name = md.GetString(tr.Name);
                    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return ParentName(tr.ResolutionScope) + "+" + name;
                    var ns = md.GetString(tr.Namespace);
                    return ns == "" ? name : ns + "." + name;
                }
                case HandleKind.TypeSpecification:
                {
                    var b = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)p).Signature);
                    if (b.ReadByte() != 0x15) return null;   // GENERICINST
                    b.ReadByte();                             // CLASS / VALUETYPE
                    return ParentName(b.ReadTypeHandle());
                }
            }
            return null;
        }

        /// <summary>Instruction list (opcode, 4-byte token or small operand). Null if the body cannot be decoded.</summary>
        static List<Ins>? Decode(byte[] il)
        {
            var res = new List<Ins>(il.Length / 3);
            int i = 0;
            try
            {
                while (i < il.Length)
                {
                    int pos = i;
                    ushort op = il[i++];
                    if (op == 0xFE) op = (ushort)(0xFE00 | il[i++]);
                    int tok = 0;
                    switch (OperandSize(op))
                    {
                        case 0: break;
                        case 1: tok = il[i]; i += 1; break;
                        case 2: tok = BitConverter.ToUInt16(il, i); i += 2; break;
                        case 4: tok = BitConverter.ToInt32(il, i); i += 4; break;
                        case 8: i += 8; break;
                        case -1: { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }   // switch
                    }
                    res.Add(new Ins(op, tok, pos));
                }
            }
            catch { return null; }
            return res;
        }

        static int OperandSize(ushort op)
        {
            switch (op)
            {
                case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: case 0x1F: case 0xDE: case 0xFE12: case 0xFE19:
                    return 1;
                case 0xFE09: case 0xFE0A: case 0xFE0B: case 0xFE0C: case 0xFE0D: case 0xFE0E:
                    return 2;
                case 0x21: case 0x23:
                    return 8;
                case 0x45:
                    return -1;
                case 0x20: case 0x22: case 0x27: case 0x28: case 0x29: case 0x79: case 0x8C: case 0x8D: case 0x8F:
                case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0: case 0xDD:
                case 0xFE06: case 0xFE07: case 0xFE15: case 0xFE16: case 0xFE1C:
                    return 4;
            }
            if (op >= 0x2B && op <= 0x37) return 1;   // short branches
            if (op >= 0x38 && op <= 0x44) return 4;   // long branches
            if (op >= 0x6F && op <= 0x75) return 4;   // callvirt .. isinst
            if (op >= 0x7B && op <= 0x81) return 4;   // field ops, stobj
            return 0;
        }
    }
}
