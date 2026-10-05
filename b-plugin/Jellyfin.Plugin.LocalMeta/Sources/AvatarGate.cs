using System;
using System.IO;

namespace Jellyfin.Plugin.LocalMeta
{
    /// <summary>
    /// 头像准入：尺寸解析 + 最小尺寸门槛。
    ///
    /// 只解析文件头，不引入任何图像处理依赖 —— 插件目录必须只留托管程序集，
    /// 带原生库（如 SkiaSharp / ImageSharp 的 native 组件）会让整个插件被判 Malfunctioned。
    ///
    /// 存在的理由：Gfriends 导出里混着一批 125x125 的 DMM 缩略图，
    /// 不加门槛写进库就是把 MetaTube 刮到的高清头像换成小图。
    /// </summary>
    public static class AvatarGate
    {
        /// <summary>
        /// 读图片尺寸（JPEG / PNG / WebP）。读不出来返回 (0,0)。
        /// </summary>
        public static (int W, int H) ImageSize(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var head = new byte[64];
                var n = fs.Read(head, 0, head.Length);
                if (n < 24)
                {
                    return (0, 0);
                }

                // ---- JPEG: FF D8 起，找 SOF0~3 ----
                if (head[0] == 0xFF && head[1] == 0xD8)
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

                    return (0, 0);
                }

                // ---- PNG: 89 50 4E 47，IHDR 的宽高是大端 4 字节 ----
                if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                {
                    return (Be32(head, 16), Be32(head, 20));
                }

                // ---- WebP: RIFF....WEBP ----
                if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                    && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                {
                    // VP8X 扩展格式：画布宽高是 24 位小端，存的是减 1 后的值
                    if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'X')
                    {
                        return ((head[24] | head[25] << 8 | head[26] << 16) + 1,
                                (head[27] | head[28] << 8 | head[29] << 16) + 1);
                    }

                    // VP8L 无损：21 起 4 字节小端，低 14 位宽、次 14 位高，都减 1
                    if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'L')
                    {
                        var v = head[21] | head[22] << 8 | head[23] << 16 | head[24] << 24;
                        return ((v & 0x3FFF) + 1, ((v >> 14) & 0x3FFF) + 1);
                    }

                    // VP8 有损：同步码 9D 01 2A 之后两字节宽、两字节高（低 14 位有效）
                    if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == ' ')
                    {
                        return ((head[26] | head[27] << 8) & 0x3FFF,
                                (head[28] | head[29] << 8) & 0x3FFF);
                    }
                }
            }
            catch
            {
                // 读不到就按 0 处理，交给调用方决定
            }

            return (0, 0);
        }

        /// <summary>
        /// 源头像是否可用：文件非空，且短边不小于门槛。
        /// minSize &lt;= 0 表示不设门槛。
        /// </summary>
        public static bool IsUsable(string path, int minSize, out (int W, int H) size)
        {
            size = (0, 0);
            try
            {
                var fi = new FileInfo(path);

                // 0 字节文件必须挡掉：Gfriends 导出里 673 个 webp 全是 0 字节，
                // 7 个 jpg 也是，写进库就是一张坏图。
                if (!fi.Exists || fi.Length == 0)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            size = ImageSize(path);
            if (size.W <= 0 || size.H <= 0)
            {
                // 解析不出尺寸时不拦，避免误伤以后新增的格式
                return true;
            }

            // 按短边判定，不误杀 400x600 这类竖版高清图
            return minSize <= 0 || Math.Min(size.W, size.H) >= minSize;
        }

        private static int Be32(byte[] b, int o)
        {
            return b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
        }
    }
}
