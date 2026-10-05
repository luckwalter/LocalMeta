using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.LocalMeta.Sources
{
    /// <summary>
    /// 本地资料源抽象。要加新源（新 sqlite、JSON 目录、HTTP 内部接口等），
    /// 实现这个接口再在 LocalMetaSourceFactory 里注册即可，provider 与计划任务都不用改。
    /// </summary>
    public interface ILocalMetaProfileSource
    {
        string Name { get; }

        /// <summary>
        /// 取身体资料。字段越界/缺失时返回 false，调用方直接跳过。
        /// </summary>
        bool TryGetProfile(string name, out PersonProfile profile);

        /// <summary>
        /// 取头像本地路径。
        /// </summary>
        bool TryGetAvatarPath(string name, out string path);
    }

    /// <summary>
    /// 一份人物身体资料。
    /// </summary>
    public sealed class PersonProfile
    {
        public int Bust { get; init; }

        public int Waist { get; init; }

        public int Hips { get; init; }

        public string Cup { get; init; } = string.Empty;

        public int Height { get; init; }

        public string Debut { get; init; } = string.Empty;
    }

    /// <summary>
    /// 名字归一化：去括号别名、压空白、按替换表校正。
    /// </summary>
    public static class NameNormalizer
    {
        private static readonly Regex _paren = new Regex(@"[（(].*?[）)]", RegexOptions.Compiled);

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var s = _paren.Replace(name, string.Empty).Trim();
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>
        /// 按配置里的替换表校正（"原名|替换名 " 用竖线分隔多条）。
        /// </summary>
        public static string ApplySubstitutes(string name, string rawRules)
        {
            if (string.IsNullOrWhiteSpace(rawRules))
            {
                return name;
            }

            foreach (var line in rawRules.Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2 && parts[0].Trim() == name.Trim())
                {
                    return parts[1].Trim();
                }
            }

            return name;
        }
    }

    /// <summary>
    /// JavBoss 本地 sqlite 资料源（jav_idol 表）。
    /// cup 列是数字索引，映射规则见 PluginConfiguration.CupLetters。
    /// </summary>
    public sealed class JavBossSqliteSource : ILocalMetaProfileSource
    {
        private readonly string _dbPath;
        private readonly string _cupLetters;
        private readonly int _heightMin;
        private readonly int _heightMax;
        private readonly int _girthMin;
        private readonly int _girthMax;
        // 缓存"已消费完的 profile"，不是缓存 SqliteDataReader：
        // reader 依赖 SqliteConnection，若把 reader 缓存下来，
        // 连接随 using 释放后，第二次读取就会抛 "connection closed"。
        private readonly Dictionary<string, PersonProfile> _cache =
            new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);

        public JavBossSqliteSource(string dbPath, PluginConfiguration config)
        {
            _dbPath = dbPath;
            _cupLetters = string.IsNullOrEmpty(config.CupLetters) ? "ABCDEFGHIJKLMNOPQ" : config.CupLetters;
            _heightMin = config.HeightMin;
            _heightMax = config.HeightMax;
            _girthMin = config.GirthMin;
            _girthMax = config.GirthMax;
        }

        public string Name => "JavBoss(local)";

        public bool TryGetProfile(string name, out PersonProfile profile)
        {
            profile = null;
            if (!File.Exists(_dbPath))
            {
                return false;
            }

            var key = NameNormalizer.Normalize(name);
            if (_cache.TryGetValue(key, out var cached))
            {
                // 缓存里没命中过（null）也直接返回，避免每次都去查库
                profile = cached;
                return profile != null;
            }

            profile = ReadProfile(key);
            _cache[key] = profile;
            return profile != null;
        }

        /// <summary>
        /// 查一次库并当场把 reader 消费完，只把结果带回来。
        /// </summary>
        private PersonProfile ReadProfile(string key)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT bust, waist, hips, height_cm, cup, birth_date FROM jav_idol WHERE name=$n " +
                "UNION ALL " +
                "SELECT bust, waist, hips, height_cm, cup, birth_date FROM jav_idol WHERE japanese_name=$n LIMIT 1";
            cmd.Parameters.AddWithValue("$n", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            int SafeInt(object v)
            {
                return int.TryParse(v?.ToString() ?? string.Empty, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var i) ? i : 0;
            }

            var bust = SafeInt(reader["bust"]);
            var waist = SafeInt(reader["waist"]);
            var hips = SafeInt(reader["hips"]);
            var height = SafeInt(reader["height_cm"]);

            // 脏数据拦截：越界一律不写
            bool GirthOk(int v) => v >= _girthMin && v <= _girthMax;
            if (!GirthOk(bust) || !GirthOk(waist) || !GirthOk(hips) ||
                height < _heightMin || height > _heightMax)
            {
                return null;
            }

            // cup 为 NULL 时必须留空，不能按索引 0 映射成 A。
            // 数据源实测 1456 条里有 1078 条 cup 为空，按 0 处理会把这些人错写成 A 罩杯。
            var cup = string.Empty;
            var cupRaw = reader["cup"];
            if (cupRaw != null && cupRaw != DBNull.Value)
            {
                var cupIdx = SafeInt(cupRaw);
                if (cupIdx >= 0 && cupIdx < _cupLetters.Length)
                {
                    cup = _cupLetters[cupIdx].ToString();
                }
            }

            var debut = string.Empty;
            var m = Regex.Match(reader["birth_date"]?.ToString() ?? string.Empty, @"(\d{4})-(\d{1,2})-(\d{1,2})");
            if (m.Success)
            {
                debut = string.Format(CultureInfo.InvariantCulture, "{0}年{1}月{2}日",
                    m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            }

            return new PersonProfile
            {
                Bust = bust,
                Waist = waist,
                Hips = hips,
                Cup = cup,
                Height = height,
                Debut = debut
            };
        }

        public bool TryGetAvatarPath(string name, out string path)
        {
            path = null;
            return false;
        }

    }

    /// <summary>
    /// Gfriends 导出的头像目录源：gfriends_plan.json 做 名字->编号 索引，avatars/ 放实体文件。
    /// </summary>
    public sealed class GfriendsAvatarSource : ILocalMetaProfileSource
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public GfriendsAvatarSource(string root)
        {
            _root = root;
            var plan = Path.Combine(root ?? string.Empty, "gfriends_plan.json");
            if (!File.Exists(plan))
            {
                return;
            }

            // plan 是 [[编号, 名字, 标签, 文件名], ...] 的二维数组。
            // 不能用 DeserializeObject<IEnumerable> —— Newtonsoft 无法实例化接口类型，
            // 会抛 JsonSerializationException，进而让整个 provider 创建失败。
            // 解析失败时只丢这一个源，不往上抛，避免拖垮插件。
            Newtonsoft.Json.Linq.JArray rows;
            try
            {
                rows = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(plan));
            }
            catch (Exception)
            {
                return;
            }

            foreach (var item in rows)
            {
                if (item is not Newtonsoft.Json.Linq.JArray arr || arr.Count < 2)
                {
                    continue;
                }

                var gid = arr[0]?.ToString() ?? string.Empty;
                var nm = arr[1]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(nm))
                {
                    continue;
                }
                if (!_byName.ContainsKey(nm))
                {
                    _byName[nm] = gid;
                }

                var bare = NameNormalizer.Normalize(nm);
                if (!_byName.ContainsKey(bare))
                {
                    _byName[bare] = gid;
                }
            }
        }

        public string Name => "Gfriends(local)";

        public bool TryGetProfile(string name, out PersonProfile profile)
        {
            profile = null;
            return false;
        }

        public bool TryGetAvatarPath(string name, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(_root) || !_byName.TryGetValue(name, out var gid))
            {
                return false;
            }

            foreach (var ext in new[] { ".jpg", ".webp" })
            {
                var p = Path.Combine(_root, "avatars", gid + ext);
                if (File.Exists(p))
                {
                    path = p;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 源装配。以后加新源在这里挂一行。
    /// </summary>
    public static class LocalMetaSourceFactory
    {
        public static List<ILocalMetaProfileSource> Build(PluginConfiguration config, string appDir)
        {
            var list = new List<ILocalMetaProfileSource>();

            // 头像根目录：优先认为 AvatarSourceDir 就是 Gfriends 导出目录
            // （其下直接有 gfriends_plan.json + avatars/），找不到再看下一级 gf/ 子目录
            // （旧布局，兼容既有部署）。
            var avDir = string.IsNullOrWhiteSpace(config.AvatarSourceDir)
                ? appDir
                : config.AvatarSourceDir;
            var avatars = ResolveAvatarRoot(avDir);
            if (!string.IsNullOrEmpty(avatars))
            {
                list.Add(new GfriendsAvatarSource(avatars));
            }

            var db = config.ProfileDbPath;
            if (string.IsNullOrWhiteSpace(db) || !File.Exists(db))
            {
                db = FindJavBossDb(appDir);
            }

            if (!string.IsNullOrEmpty(db) && File.Exists(db))
            {
                list.Add(new JavBossSqliteSource(db, config));
            }

            return list;
        }

        /// <summary>
        /// 定位 Gfriends 头像根目录。支持两种布局：
        /// 目录本身含 gfriends_plan.json，或下一级 gf/ 子目录含它。
        /// 找不到返回空串（调用方跳过该源）。
        /// </summary>
        private static string ResolveAvatarRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return string.Empty;
            }

            if (File.Exists(Path.Combine(dir, "gfriends_plan.json")))
            {
                return dir;
            }

            var sub = Path.Combine(dir, "gf");
            return File.Exists(Path.Combine(sub, "gfriends_plan.json")) ? sub : string.Empty;
        }

        private static string FindJavBossDb(string appDir)
        {
            foreach (var dir in new[] { appDir, Path.Combine(appDir ?? string.Empty, "data") })
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var p = Path.Combine(dir, "javboss.db");
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return string.Empty;
        }
    }
}
