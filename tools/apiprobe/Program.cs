using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class SigProv : ISignatureTypeProvider<string, object>
{
    internal static MetadataReader _mr;

    public static string TName(EntityHandle h)
    {
        try
        {
            if (h.Kind == HandleKind.TypeDefinition)
            {
                var td = _mr.GetTypeDefinition((TypeDefinitionHandle)h);
                var ns = _mr.GetString(td.Namespace);
                var n = _mr.GetString(td.Name);
                return (ns.Length > 0 ? ns + "." : "") + n;
            }
            if (h.Kind == HandleKind.TypeReference)
            {
                var tr = _mr.GetTypeReference((TypeReferenceHandle)h);
                var ns = _mr.GetString(tr.Namespace);
                var n = _mr.GetString(tr.Name);
                return (ns.Length > 0 ? ns + "." : "") + n;
            }
            if (h.Kind == HandleKind.TypeSpecification)
            {
                var ts = _mr.GetTypeSpecification((TypeSpecificationHandle)h);
                return ts.DecodeSignature(new SigProv(), null);
            }
        }
        catch { }
        return h.Kind.ToString();
    }

    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object gc, int i) => "!!" + i;
    public string GetGenericTypeParameter(object gc, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind) => TName(h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind) => TName(h);
    public string GetTypeFromSpecification(MetadataReader r, object gc, TypeSpecificationHandle h, byte rawKind) => TName(h);
}

class Program
{
    static string Render(EntityHandle h)
    {
        try { return SigProv.TName(h); }
        catch { return "?"; }
    }

    // 关键：PEReader 与 MetadataReader 必须在同一个活着的 using 作用域内使用
    static void ScanFile(string path, HashSet<string> want, TextWriter w)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return;

            var mr = pe.GetMetadataReader();
            SigProv._mr = mr;

            bool any = false;
            foreach (var th in mr.TypeDefinitions)
            {
                TypeDefinition td;
                string nm;
                try
                {
                    td = mr.GetTypeDefinition(th);
                    nm = mr.GetString(td.Name);
                }
                catch { continue; }

                if (!want.Contains(nm)) continue;
                if (!any) { w.WriteLine("======== " + Path.GetFileName(path) + " ========"); any = true; }

                string ns = mr.GetString(td.Namespace);
                w.WriteLine("### " + (ns.Length > 0 ? ns + "." : "") + nm);

                var ifaces = new List<string>();
                foreach (var ih in td.GetInterfaceImplementations())
                {
                    var ii = mr.GetInterfaceImplementation(ih);
                    ifaces.Add(Render(ii.Interface));
                }
                string b;
                try { b = td.BaseType.IsNil ? "-none-" : Render(td.BaseType); } catch { b = "?"; }
                w.WriteLine("  base   : " + b);
                w.WriteLine("  ifaces : " + (ifaces.Count > 0 ? string.Join(", ", ifaces) : "-"));

                foreach (var mh in td.GetMethods())
                {
                    MethodDefinition md;
                    string mn;
                    try
                    {
                        md = mr.GetMethodDefinition(mh);
                        mn = mr.GetString(md.Name);
                    }
                    catch { continue; }

                    if (mn.StartsWith("<") || mn.StartsWith("get_") || mn.StartsWith("set_")) continue;

                    string sig;
                    try
                    {
                        var dec = md.DecodeSignature(new SigProv(), null);
                        sig = dec.ReturnType + " " + mn + "(" + string.Join(", ", dec.ParameterTypes) + ")";
                    }
                    catch { sig = mn; }

                    var flags = md.Attributes;
                    string attrs = "";
                    if ((flags & MethodAttributes.Abstract) != 0) attrs += "abstract ";
                    if ((flags & MethodAttributes.Virtual) != 0) attrs += "virtual ";
                    if ((flags & MethodAttributes.Static) != 0) attrs += "static ";
                    w.WriteLine("    " + attrs.Trim() + " " + sig);
                }

                foreach (var ph in td.GetProperties())
                {
                    try
                    {
                        var pd = mr.GetPropertyDefinition(ph);
                        w.WriteLine("    prop  : " + mr.GetString(pd.Name));
                    }
                    catch { }
                }

                // 枚举：列出成员名（这就是 GetDefaultTriggers 里能填的值）
                var attrs2 = td.Attributes;
                if ((attrs2 & TypeAttributes.Interface) == 0)
                {
                    foreach (var fh in td.GetFields())
                    {
                        try
                        {
                            var fd = mr.GetFieldDefinition(fh);
                            var fn = mr.GetString(fd.Name);
                            if (fn.StartsWith("value__")) continue;
                            w.WriteLine("    enum  : " + fn);
                        }
                        catch { }
                    }
                }

                w.WriteLine();
            }
        }
        catch (Exception ex)
        {
            w.WriteLine("[skip " + Path.GetFileName(path) + ": " + ex.GetType().Name + "]");
        }
    }

    // 列出某命名空间下所有类型（模式 "NS:MediaBrowser.Controller.Providers"）
    static void ListNamespace(string dir, string nsFilter, TextWriter w)
    {
        w.WriteLine("======== NAMESPACE " + nsFilter + " ========");
        foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x))
        {
            try
            {
                using var fs = File.OpenRead(f);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var mr = pe.GetMetadataReader();
                SigProv._mr = mr;

                foreach (var th in mr.TypeDefinitions)
                {
                    TypeDefinition td;
                    string ns, nm;
                    try
                    {
                        td = mr.GetTypeDefinition(th);
                        ns = mr.GetString(td.Namespace);
                        nm = mr.GetString(td.Name);
                    }
                    catch { continue; }

                    if (ns != nsFilter) continue;
                    string kind = "class";
                    try
                    {
                        var a = td.Attributes;
                        if ((a & TypeAttributes.Interface) != 0) kind = "interface";
                        else if ((a & TypeAttributes.Sealed) != 0 && (a & TypeAttributes.Abstract) != 0) kind = "static";
                    }
                    catch { }

                    var bases = new List<string>();
                    try
                    {
                        if (!td.BaseType.IsNil) bases.Add(Render(td.BaseType));
                        foreach (var ih in td.GetInterfaceImplementations())
                        {
                            var ii = mr.GetInterfaceImplementation(ih);
                            bases.Add(Render(ii.Interface));
                        }
                    }
                    catch { }

                    w.WriteLine("  [" + kind + "] " + nm + (bases.Count > 0 ? "  : " + string.Join(", ", bases) : ""));
                }
            }
            catch { }
        }
        w.WriteLine();
    }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"C:\Jellyfin";

        // 命名空间列举模式
        var nsArgs = args.Skip(1).Where(a => a.StartsWith("NS:")).Select(a => a.Substring(3)).ToArray();
        if (nsArgs.Length > 0)
        {
            var w2 = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            foreach (var ns in nsArgs) ListNamespace(dir, ns, w2);
            w2.Flush();
            return;
        }

        var want = new HashSet<string>(new[] {
            "IPluginConfiguration", "BasePluginConfiguration", "BasePlugin`1",
            "BaseItemKind", "RemoteImageInfo", "IRemoteImageProvider",
            "IScheduledTask", "IMetadataProvider`1", "MetadataResult`1",
            "PluginPageInfo", "IHasWebPages", "IPlugin", "TaskTriggerInfo",
        });
        if (args.Length > 1)
        {
            want.Clear();
            foreach (var a in args.Skip(1)) want.Add(a);
        }

        var w = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
        w.AutoFlush = false;
        foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x))
            ScanFile(f, want, w);
        w.Flush();
    }
}
