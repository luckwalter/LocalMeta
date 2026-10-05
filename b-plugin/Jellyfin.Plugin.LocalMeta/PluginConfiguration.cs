using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalMeta
{
    /// <summary>
    /// 配置模型。所有默认值都对齐 A 阶段 localmeta.py 的 config.json，
    /// 两边改一处要同步另一处，避免两个实现行为分叉。
    /// 新字段只增不删，旧配置升级后仍可加载。
    ///
    /// 基类说明（10.11.6 实测签名取证）：
    /// 10.11 已移除旧的 IPluginConfiguration 接口，配置基类为
    /// MediaBrowser.Model.Plugins.BasePluginConfiguration（空类，只为标识类型）。
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// 总开关。关掉后 provider 与计划任务都不动数据。
        /// </summary>
        public bool EnablePlugin { get; set; } = true;

        /// <summary>
        /// 目标媒体库名，多个用逗号分隔。留空表示不限库（所有 Person 都兜）。
        /// </summary>
        public string LibraryNames { get; set; } = "Japan Pron Movie";

        /// <summary>
        /// 是否覆盖已有字段。false（默认）= 只补空，已有头像/简介的一律跳过；
        /// true = 源里有的都覆盖一次。
        ///
        /// 注意：开启后会把远程刮削器（MetaTube 等）刮到的内容一起冲掉，
        /// 只在确认要重刷时临时打开，刷完记得关回去。
        /// 对齐 A 阶段 config.json 的 overwriteExisting。
        /// </summary>
        public bool OverwriteExisting { get; set; }

        /// <summary>
        /// 写库前最多保留多少份 jellyfin.db 备份，按文件名（内含时间戳）轮转删除最旧的。
        ///
        /// 实测每份约 47MB，不做轮转的话每天一份一年就是 17GB。
        /// 对齐 A 阶段 config.json 的 backupMax=10。
        /// </summary>
        public int BackupMax { get; set; } = 10;

        /// <summary>
        /// 单次计划任务最多处理多少人，防止大库一次跑太久。
        /// </summary>
        public int MaxItemsPerRun { get; set; } = 100;

        /// <summary>
        /// 备份 jellyfin.db（直写库时建议开）。
        /// </summary>
        public bool BackupBeforeWrite { get; set; } = true;

        /// <summary>
        /// 简介文本模板，支持占位符：{bust} {waist} {hips} {cup} {height} {debut}。
        /// 留空则用内置默认模板。改这里就能自定义输出格式。
        /// </summary>
        public string OverviewTemplate { get; set; } = string.Empty;

        /// <summary>
        /// 罩杯字母表（cup 数字是索引）。
        /// 实测校验：cup=6→G、7→H、8→I、9→J（用 bust-waist 差值反推），即 字母 = chr(65 + cup)。
        /// </summary>
        public string CupLetters { get; set; } = "ABCDEFGHIJKLMNOPQ";

        /// <summary>
        /// 合法身高区间，越界跳过（防脏数据）。
        /// </summary>
        public int HeightMin { get; set; } = 130;

        /// <summary>
        /// 合法身高上限。
        /// </summary>
        public int HeightMax { get; set; } = 200;

        /// <summary>
        /// 合法围度区间（胸/腰/臀）。
        /// </summary>
        public int GirthMin { get; set; } = 50;

        /// <summary>
        /// 合法围度上限。
        /// </summary>
        public int GirthMax { get; set; } = 130;

        /// <summary>
        /// 资料源（JavBoss sqlite）路径。留空则自动在插件目录与 Jellyfin 程序目录找 javboss.db。
        /// </summary>
        public string ProfileDbPath { get; set; } = string.Empty;

        /// <summary>
        /// 头像源（Gfriends 导出目录）路径，其下应有 avatars/ 子目录。
        /// </summary>
        public string AvatarSourceDir { get; set; } = string.Empty;

        /// <summary>
        /// 姓名替换规则，"原名|替换名 " 用竖线分隔；留空表示不替换。
        /// 用于解决 Jellyfin 里带括号别名导致对不上源的情况。
        /// </summary>
        public string NameSubstitutes { get; set; } = string.Empty;

        /// <summary>
        /// 是否参与图片提供（关闭后只补文字资料）。
        /// </summary>
        public bool ProvideImages { get; set; } = true;

        /// <summary>
        /// 是否参与文字资料提供（关闭后只补头像）。
        /// </summary>
        public bool ProvideMetadata { get; set; } = true;

        /// <summary>
        /// 兼容旧配置读取。
        /// 注意：Jellyfin 插件配置走 MediaBrowser.Model.Serialization.IXmlSerializer（XML），
        /// 不是 System.Text.Json，所以这里必须用 XmlIgnore 才对，
        /// 10.11.6 实测 BasePlugin&lt;T&gt; 构造函数收的是 IXmlSerializer。
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int LegacySchemaVersion { get; set; } = 2;
    }

    /// <summary>
    /// 名字替换规则项。
    /// </summary>
    public class NameSubstituteEntry
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;
    }
}
