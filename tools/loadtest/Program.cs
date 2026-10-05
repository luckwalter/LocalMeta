using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

class LoadTest
{
    // 不写死绝对路径：优先命令行参数，其次按常见布局自动探测。
    // 用法：
    //   loadtest <插件publish目录> [Jellyfin安装目录]
    //   loadtest                       # 两处都自动猜
    static string PluginDir;
    static string JellyfinDir;

    static int Main(string[] args)
    {
        PluginDir = args.Length > 0 ? args[0] : FindPluginDir();
        JellyfinDir = args.Length > 1 ? args[1] : FindJellyfinDir();

        if (PluginDir == null)
        {
            Console.WriteLine("FAIL: 找不到插件 publish 目录。用法: loadtest <publish目录> [Jellyfin目录]");
            return 1;
        }
        if (JellyfinDir == null)
        {
            Console.WriteLine("WARN: 找不到 Jellyfin 安装目录，程序集解析可能失败。用法: loadtest <publish目录> <Jellyfin目录>");
        }

        Console.WriteLine("插件目录: " + PluginDir);
        Console.WriteLine("Jellyfin : " + (JellyfinDir ?? "(未找到)"));
        Console.WriteLine();

        // 程序集解析：先找插件目录，再找 Jellyfin 本体目录
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var dir in new[] { PluginDir, JellyfinDir })
            {
                var p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p))
                {
                    try { return ctx.LoadFromAssemblyPath(p); } catch { }
                }
            }
            return null;
        };

        var dll = Path.Combine(PluginDir, "Jellyfin.Plugin.LocalMeta.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("FAIL: 找不到 " + dll);
            return 1;
        }

        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(dll);
            Console.WriteLine("OK  : 程序集加载成功 -> " + asm.FullName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: 加载异常 " + ex);
            return 1;
        }

        // 1) 逐个类型强制加载，触发 TypeLoadException
        var types = new List<Type>();
        foreach (var t in asm.GetTypes())
        {
            try
            {
                // 触发类型初始化与基类/接口解析
                _ = t.BaseType;
                _ = t.GetInterfaces();
                types.Add(t);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: 类型加载失败 " + t.FullName + " -> " + ex.Message);
                return 1;
            }
        }
        Console.WriteLine("OK  : 类型全部解析成功，共 " + types.Count + " 个");

        // 2) 找插件主类，验证它确实是 BasePlugin<LocalMetaConfig> 且实现 IHasWebPages
        var pluginType = types.FirstOrDefault(t => t.Name == "Plugin");
        if (pluginType == null) { Console.WriteLine("FAIL: 找不到 Plugin 类"); return 1; }

        var cfgType = asm.GetType("Jellyfin.Plugin.LocalMeta.PluginConfiguration");
        var basePluginGeneric = typeof(MediaBrowser.Common.Plugins.BasePlugin<>).MakeGenericType(cfgType);
        Console.WriteLine((pluginType.BaseType == basePluginGeneric ? "OK  : " : "FAIL: ")
            + "Plugin 基类 = " + Describe(pluginType.BaseType));

        var hasWeb = typeof(MediaBrowser.Model.Plugins.IHasWebPages).IsAssignableFrom(pluginType);
        Console.WriteLine((hasWeb ? "OK  : " : "FAIL: ") + "Plugin 实现 IHasWebPages");

        // 3) 配置类基类
        var cfgBase = cfgType?.BaseType;
        var expectBase = typeof(MediaBrowser.Model.Plugins.BasePluginConfiguration);
        Console.WriteLine((cfgBase == expectBase ? "OK  : " : "FAIL: ")
            + "PluginConfiguration 基类 = " + Describe(cfgBase));

        // 4) 两个 provider 是否被 Jellyfin 识别
        CheckProvider(asms: new[] { asm },
            iface: typeof(MediaBrowser.Controller.Providers.ILocalMetadataProvider<MediaBrowser.Controller.Entities.Person>),
            label: "ILocalMetadataProvider<Person>");

        CheckProvider(new[] { asm },
            typeof(MediaBrowser.Controller.Providers.IRemoteImageProvider),
            "IRemoteImageProvider");

        CheckProvider(new[] { asm },
            typeof(MediaBrowser.Model.Tasks.IScheduledTask),
            "IScheduledTask");

        // 5) 计划任务 Key 可用
        var taskType = types.FirstOrDefault(t => typeof(MediaBrowser.Model.Tasks.IScheduledTask).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
        if (taskType != null)
        {
            var inst = Activator.CreateInstance(taskType, new object[] { null, null });
            var key = taskType.GetProperty("Key")?.GetValue(inst) as string;
            Console.WriteLine((string.IsNullOrEmpty(key) ? "FAIL: " : "OK  : ") + "IScheduledTask.Key = " + key);
        }

        // 6) 嵌入资源可达
        var resNames = asm.GetManifestResourceNames();
        Console.WriteLine((resNames.Any(n => n.EndsWith("configPage.html")) ? "OK  : " : "FAIL: ")
            + "嵌入资源: " + string.Join(", ", resNames));

        return 0;
    }

    static void CheckProvider(IEnumerable<Assembly> asms, Type iface, string label)
    {
        var found = false;
        foreach (var a in asms)
        {
            foreach (var t in a.GetTypes())
            {
                if (t.IsInterface || t.IsAbstract) continue;
                if (iface.IsAssignableFrom(t)) { found = true; break; }
            }
            if (found) break;
        }
        Console.WriteLine((found ? "OK  : " : "FAIL: ") + label + (found ? " 已实现" : " 未找到实现"));
    }

    /// <summary>在仓库常见布局里找插件 publish 目录。</summary>
    static string FindPluginDir()
    {
        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(typeof(LoadTest).Assembly.Location),
        };
        var rel = Path.Combine("b-plugin", "Jellyfin.Plugin.LocalMeta", "bin", "Release", "net9.0", "publish");
        var relWin = rel.Replace('/', Path.DirectorySeparatorChar);

        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            var p = Path.Combine(r, relWin);
            if (File.Exists(Path.Combine(p, "Jellyfin.Plugin.LocalMeta.dll"))) return p;
        }
        return null;
    }

    /// <summary>探测 Jellyfin 安装目录（默认 C:\Jellyfin，也认 Linux/macOS 常见位置）。</summary>
    static string FindJellyfinDir()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("JELLYFIN_DIR"),
            @"C:\Jellyfin",
            "/usr/lib/jellyfin",
            "/var/lib/jellyfin",
            "/Applications/Jellyfin.app/Contents/Resources",
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (Directory.Exists(Path.Combine(c, "MediaBrowser.Controller.dll"))) return c;
        }
        return null;
    }

    static string Describe(Type t)
    {
        if (t == null) return "null";
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        var n = t.Name;
        var i = n.IndexOf('`');
        if (i > 0) n = n.Substring(0, i);
        return t.Namespace + "." + n + "<" + string.Join(",", t.GetGenericArguments().Select(a => a.Name)) + ">";
    }
}
