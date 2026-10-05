using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMeta.Providers;
using Jellyfin.Plugin.LocalMeta.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMeta.Tasks
{
    /// <summary>
    /// 每日兜底任务：扫缺口 → 补头像文件 + 补简介 → 直写 jellyfin.db。
    /// 与 provider 路径共用同一套判定逻辑，只在字段为空时写，重复执行结果不变。
    /// </summary>
    public class LocalMetaBackfillTask : IScheduledTask
    {
        private readonly ILogger<LocalMetaBackfillTask> _logger;
        private readonly IApplicationPaths _appPaths;

        public LocalMetaBackfillTask(ILogger<LocalMetaBackfillTask> logger, IApplicationPaths appPaths)
        {
            _logger = logger;
            _appPaths = appPaths;
        }

        public string Name => "LocalMeta 人物资料补齐";

        /// <summary>
        /// 任务唯一标识。10.11 的 IScheduledTask 新增了 Key 成员（实测签名取证），
        /// 缺它编译不过。Jellyfin 用它记录上次执行时间，改名会导致状态丢失。
        /// </summary>
        public string Key => "LocalMetaActorRefill";

        public string Description => "扫描指定媒体库的演员，补齐缺失的头像与简介（只补空，不覆盖已有内容）。";

        public string Category => "LocalMeta";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 10.11 实测：枚举类型是 TaskTriggerInfoType（不是 TaskTriggerInfo.TriggerInterval），
            // 成员有 DailyTrigger / WeeklyTrigger / IntervalTrigger / StartupTrigger。
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            };
        }

        /// <summary>
        /// 10.11 实测签名：ExecuteAsync(IProgress&lt;double&gt;, CancellationToken)。
        /// 参数顺序是 progress 在前，旧版文档里多是反的。
        /// </summary>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.EnablePlugin)
            {
                _logger.LogInformation("[LocalMeta] 插件未启用，跳过");
                return;
            }

            // appPaths.DataPath 本身就是 .../Data/data，jellyfin.db 就在它下面。
            // 以前这里又拼了一层 "data"，结果是 .../Data/data/data/jellyfin.db，找不到库。
            var db = Path.Combine(_appPaths.DataPath, "jellyfin.db");
            if (!File.Exists(db))
            {
                _logger.LogWarning("[LocalMeta] 找不到数据库: {Db}", db);
                return;
            }

            var sources = LocalMetaSourceFactory.Build(cfg, _appPaths.DataPath);
            _logger.LogInformation("[LocalMeta] 任务开始，资料源 {Count} 个", sources.Count);

            var targets = LoadTargets(db, cfg);
            _logger.LogInformation("[LocalMeta] 库内演员 {Count} 人，缺图 {Img} / 缺简介 {Bio}",
                targets.Count, targets.Count(t => !t.HasImage), targets.Count(t => !t.HasBio));

            var limit = Math.Min(cfg.MaxItemsPerRun, targets.Count);
            var done = 0;
            var filledImg = 0;
            var filledBio = 0;

            // 先只做文件 + 内存判断，最后一次性写库，减少 sqlite 争用
            var imageWrites = new List<(string ItemId, string Path, int W, int H)>();
            var bioUpdates = new List<(string PersonId, string Text)>();

            foreach (var t in targets.Take(limit))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var name = NameNormalizer.ApplySubstitutes(t.Name, cfg.NameSubstitutes);

                // OverwriteExisting 默认 false = 只补空。开启后已有内容也会被源覆盖，
                // 会把远程刮削器刮到的资料一起冲掉，只在确认重刷时临时打开。
                if (cfg.ProvideImages && (cfg.OverwriteExisting || !t.HasImage))
                {
                    foreach (var src in sources)
                    {
                        if (src.TryGetAvatarPath(name, out var path) && File.Exists(path))
                        {
                            var dest = CopyAvatar(name, path);
                            if (!string.IsNullOrEmpty(dest))
                            {
                                var wh = ImageSize(dest);
                                // Peoples.Id 与 Person 实体 Id 都要写，否则影片页/人物页只有一个有图
                                foreach (var id in new[] { t.PeopleId, t.PersonId }
                                             .Where(x => !string.IsNullOrEmpty(x)).Distinct())
                                {
                                    imageWrites.Add((id, dest, wh.Item1, wh.Item2));
                                }

                                filledImg++;
                            }

                            break;
                        }
                    }
                }

                if (cfg.ProvideMetadata && (cfg.OverwriteExisting || !t.HasBio))
                {
                    foreach (var src in sources)
                    {
                        if (src.TryGetProfile(name, out var prof))
                        {
                            var text = LocalMetaPersonMetadataProvider.RenderOverview(prof, cfg);
                            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(t.PersonId))
                            {
                                bioUpdates.Add((t.PersonId, text));
                                filledBio++;
                            }

                            break;
                        }
                    }
                }

                done++;
                progress?.Report(done / (double)Math.Max(limit, 1) * 100);
            }

            if (cfg.BackupBeforeWrite && (imageWrites.Count > 0 || bioUpdates.Count > 0))
            {
                Backup(db, cfg);
            }

            WriteBack(db, imageWrites, bioUpdates, cfg);

            _logger.LogInformation("[LocalMeta] 任务完成：补图 {Img} / 补简介 {Bio}", filledImg, filledBio);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        // ------------------------------------------------- 扫描
        private sealed class Target
        {
            public string PeopleId { get; set; }

            public string PersonId { get; set; }

            public string Name { get; set; }

            public bool HasImage { get; set; }

            public bool HasBio { get; set; }
        }

        /// <summary>
        /// BaseItems.Type 里 Person 的实际值（全名，非短名 "Person"）。
        /// </summary>
        private const string PersonEntityType = "MediaBrowser.Controller.Entities.Person";

        private static string LookupPersonId(SqliteCommand cmd, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            cmd.CommandText = "SELECT Id FROM BaseItems WHERE Type=$t AND Name=$n LIMIT 1";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$t", PersonEntityType);
            cmd.Parameters.AddWithValue("$n", name);
            var r = cmd.ExecuteScalar();
            return r?.ToString();
        }

        private List<Target> LoadTargets(string db, PluginConfiguration cfg)
        {
            var targets = new List<Target>();
            var libs = (cfg.LibraryNames ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).ToList();

            using var conn = new SqliteConnection("Data Source=" + db);
            conn.Open();
            using var cmd = conn.CreateCommand();

            // 注意：不能图省事拼 IN ('%') 表示"不限库"，那是精确字符串比较，
            // 匹配不到任何 Id，会把整个任务变成空跑。筛名单为空就干脆不加库过滤。
            var filmIds = new List<string>();
            foreach (var lib in libs)
            {
                cmd.CommandText = "SELECT Id FROM BaseItems WHERE Name=$n LIMIT 1";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$n", lib);
                var r = cmd.ExecuteScalar();
                if (r != null)
                {
                    filmIds.Add(r.ToString());
                }
                else
                {
                    _logger.LogWarning("[LocalMeta] 找不到媒体库 {Lib}，已跳过", lib);
                }
            }

            var sql =
                "SELECT DISTINCT p.Id, p.Name FROM Peoples p " +
                "JOIN PeopleBaseItemMap m ON m.PeopleId=p.Id " +
                "JOIN AncestorIds a ON a.ItemId=m.ItemId " +
                "WHERE p.PersonType='Actor'";
            if (filmIds.Count > 0)
            {
                sql += " AND a.ParentItemId IN (" + string.Join(",", filmIds.Select(_ => "$f")) + ")";
            }

            cmd.CommandText = sql;
            cmd.Parameters.Clear();
            foreach (var f in filmIds)
            {
                cmd.Parameters.AddWithValue("$f", f);
            }

            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, string)>();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            reader.Close();

            foreach (var (pid, name) in rows)
            {
                // 实测：Peoples 里的名字带括号时，Person 条目有的也带括号（原样命中），
                // 有的只存主名（必须去括号才命中）。两种都要试，只做去括号会漏约 27 人。
                var personId = LookupPersonId(cmd, name)
                               ?? LookupPersonId(cmd, NameNormalizer.Normalize(name));

                var t = new Target { PeopleId = pid, PersonId = personId, Name = name };

                cmd.CommandText = "SELECT COUNT(*) FROM BaseItemImageInfos WHERE ItemId=$i AND ImageType=0";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$i", pid);
                t.HasImage = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

                if (!string.IsNullOrEmpty(personId))
                {
                    cmd.CommandText = "SELECT Overview FROM BaseItems WHERE Id=$i";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$i", personId);
                    var ov = cmd.ExecuteScalar();
                    t.HasBio = ov != null && !string.IsNullOrWhiteSpace(ov.ToString());
                }

                targets.Add(t);
            }

            return targets;
        }

        // ------------------------------------------------- 落盘 / 写库
        /// <summary>
        /// 元数据根目录（其下有 People/）。DataPath 是 .../Data/data，
        /// 所以这里取它的上一级 .../Data。
        /// </summary>
        private string MetadataRoot()
        {
            var parent = Directory.GetParent(_appPaths.DataPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var root = parent?.FullName ?? _appPaths.DataPath;
            return Path.Combine(root, "metadata");
        }

        private string CopyAvatar(string name, string src)
        {
            try
            {
                var relDir = Path.Combine(name.Substring(0, 1), name);
                // metadata/ 在数据根目录（DataPath 的上一级），不在 DataPath 下面
                var dir = Path.Combine(MetadataRoot(), "People", relDir);
                Directory.CreateDirectory(dir);
                var ext = Path.GetExtension(src);
                var dest = Path.Combine(dir, "folder" + ext);
                File.Copy(src, dest, true);
                return dest;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LocalMeta] 头像复制失败: {Name}", name);
                return null;
            }
        }

        private static (int, int) ImageSize(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var head = new byte[64];
                var n = fs.Read(head, 0, head.Length);
                if (n > 0 && head[0] == 0xFF && head[1] == 0xD8)
                {
                    for (var i = 2; i + 8 < n; i++)
                    {
                        if (head[i] != 0xFF)
                        {
                            continue;
                        }

                        var m = head[i + 1];
                        if (m == 0xC0 || m == 0xC1 || m == 0xC2 || m == 0xC3)
                        {
                            return (head[i + 7] << 8 | head[i + 6], head[i + 5] << 8 | head[i + 4]);
                        }

                        if (m == 0xD8 || m == 0xD9 || (m >= 0xD0 && m <= 0xD7))
                        {
                            i++;
                            continue;
                        }

                        i += 2 + (head[i + 2] << 8 | head[i + 3]);
                    }
                }
            }
            catch
            {
                // 读不到就按 0 处理，不影响补图
            }

            return (0, 0);
        }

        /// <summary>
        /// 备份文件名前缀。轮转只清理带这个前缀的，不碰别的程序产生的备份
        /// （实测目录里还有 .bak_ / .bak_bio_ 等手工备份）。
        /// </summary>
        private const string BackupPrefix = ".bak_localmeta_";

        private void Backup(string db, PluginConfiguration cfg)
        {
            try
            {
                var bak = db + BackupPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                File.Copy(db, bak, true);
                _logger.LogInformation("[LocalMeta] 已备份: {Bak}", Path.GetFileName(bak));

                RotateBackups(db, cfg.BackupMax);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LocalMeta] 备份失败，继续写库");
            }
        }

        /// <summary>
        /// 只保留最近 max 份备份，多出来的删掉。
        /// 不做轮转的话每天一份，实测每份约 47MB，一年就是 17GB。
        ///
        /// 排序依据是文件名里的 yyyyMMdd_HHmmss，字典序等于时间序。
        /// </summary>
        private void RotateBackups(string db, int max)
        {
            try
            {
                var keep = Math.Max(1, max);
                var dir = Path.GetDirectoryName(db);
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }

                var olds = Directory.GetFiles(dir, Path.GetFileName(db) + BackupPrefix + "*")
                    .OrderByDescending(x => x, StringComparer.Ordinal)
                    .Skip(keep)
                    .ToList();

                foreach (var f in olds)
                {
                    File.Delete(f);
                    _logger.LogInformation("[LocalMeta] 轮转删除旧备份: {F}", Path.GetFileName(f));
                }
            }
            catch (Exception ex)
            {
                // 轮转失败不影响主流程，下次跑还会再试
                _logger.LogWarning(ex, "[LocalMeta] 备份轮转失败，不影响本次写库");
            }
        }

        private void WriteBack(string db, List<(string ItemId, string Path, int W, int H)> images,
            List<(string PersonId, string Text)> bios, PluginConfiguration cfg)
        {
            if (images.Count == 0 && bios.Count == 0)
            {
                return;
            }

            using var conn = new SqliteConnection("Data Source=" + db);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=30000";
            cmd.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var (itemId, path, w, h) in images)
                {
                    // 必须先删旧的再插。INSERT OR REPLACE 在这里是无效的：
                    // 主键 Id 每次都是新 Guid，永远撞不上，REPLACE 不会触发，
                    // 重复跑会让同一个人的 Primary 图积累成多条记录。
                    // 只补空模式走到这里说明原本没图，DELETE 是空操作，无害。
                    cmd.CommandText = "DELETE FROM BaseItemImageInfos WHERE ItemId=$item AND ImageType=0";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$item", itemId);
                    cmd.ExecuteNonQuery();

                    cmd.CommandText =
                        "INSERT OR REPLACE INTO BaseItemImageInfos " +
                        "(Id,ItemId,ImageType,DateModified,Height,Width,Path,Blurhash) " +
                        "VALUES ($i,$item,0,$d,$h,$w,$p,NULL)";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$i", Guid.NewGuid().ToString("N").ToUpperInvariant());
                    cmd.Parameters.AddWithValue("$item", itemId);
                    cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$h", h);
                    cmd.Parameters.AddWithValue("$w", w);
                    cmd.Parameters.AddWithValue("$p", path);
                    cmd.ExecuteNonQuery();
                }

                // 只补空模式：加 AND 条件兜底，防止扫描到写库之间被别的刮削器抢先写入。
                // 覆盖模式：无条件写。
                var bioSql = cfg.OverwriteExisting
                    ? "UPDATE BaseItems SET Overview=$t WHERE Id=$i"
                    : "UPDATE BaseItems SET Overview=$t WHERE Id=$i AND (Overview IS NULL OR Overview='')";

                foreach (var (personId, text) in bios)
                {
                    cmd.CommandText = bioSql;
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$t", text);
                    cmd.Parameters.AddWithValue("$i", personId);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogInformation("[LocalMeta] 写库完成：图 {Img} / 简介 {Bio}",
                    images.Count, bios.Count);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "[LocalMeta] 写库失败，已回滚");
            }
        }
    }
}
