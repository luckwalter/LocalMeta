using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalMeta.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMeta.Providers
{
    /// <summary>
    /// Person 头像兜底 provider。
    ///
    /// 10.11.6 实测接口（ILSpy 取证，非文档抄写）：
    ///   IRemoteImageProvider : IImageProvider
    ///     IImageProvider.Supports(BaseItem) : bool
    ///     IRemoteImageProvider.GetSupportedImages(BaseItem) : IEnumerable&lt;ImageType&gt;
    ///     IRemoteImageProvider.GetImages(BaseItem, CancellationToken) : Task&lt;IEnumerable&lt;RemoteImageInfo&gt;&gt;
    ///     IRemoteImageProvider.GetImageResponse(string, CancellationToken) : Task&lt;HttpResponseMessage&gt;
    ///   注意 GetSupportedImages 返回的是 ImageType（枚举），不是 BaseItemKind；
    ///   BaseItemKind 在 10.11 已搬到 Jellyfin.Data.Enums 命名空间。
    ///
    /// 本地文件直接当 RemoteImageInfo.Url 返回，GetImageResponse 再从磁盘读出，
    /// 全程不走外网。
    /// </summary>
    public class LocalMetaPersonImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<LocalMetaPersonImageProvider> _logger;
        private readonly List<ILocalMetaProfileSource> _sources;

        public LocalMetaPersonImageProvider(
            ILogger<LocalMetaPersonImageProvider> logger,
            IApplicationPaths appPaths)
        {
            _logger = logger;
            var cfg = Plugin.Instance?.Configuration;
            _sources = cfg == null
                ? new List<ILocalMetaProfileSource>()
                : LocalMetaSourceFactory.Build(cfg, appPaths.DataPath);
        }

        /// <inheritdoc />
        public string Name => "LocalMeta";

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Person;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        /// <inheritdoc />
        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            // 显式给 Task.FromResult 泛型实参，否则 Task<RemoteImageInfo[]> 推不出 IEnumerable 版
            return Task.FromResult<IEnumerable<RemoteImageInfo>>(GetImagesCore(item));
        }

        private IEnumerable<RemoteImageInfo> GetImagesCore(BaseItem item)
        {
            if (item is null)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.EnablePlugin || !cfg.ProvideImages)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var name = NameNormalizer.ApplySubstitutes(item.Name ?? string.Empty, cfg.NameSubstitutes);
            foreach (var src in _sources)
            {
                if (!src.TryGetAvatarPath(name, out var path))
                {
                    continue;
                }

                // 与计划任务同一套门槛：源图太小或 0 字节就不提供给 Jellyfin，
                // 否则刷新元数据时同样会把高清头像换成小图。
                if (!AvatarGate.IsUsable(path, cfg.MinAvatarWidth, out var sz))
                {
                    _logger.LogInformation(
                        "[LocalMeta] 头像被门槛跳过: {Name} {W}x{H} (门槛 {Min})",
                        name, sz.W, sz.H, cfg.MinAvatarWidth);
                    continue;
                }

                _logger.LogInformation("[LocalMeta] 提供头像: {Name} <- {Source}", name, src.Name);
                return new[]
                {
                    new RemoteImageInfo
                    {
                        Url = path,
                        ProviderName = src.Name,
                        Type = ImageType.Primary
                    }
                };
            }

            return Array.Empty<RemoteImageInfo>();
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(url, cancellationToken).ConfigureAwait(false);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg");
                return resp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LocalMeta] 读头像失败: {Url}", url);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
