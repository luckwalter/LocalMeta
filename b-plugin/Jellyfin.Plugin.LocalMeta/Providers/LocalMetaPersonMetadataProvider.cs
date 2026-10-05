using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMeta.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMeta.Providers
{
    /// <summary>
    /// Person 文字资料兜底 provider。
    ///
    /// 10.11.6 实测接口（从 MediaBrowser.Controller.dll 取证，不是照教程抄）：
    ///   ILocalMetadataProvider&lt;Person&gt; : IMetadataProvider&lt;Person&gt;, ILocalMetadataProvider
    ///     GetMetadata(ItemInfo, IDirectoryService, CancellationToken)
    ///         : Task&lt;MetadataResult&lt;Person&gt;&gt;
    ///   注意 10.x 起 GetMetadata 不再是 (result, ct) 形态，而是从 ItemInfo 出发、
    ///   返回一个新的 MetadataResult&lt;Person&gt;。用旧签名会直接编译不过。
    ///
    /// 兜底策略：只在 Overview 为空时补；Order 给大值排到 MetaTube 之后，
    /// 靠 Jellyfin 的 provider 顺序天然"它没刮到才兜"，不写任何互斥判断。
    /// </summary>
    public class LocalMetaPersonMetadataProvider : ILocalMetadataProvider<Person>, IHasOrder
    {
        /// <summary>
        /// 兜底顺序。Jellyfin 按 Order 升序调用，值越大越晚。
        /// MetaTube 等远程 provider 通常是 0/1，这里给 100 排到最后。
        /// </summary>
        public int Order => 100;

        private readonly ILogger<LocalMetaPersonMetadataProvider> _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly List<ILocalMetaProfileSource> _sources;

        public LocalMetaPersonMetadataProvider(
            ILogger<LocalMetaPersonMetadataProvider> logger,
            IApplicationPaths appPaths)
        {
            _logger = logger;
            _appPaths = appPaths;

            var cfg = Plugin.Instance?.Configuration;
            _sources = cfg == null
                ? new List<ILocalMetaProfileSource>()
                : LocalMetaSourceFactory.Build(cfg, appPaths.DataPath);
            _logger.LogInformation("[LocalMeta] 载入资料源 {Count} 个", _sources.Count);
        }

        /// <summary>
        /// provider 显示名，Jellyfin 面板里能看到。
        /// </summary>
        public string Name => "LocalMeta";

        /// <inheritdoc />
        public Task<MetadataResult<Person>> GetMetadata(
            ItemInfo itemInfo,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>();

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.EnablePlugin || !cfg.ProvideMetadata)
            {
                return Task.FromResult(result);
            }

            // 本 provider 只处理 Person；ItemInfo 不带实体，名字从路径名兜
            var rawName = itemInfo?.Path is null
                ? string.Empty
                : System.IO.Path.GetFileNameWithoutExtension(itemInfo.Path);

            if (string.IsNullOrWhiteSpace(rawName))
            {
                return Task.FromResult(result);
            }

            var person = new Person { Name = rawName };
            var name = NameNormalizer.ApplySubstitutes(rawName, cfg.NameSubstitutes);

            foreach (var src in _sources)
            {
                if (!src.TryGetProfile(name, out var profile))
                {
                    continue;
                }

                person.Overview = RenderOverview(profile, cfg);
                result.Item = person;
                result.HasMetadata = true;
                _logger.LogInformation("[LocalMeta] 补简介: {Name} <- {Source}", name, src.Name);
                break;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// 把结构化资料渲染成文本。模板可配，占位符 {bust} {waist} {hips} {cup} {height} {debut}。
        /// 与 A 阶段 localmeta.py 的默认模板保持一致。
        /// </summary>
        internal static string RenderOverview(PersonProfile p, PluginConfiguration cfg)
        {
            var tpl = string.IsNullOrWhiteSpace(cfg.OverviewTemplate)
                ? "3サイズ: B:{bust} / W:{waist} / H:{hips} <br> カップサイズ: {cup} <br> 身長: {height}cm <br> デビュー: {debut}"
                : cfg.OverviewTemplate;

            return tpl
                .Replace("{bust}", p.Bust.ToString(CultureInfo.InvariantCulture))
                .Replace("{waist}", p.Waist.ToString(CultureInfo.InvariantCulture))
                .Replace("{hips}", p.Hips.ToString(CultureInfo.InvariantCulture))
                .Replace("{cup}", string.IsNullOrEmpty(p.Cup) ? "?" : p.Cup)
                .Replace("{height}", p.Height.ToString(CultureInfo.InvariantCulture))
                .Replace("{debut}", string.IsNullOrEmpty(p.Debut) ? "?" : p.Debut)
                .Replace("<br>", Environment.NewLine);
        }
    }
}
