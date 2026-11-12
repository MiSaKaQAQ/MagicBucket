using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Reflection;
using System.IO;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace SrankFlag;

/// <summary>
/// S 怪窗口的「开图插旗 + 自动前往 + 自动丢弃 + 查小异亚」后端。
///
/// 浏览器里的窗口碰不到游戏，所以这里开一个只监听回环的小接口：
///   GET  /ping                        健康检查（页面用它判断插件在不在）
///   GET  /maps?q=关键词                搜地图名，排查译名对不上
///   GET  /flag?map=..&x=..&y=..        浏览器地址栏可以直接试
///   POST /flag  {"map","x","y"}        只开图 + 打旗
///   POST /goto  {"map","x","y","world","line"}   开图 + 跨服 + 传最近水晶 + 切分线
///   POST /telepo {"map","x","y"}       传最近的大水晶（不开图）
///   POST /worldtravel {"world"}         跨服传送
///   POST /insc {"line"}                切分线
///   POST /discard {}                   自动丢弃当前地图的触发条件道具
///   GET  /weeEa                        查当前地图的小异亚（返回附近玩家列表）
///
/// 干活的是 IGameGui.OpenMapWithMapLink + 直接发 /pdr 系命令 + InventoryManager + ObjectTable。
/// </summary>
public sealed partial class Plugin : IDalamudPlugin
{
    private const string CommandName = "/srankflag";
    private const string CommandAlias = "/水桶";     // 中文别名（同 /srankflag）
    private const int Port = 18099;

    /* 触发条件 = 丢弃道具 的地图（territoryId → (物品Id, 单次整组丢弃量)）。
       只有这三张图需要：雷克兰德 ×1 / 厄尔庇斯(鸟蛋) ×5 / 亚克特尔树海 ×50。
       丢法：从大堆拆出「正好这个数量」的整组，一次整组丢出去，不碰剩下的。 */
    private static readonly Dictionary<uint, (uint ItemId, int Amount)> TriggerDiscardItems = new()
    {
        { 813, (27850u, 1) },    // 雷克兰德
        { 961, (36256u, 5) },    // 厄尔庇斯（鸟蛋）
        { 1189, (7767u, 50) },   // 亚克特尔树海
    };

    /* 这些 S 的触发与踩点无关：展开明细里整块省略「绿点 / 触发点」相关 UI */
    private static readonly HashSet<string> NoPointHunts = new(StringComparer.Ordinal)
    {
        "俄菲翁尼厄斯", "顾尼图", "先驱勇士 阿提卡斯", "戾虫", "内尤佐缇",
        "得到宽恕的炫学", "山谢亚", "沉思之物", "伊休妲", "颇胝迦",
        "盐和光",   // 触发是「50 次丢弃动作」，全图任意位置，点位没有意义（这条按用户要求先不做自动准备）
    };

    /* 只有这些 S 的触发要走到固定点位（NPC 对话 / 踩点 / 交道具），才值得「前往绿点」。
       其余 S 的触发是打怪或全图任意位置，传到大水晶等刷新就够——跑到绿点纯属多跑冤枉路。
       名字要和 srank_data.json 的 cn 完全一致（「厌忌之人 奇里格」是一只怪，中间有空格）。 */
    private static readonly HashSet<string> PointHunts = new(StringComparer.Ordinal)
    {
        "咕尔呱洛斯", "南迪", "夺心魔", "布隆特斯", "凯撒贝希摩斯", "伽马",
        "兀鲁忽乃朝鲁", "多智兽", "阿格拉俄珀", "布弗鲁", "狭缝",
        "阿姆斯特朗", "厌忌之人 奇里格", "伊努索奇", "天气预报机器人",
    };

    /* 有些 S 的触发点离这张图「默认的第一座大水晶」很远，指定只许传某一座。
       值写「能唯一定位的水晶名片段」——运行时在 Aetheryte 表里按包含匹配，
       比手写全名稳（客户端译名可能和这里差一两个字）。 */
    private static readonly Dictionary<string, string> HuntAetheryte = new(StringComparer.Ordinal)
    {
        ["山谢亚"] = "谢申|青磷",   // 夏劳尼荒野：只传 谢申内青磷泉 大水晶
        ["得到宽恕的炫学"] = "图姆拉",   // 珂露西亚岛：传 图姆拉村 更近
    };

    /* 只有这几张图的触发条件是「丢道具」，其余地图的「自动前往」不应带丢弃 */
    private static readonly Dictionary<string, uint> DiscardMaps = new(StringComparer.Ordinal)
    {
        ["雷克兰德"] = 813,
        ["厄尔庇斯"] = 961,
        ["亚克特尔树海"] = 1189,
    };

    /* 按地图自动召唤的随从（territoryId → minionId），数据来自 RankSSpawnHelper。
       960 是小异亚所在的活着的记忆外围…等等，960 是“黑风海上层”？不重要，照抄 RSSH 的映射。 */
    private static readonly Dictionary<uint, uint> MinionMap = new()
    {
        { 1188, 180 },
        { 960, 423 },
        { 816, 303 },
        { 956, 434 },
        { 614, 215 },
        { 397, 148 },
    };

    /* 游戏内窗口要给“自动寻路”的 6 只 S 怪（用户点名）：点位来自熊站 tracker-points 快照（地图坐标）。 */
    private sealed record NavHunt(string Cn, string Map, (float x, float y)[] Points);

    private static readonly NavHunt[] NavHunts =
    {
        new("阿格拉俄珀", "伊尔美格", new[]
        {
            (27.15f, 18.97f), (31.52f, 13.71f), (19.72f, 34.94f), (7.53f, 22.91f), (9.75f, 30.75f),
            (22.93f, 28.69f), (8.09f, 27.10f), (24.91f, 37.23f), (11.16f, 15.91f), (24.54f, 32.94f),
            (13.74f, 34.11f), (20.09f, 8.62f), (25.59f, 7.06f), (19.31f, 27.32f), (34.10f, 7.28f),
            (10.56f, 20.28f), (29.35f, 5.41f),
        }),
        new("伊努索奇", "克扎玛乌卡湿地", new[]
        {
            (9.50f, 7.87f), (20.21f, 28.37f), (37.00f, 20.01f), (5.32f, 28.86f), (23.97f, 36.71f),
            (16.30f, 17.27f), (6.44f, 12.05f), (15.83f, 23.67f), (36.71f, 17.51f), (34.42f, 36.52f),
            (30.00f, 15.75f),
        }),
        new("阿姆斯特朗", "加雷马", new[]
        {
            (27.59f, 33.96f), (29.06f, 20.89f), (15.79f, 19.65f), (11.78f, 17.27f), (12.13f, 12.84f),
            (33.01f, 21.85f), (23.43f, 25.76f), (9.79f, 11.67f), (32.45f, 32.55f), (26.92f, 8.08f),
        }),
        new("天气预报机器人", "活着的记忆", new[]
        {
            (12.47f, 37.84f), (5.17f, 12.88f), (12.95f, 27.82f), (18.91f, 20.21f), (4.26f, 28.96f),
            (26.94f, 31.32f), (37.19f, 18.70f), (38.99f, 30.05f), (32.91f, 20.94f), (4.31f, 33.28f),
        }),
        new("狭缝", "天外天垓", new[]
        {
            (14.92f, 36.14f), (16.29f, 26.09f), (19.31f, 9.82f), (17.61f, 30.31f), (21.19f, 34.15f),
            (8.23f, 20.42f), (13.28f, 10.44f), (11.93f, 22.02f), (27.97f, 12.43f), (15.45f, 32.60f),
        }),
        new("厌忌之人 奇里格", "奥阔帕恰山", new[]
        {
            (19.04f, 14.30f), (28.82f, 9.46f), (11.50f, 9.12f), (21.93f, 20.75f), (7.51f, 25.61f),
            (28.14f, 22.88f), (15.73f, 24.29f), (34.06f, 19.21f), (21.92f, 16.86f), (20.31f, 23.70f),
        }),
    };

    /* ───── 熊站快照数据（srank_data.json，与网页窗口同源；放在插件 DLL 同目录） ───── */
    private sealed class SnapshotData
    {
        public string dc { get; set; } = "";
        public double snapshot { get; set; }
        public string[] worlds { get; set; } = [];
        public HuntData[] hunts { get; set; } = [];
    }

    private sealed class HuntData
    {
        public string en { get; set; } = "";
        public string cn { get; set; } = "";
        public string map { get; set; } = "";
        public string patch { get; set; } = "";
        public double cd { get; set; }     // 最短冷却（小时），来自站点 rt[0]
        public double win { get; set; }    // 冷却浮动窗口（小时），来自站点 rt[1]；0 时退回全局默认
        public double[] mt { get; set; } = new double[0]; // 维护后再生（小时），[0]=再生 [1]=窗口；来自 trigger_cd.json 的 mt，全局重启时间 T 主导时启用
        public bool counted { get; set; }  // 触发要累计计数（农怪 / 钓 / 丢弃 / FATE 次数）
        public bool prob { get; set; }     // 触发含额外概率判定
        public string kind { get; set; } = "";  // 触发方式（钓鱼 / 青魔自爆 / 农怪计数 …，认不出为空）
        public string[] tips { get; set; } = [];   // 触发要点（整理自灰机 Wiki 攻略）
        public double lastDeath { get; set; }
        public WorldDeath[] worlds { get; set; } = [];
    }

    /// <summary>v1.2.7：站点 mt 表绝大多数等于 0.6×rt，只有这两只例外（Minhocao 窗口 4.8h、
    /// Zona Seeker 窗口 3.6h）。老用户插件目录里的 srank_data.json 是用户状态文件，部署时不能覆盖，
    /// 所以运行时按 en 补齐 mt；新装的包里已带完整 mt 字段，这里会直接跳过。</summary>
    private static readonly Dictionary<string, double[]> MaintPairOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Minhocao"] = new[] { 34.2, 4.8 },       // rt = [57, 6]
        ["Zona Seeker"] = new[] { 34.2, 3.6 },    // rt = [57, 8]
    };

    private sealed class WorldDeath
    {
        public string world { get; set; } = "";
        public double lastDeath { get; set; }
    }

    private sealed class PointSet
    {
        public int[] ids { get; set; } = [];
        public float[][] pts { get; set; } = [];
        public int[] bm { get; set; } = [];
        public Dictionary<string, PointFlags> w { get; set; } = new();
    }

    private sealed class PointFlags
    {
        public int[] r { get; set; } = [];
        public int[] y { get; set; } = [];
        public int[] a { get; set; } = [];
    }

    private sealed class DataFile
    {
        public SnapshotData? data { get; set; }
        /// <summary>四大区全量（豆豆柴 / 莫古力 / 猫小胖 / 陆行鸟）：每项各自带 worlds 与 hunts。</summary>
        public List<SnapshotData> dcs { get; set; } = new();
        public Dictionary<string, PointSet> points { get; set; } = new();
    }

    private sealed record Pt(int Id, float X, float Y, char S);

    /// <summary>一个大区里用户调好的筛选（服 / 版本 / 只看开闸 / 搜索词）。
    /// 切大区、重载插件、重启游戏之后都按这份恢复，别把人家挑好的条件弄丢。</summary>
    private sealed class DcFilter
    {
        /// <summary>选中的服。worldsSet 用来区分「没存过」和「一个都不选」。</summary>
        public List<string> worlds { get; set; } = new();
        public bool worldsSet { get; set; }
        public List<string> patches { get; set; } = new();
        public bool onlyOpen { get; set; } = true;
        public string search { get; set; } = "";
    }

    /// <summary>插件目录下的 prefs.json：上次看的大区 + 每个大区各自的筛选。</summary>
    private sealed class UiPrefs
    {
        public string dc { get; set; } = "";
        public Dictionary<string, DcFilter> byDc { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ITargetManager targets;   // 直接设当前目标（比 /target 聊天命令稳）
    private readonly IGameInventory gameInventory;   // 背包变更事件：数采集「次数」用
    private readonly IDalamudPluginInterface pi;   // Artisan IPC 用
    private readonly IUiBuilder uiBuilder;
    private NativeHudRuntime? nativeHud;          // v1.1.71/72/74：原生「触发中」+「自动前往」+「主窗」（失败各自回退 ImGui）
    /// <summary>v1.2.12：S 怪历史记录（双源：主站 HTTP / Sonar 聊天栏）。独立 srank_history.json，
    /// 绝不触碰 5 个用户状态文件。</summary>
    private SrankHistory? history;
    private bool historyWindowOpen;                // 原生历史窗口开关（第 5 个窗，纳入过图快照）
    private HistoryFilter historyFilter = HistoryFilter.All;
    private int historyPage;
    private bool legacyMainOpen;                  // v1.1.74：临时切回原 ImGui 完整版主窗
    private bool settingsOpen;                    // v1.1.98：齿轮打开 ImGui「高级设置」（默认 false）
    private string mainExpandedKey = "";          // v1.1.77：原生主窗当前展开的「服 × 怪」行 key

    private readonly CancellationTokenSource cts = new();
    private TcpListener? listener;

    // 自动化开关 + 窗口状态
    private bool autoSummonMinion;
    private bool autoLeaveDuty;

    /// <summary>倒地后自动点「陷入无法战斗状态 · 要传送到返回点吗？」的「确定」。
    /// 触发成功（尤其顾尼图被口锥打倒）后玩家是死的，不点就会一直躺着。</summary>
    private bool autoConfirmReturn = true;
    private DateTime lastReturnClick;
    private bool returnClickLogged;
    private bool mainWindowOpen = true;
    /// <summary>v1.2.7：原生 KTK 设置面板开关（主窗齿轮切换）。</summary>
    private bool settingsWindowOpen;
    // ───────── v1.2.9：过图窗口快照（传送前记录 / 落地后延迟恢复，绝不强锁） ─────────
    private bool teleportSnapshotPending;
    private bool teleportSnapshotMain, teleportSnapshotHud, teleportSnapshotAuto,
                 teleportSnapshotSettings, teleportSnapshotHistory;   // v1.2.12：第 5 窗
    private DateTime teleportSnapshotAtUtc = DateTime.MinValue;
    private DateTime restoreAfterTeleportUtc = DateTime.MinValue;
    private bool wasBetweenAreas;
    // v1.2.11：稳定态窗口寄存器。每帧在「非过图 + 已落地」时记录用户当前可见的窗口开关，
    // 作为传送快照的权威来源。游戏过图 hide addon 会把 bool 写成 false，直接拍快照会被污染。
    // v1.2.12：历史窗口一并纳入，保持「原来开着几个窗就保持几个」。
    private bool stableWindowMain, stableWindowHud, stableWindowAuto,
                 stableWindowSettings, stableWindowHistory;
    private bool stableWindowValid;
    /// <summary>「停止」令牌：每次点停止 +1，之前排进队列的延时任务全部作废。</summary>
    private int runGen;
    /// <summary>硬停止开关：停止后连「已经排到框架线程上、还没来得及发」的命令也一并拦掉。
    /// runGen 只能作废「还没触发的延时任务」，但 Say() 是先排进框架线程队列再执行的，
    /// 点停止那一刻队列里可能还躺着自爆、下坐骑、joinexitduty 这些命令，它们会在停止之后照发——
    /// 于是看起来像「点了停止还在自己动」。halted 就是补这个缺口，下一次发起动作时自动解除。</summary>
    private bool halted;
    private float gounituHpMark;            // 顾尼图触发：记下「最大化」出现时的血量，用来判定之后是否受口锥
    private readonly List<uint> unlockedMinions = new();
    private DateTime lastSummonCheck = DateTime.MinValue;
    private DateTime lastLeaveTry = DateTime.MinValue;

    // S 怪列表窗口状态
    private readonly string pluginDirectory;
    private string dataError = "";
    private string currentDc = "";                    // 当前查看的大区（多大大区数据里选一个）
    private DataFile? srankData;
    private readonly HashSet<string> expandedHunts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> huntWorld = new(StringComparer.Ordinal); // en → 触发点面板选中的服
    private readonly HashSet<string> worldFilter = new(StringComparer.Ordinal);          // 当前参与统计的服务器（空 = 未初始化）
    private readonly HashSet<string> tilesExpanded = new(StringComparer.Ordinal);        // 怪|服 → 绿点瓦片已展开（否则只铺一行 + 「+N」）
    private readonly HashSet<string> tipsOpen = new(StringComparer.Ordinal);            // 怪 → 明细里的「触发要点」已展开
    private bool lightMode;          // true = 亮色主题
    private bool themeLoaded;        // 主题只从磁盘读一次

    /* ───── 最小化小方块 ───── */
    private bool miniMode;                      // true = 主窗收起，只留一个方形小窗
    private Vector2? miniPos;                   // 小窗位置（自己管，拖动即更新）
    private bool miniDragging;                  // 本次按下有拖动过，松开时不要当成点击
    private Dalamud.Interface.Textures.ISharedImmediateTexture? miniIcon;
    private bool miniIconTried;
    private bool searchInHeader;                                                        // 搜索行并进了标题行（窗口够宽时）
    private List<string> lastListed = new();                                            // 上一帧列表里的怪（顶栏「展开全部」用）
    private bool onlyOpen = false;   // 默认显示全部 S（含已过窗），玩家可点「只看开闸」折叠；避免非豆豆柴大区未刷新时列表空白
    private string searchText = "";
    private readonly byte[] searchBuf = new byte[64];
    private readonly HashSet<string> patchSel = new(StringComparer.Ordinal); // 版本多选（空 = 全部）
    private UiPrefs prefs = new();          // 筛选条件持久化（prefs.json）
    private bool prefsLoaded;               // 只在启动时读一次
    private string lastFilterSig = "";      // 上一帧的筛选指纹，变化时才落盘
    private DateTime filterDirtyAt = DateTime.MinValue;
    private string drawErrorLogged = ""; // 上次已记录的绘制异常指纹（防刷屏）

    /* ───── 微软雅黑字体 ─────
       默认字体图集是给 16px 上下用的，直接 AddText 放大到 21/42px 必然是插值糊图。
       这里自建一个独立图集，把雅黑按 14 / 22 / 44px 三档栅格好，绘制时按字号挑最接近的一档
       （只用「缩小」不用「放大」），中文字形才锐利。
       字形范围必须精确给：留空会把雅黑所有 UCS-2 字形塞进图集，41px 档直接爆内存。 */
    private const string FontGlyphs =
        "·×—…⌃⌄▶▸▾●⟳　、。「」不丢个中为之乌了亚产人从令件伊休传佐作俄修先克入全兰关具"
        + "兽内再写冷凯出击分切到制刷前副加务动助努劳勇包匹占卡即却厄厌原发取口只召可右号叹同名后启"
        + "员命唤器回因图在地坐垓塔士夏外多够大天太失奇奈奥妲姆始子字存学安家宽寻小尔尚尤就尼展山岛"
        + "岸川左已布帕常庇开异弃弗张当录影往得德忆忌快态思怪恕息恰戏成或戾所手扎打找把报拆拉拖持按"
        + "换据排接控提插搜支收数整文斯新旗无旧时明显晓晶智暂暗曦最月有服朗未本机杀条来析林查柴标树"
        + "格检森次正步每气水沉没法泪津活海清游湖湿满炫点焰照熊物特状独狭玛玩珀珂理用的监目相看着知"
        + "示穆空窗立站端等管系索红线组细终经绘统绪维绿缇缓编缝缺网置美翁背胝能自艾茶荒菲萨落藏虫行"
        + "被西要解触记试询该请读调谢豆败起足跨路跳踩身载辉过近还这连迦迷退送逆途道遗部配里重野量金"
        + "钟银锁键闭间阔阳阿附除随隐雷需露页顾预颇风飞马驱鲁黑（），：？一与且东云剩区央实微推日星晴暴灵现碧秒算纳续翻致薄衣计诺进采阴集雨雪雾上下专业串主乃亮伐会伽体余你倒做兀光共准初利剧功化南去反叠向含呱和咕围备复夺套孔完宠属巡巨师希并建弱弹形心志忽恶情意战执技折指摩撒改放效斗更朝朱束构楚概段洛流测深烹爆版牛犎狂狩猎率生申画留白盐直确磷科程符第筛精级结继职色芙英范蓝蝶装认讨贝走超软返迪选逐造都铺闪闸陆限隆雀雅青面题骑魔鸟默但绑设跑我停也候别国它帝幻搭是混穿判好定归零差话任工延止消何儿己断离给轮块模住几稳刻咏唱头神否掉园艺《》『』【】・三仍伤使冰受害对射尝平待挑攻斧猛突约蛭衅诱距边钓锥！．；～位域基潜规固补交代单吃合品方菜表"
        + "世侧凑列勾双变响圈境少层底式强悬感户择旦期栏样框桶永浮潮片界盯种稍紧聊至苍莲让错队顶龙";

    private IFontAtlas? fontAtlas;
    private IFontHandle? fontHandleS, fontHandleM, fontHandleL;
    private ILockedImFont? fontLockS, fontLockM, fontLockL;
    private ImFontPtr fontPtrS, fontPtrM, fontPtrL;
    private bool fontReady;       // 本帧三档字体是否都拿到了
    private bool fontInitTried;   // 只在第一次绘制时尝试建图集
    private bool fontProbeLogged; // 首次判定字体是否真的可用，只记一次日志

    // 窗口尺寸：右下角手柄拖拽缩放（ImGui 边缘缩放之外的自绘兜底）
    private bool winResizing;
    private Vector2 winResizeBase;
    private Vector2 winResizeStart;
    private Vector2? winPendingSize;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        ICommandManager commandManager,
        IDataManager dataManager,
        IFramework framework,
        IPluginLog log,
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        ITargetManager targets,
        IGameInventory gameInventory)
    {
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.targets = targets;
        this.gameInventory = gameInventory;
        this.pi = pluginInterface;
        this.uiBuilder = pluginInterface.UiBuilder;
        try { this.pluginDirectory = pluginInterface.AssemblyLocation.DirectoryName ?? "."; }
        catch { this.pluginDirectory = "."; }

        var cmd = new CommandInfo(this.OnCommand)
        {
            HelpMessage = "魔法水桶：/水桶 打开游戏内窗口 · /水桶 history 打开 S 怪历史 · " +
                          "/水桶 sonar [test|probe|clear] 实时击杀自检 · " +
                          "/水桶 trigger 查看已被他人触发的 S · /水桶 trigger main on|off 主站源开关 · " +
                          "/水桶 trigger stuck on|off 闸后未死判定 · /水桶 trigger scan 演练判定 · " +
                          "/水桶 trigger clear 清空黑名单 · " +
                          "/水桶 nav <地图> <X> <Y>（开图插旗+vnav寻路） · " +
                          "/水桶 goto <地图> <X> <Y> [服务器] [分线] · /水桶 discard · /水桶 weea · " +
                          "/水桶 stop（取消全部自动流程） · " +
                          "/水桶 summon [手动召唤随从] · /水桶 summonauto on|off · /水桶 leaveauto on|off",
            ShowInHelp = true,
        };
        this.commandManager.AddHandler(CommandName, cmd);
        try { this.commandManager.AddHandler(CommandAlias, cmd); } catch { /* 别名注册失败不影响主命令 */ }

        // v1.2.12：历史库必须在任何数据源写入之前就绪（Sonar 事件 / 主站拉取都往这里推）。
        try
        {
            this.history = new SrankHistory(this.pluginDirectory,
                m => this.log.Info("[魔法水桶] " + m));
        }
        catch (Exception ex)
        {
            this.history = null;
            try { this.log.Warning("[魔法水桶] 历史库初始化失败，历史功能停用：" + ex.Message); } catch { }
        }

        this.LoadAutoSettings();   // v1.2.7：设置面板的三个自动化开关（新增独立文件，不动旧的 5 个用户状态文件）
        this.RefreshUnlockedMinions();
        this.LoadSrankData();
        this.lastBearFetch = DateTime.UtcNow - BearInterval; // 启动后第一次 Update 就拉
        this.framework.Update += this.Framework_OnUpdate;
        this.clientState.Login += this.ClientState_OnLogin;
        this.uiBuilder.Draw += this.DrawMainWindow;
        try { this.chatGui.ChatMessage += this.OnChatMessage; } catch { /* 聊天钩子失败不影响主功能 */ }
        // v1.2.12：Sonar 击杀播报独立监听（只认带 "was just killed" 的击杀，出生播报一律丢弃）
        try { this.chatGui.ChatMessage += this.OnSonarKillChatMessage; } catch { /* 不影响主功能 */ }

        this.StartServer();
        this.InitNativeHud();
    }

    public void Dispose()
    {
        this.teleportSnapshotPending = false;
        try { this.SavePrefs(); } catch { /* 卸载时也别把筛选丢了 */ }
        this.commandManager.RemoveHandler(CommandName);
        this.commandManager.RemoveHandler(CommandAlias);
        this.framework.Update -= this.Framework_OnUpdate;
        this.clientState.Login -= this.ClientState_OnLogin;
        this.uiBuilder.Draw -= this.DrawMainWindow;
        try { this.nativeHud?.Shutdown(); } catch { /* 原生 HUD 卸载失败不影响插件退出 */ }
        try { this.chatGui.ChatMessage -= this.OnChatMessage; } catch { }
        try { this.chatGui.ChatMessage -= this.OnSonarKillChatMessage; } catch { }
        this.cts.Cancel();
        try { this.listener?.Stop(); } catch { /* ignore */ }
        this.listener = null;
        this.cts.Dispose();
    }

    // ─────────────────────────── 斜杠命令 ───────────────────────────

    private void OnCommand(string command, string args)
    {
        var parts = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            // v1.2.9：/水桶 与 ImGui 彻底解耦。只切 mainWindowOpen 一个开关，
            // 不读写 miniMode / legacyMainOpen / settingsOpen 这些旧 ImGui 残留状态，
            // 不调用任何 ImGui API，也不依赖 Draw 帧或 Tick 的 200ms 节流。
            // 切完立即直接驱动原生 KTK 窗口（Close→Open 绕过 IsOpen 缓存），敲一下必定当帧生效。
            // 用户主动敲命令优先于传送快照，避免落地恢复把刚关掉的窗口又打开。
            this.mainWindowOpen = !this.mainWindowOpen;
            this.ApplyMainWindowNow();
            this.NoteUserWindowChange();   // v1.2.11：用户意图同步进快照，落地按最新状态恢复
            this.Print(this.mainWindowOpen ? "主窗已打开" : "主窗已关闭（再敲 /水桶 可开回来）");
            return;
        }
        var sub = parts[0].ToLowerInvariant();
        switch (sub)
        {
            case "history":           // v1.2.12：/水桶 history，打开 / 关闭原生历史窗口
                this.historyWindowOpen = !this.historyWindowOpen;
                this.ApplyHistoryWindowNow();
                this.NoteUserWindowChange();
                this.Print(this.historyWindowOpen ? "S 怪历史窗口已打开" : "S 怪历史窗口已关闭");
                return;
            case "sonar":             // v1.2.12：/水桶 sonar [test|probe|clear]，概率核对 + 链路自检
                this.SonarCommand(parts.Length > 1 ? parts[1].ToLowerInvariant() : "");
                return;
            case "trigger":           // v1.2.12：/水桶 trigger [clear]，查看 / 清空「已被他人触发」黑名单
                this.TriggerCommand(parts);
                return;
            case "auto":
                this.autoRunOpen = !this.autoRunOpen;
                this.nativeHud?.SetAutoWindowOpen(this.autoRunOpen);
                this.Print(this.autoRunOpen ? "自动前往悬浮窗已打开（勾选服务器和 S 后点开始）" : "自动前往悬浮窗已关闭");
                return;
            case "hud":
                this.autoHudOpen = !this.autoHudOpen;
                this.nativeHud?.SetHudWindowOpen(this.autoHudOpen);
                this.Print(this.autoHudOpen ? "触发悬浮窗已打开" : "触发悬浮窗已关闭（主窗按钮或 /水桶 hud 可再打开）");
                return;
            case "flag":
                if (parts.Length < 4) { this.Print("flag 需要：地图 X Y"); return; }
                if (!float.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fx) ||
                    !float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fy))
                { this.Print("坐标要写成数字"); return; }
                this.Print(this.Flag(string.Join(' ', parts[1..^2]), fx, fy).Message);
                return;
            case "goto":
            {
                // 从右往左找最后两个数字当作 Y、X；中间是地图名；Y 之后的是 [服务器] [分线]
                int yi = -1, xi = -1;
                for (var k = parts.Length - 1; k >= 1; k--)
                {
                    if (!float.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                    if (yi < 0) yi = k;
                    else { xi = k; break; }
                }
                if (xi < 0 || yi < 0) { this.Print("goto 需要：地图 X Y [服务器] [分线]"); return; }
                var gx = float.Parse(parts[xi], NumberStyles.Float, CultureInfo.InvariantCulture);
                var gy = float.Parse(parts[yi], NumberStyles.Float, CultureInfo.InvariantCulture);
                var gmap = string.Join(' ', parts[1..xi]);
                var rest = parts.Skip(yi + 1).ToArray();
                var gworld = rest.Length > 0 ? rest[0] : "";
                var gline = rest.Length > 1 && int.TryParse(rest[1], out var gl) ? gl : 0;
                this.Print(this.Goto(gmap, gx, gy, gworld, gline).Message);
                return;
            }
            case "telepo":
                if (parts.Length < 4) { this.Print("telepo 需要：地图 X Y"); return; }
                if (!float.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tx) ||
                    !float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ty))
                { this.Print("坐标要写成数字"); return; }
                this.Print(this.TeleportNearest(string.Join(' ', parts[1..^2]), tx, ty).Message);
                return;
            case "worldtravel":
                if (parts.Length < 2) { this.Print("worldtravel 需要：服务器名"); return; }
                this.Print(this.WorldTravel(string.Join(' ', parts[1..])).Message);
                return;
            case "insc":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var il)) { this.Print("insc 需要：分线编号"); return; }
                this.Print(this.ChangeInstance(il).Message);
                return;
            case "discard":
                this.Print(this.DiscardTrigger().Message);
                return;
            case "weea":
                var r = this.QueryWeeEa();
                this.Print(r.Ok ? $"小异亚附近 {r.Names.Count} 人：" + string.Join("、", r.Names)
                                : r.Message);
                return;
            case "nav":
                if (parts.Length < 4) { this.Print("nav 需要：地图 X Y"); return; }
                if (!float.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var nx) ||
                    !float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ny))
                { this.Print("坐标要写成数字"); return; }
                this.Print(this.NavTo(string.Join(' ', parts[1..^2]), nx, ny).Message);
                return;
            case "summon":
                this.Print(this.SummonForCurrentTerritory());
                return;
            case "summonauto":
                if (parts.Length >= 2) this.autoSummonMinion = ParseOn(parts[1]);
                if (this.autoSummonMinion) this.RefreshUnlockedMinions();
                this.SaveAutoSettings();
                this.Print($"自动召唤随从：{(this.autoSummonMinion ? "已开启" : "已关闭")}");
                return;
            case "leaveauto":
                if (parts.Length >= 2) this.autoLeaveDuty = ParseOn(parts[1]);
                this.SaveAutoSettings();
                this.Print($"独身自动退本：{(this.autoLeaveDuty ? "已开启" : "已关闭")}");
                return;
            case "倒地":
            case "return":
                if (parts.Length >= 2) this.autoConfirmReturn = ParseOn(parts[1]);
                this.SaveAutoSettings();
                this.Print($"倒地自动点「确定」返回：{(this.autoConfirmReturn ? "已开启" : "已关闭")}" +
                           (this.autoConfirmReturn ? "（副本里想躺平等复活就先关掉）" : ""));
                return;
            case "unlock":
            case "解锁":
            case "解窗口锁":
            case "lock":
            case "锁窗":
                // v1.2.9：强锁整体移除，保留别名只为兼容旧习惯，不再有任何锁状态。
                this.Print("v1.2.9 起已移除窗口强锁：/水桶 直接开关主窗，过图落地后会自动恢复。");
                return;
            case "stop":
            case "halt":
            case "cancel":
            case "停止":
                this.StopAll();
                return;
            case "ui":
                this.mainWindowOpen = !this.mainWindowOpen;
                this.ApplyMainWindowNow();
                this.NoteUserWindowChange();
                return;
            case "legacy":
                // v1.2.6 起旧 ImGui 完整版已封死，这个别名只负责把原生主窗拉回来。
                this.mainWindowOpen = true;
                this.ApplyMainWindowNow();
                this.NoteUserWindowChange();
                this.Print("旧 ImGui 完整版已移除；已打开原生主窗。");
                return;
            case "mount":
                // 单独验证上坐骑用：和「前往绿点」走的是同一套流程
                this.MountUpThen(() => this.Print("[坐骑] 流程结束"));
                return;
            default:
                this.Print("未知子命令。"); return;
        }
    }

    // ─────────────────────────── 开图 + 插旗 ───────────────────────────

    private readonly record struct FlagResult(bool Ok, string Message, uint TerritoryId = 0, uint MapId = 0);

    private FlagResult Flag(string mapName, float x, float y)
    {
        if (!this.TryResolveZone(mapName, out var territoryId, out var mapId, out var resolved))
        {
            var near = this.SearchZones(mapName, 4);
            var hint = near.Count > 0 ? "（相近：" + string.Join("、", near) + "）" : string.Empty;
            return new FlagResult(false, $"找不到地图「{mapName}」{hint}");
        }

        this.framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var payload = new MapLinkPayload(territoryId, mapId, x, y, 0.05f);
                this.gameGui.OpenMapWithMapLink(payload);
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[SrankFlag] OpenMapWithMapLink failed");
                try { this.chatGui.PrintError($"[魔法水桶] 开图失败：{ex.Message}"); } catch { /* ignore */ }
            }
        });

        return new FlagResult(true, $"已开图插旗：{resolved} ({x:0.0}, {y:0.0})", territoryId, mapId);
    }

    /// <summary>在地图上给「大水晶」插旗：解析本区大水晶坐标（取 HuntAetheryte 偏好或第一座已解锁的），再走 Flag。</summary>
    private void FlagAetheryte(string mapName, string huntCn)
    {
        if (!this.TryResolveZone(mapName, out _, out var mapId, out var resolved))
        {
            this.Print($"[魔法水桶] 找不到地图「{mapName}」");
            return;
        }
        var mapRow = this.dataManager.GetExcelSheet<Map>()?.GetRow(mapId);
        if (mapRow is null || mapRow.Value.RowId == 0)
        {
            this.Print($"[魔法水桶] 找不到地图行「{resolved}」");
            return;
        }
        var aets = this.AetherytesOnMap(mapRow.Value);
        if (aets.Count == 0)
        {
            this.Print($"[魔法水桶] 「{resolved}」没有已解锁的大水晶，无法插旗");
            return;
        }
        (string Name, float X, float Y)? pick = null;
        if (HuntAetheryte.TryGetValue(huntCn, out var frag))
        {
            var wants = frag.Split('|').Select(Normalize).Where(x => x.Length > 0).ToArray();
            pick = aets.FirstOrDefault(a => wants.Any(w => Normalize(a.Name).Contains(w)));
        }
        var a = pick ?? aets[0];
        var fr = this.Flag(resolved, a.X, a.Y);
        this.Print(fr.Message);
    }

    /// <summary>解析地图名（优先 Map 表，再退回 TerritoryType）。</summary>
    private bool TryResolveZone(string mapName, out uint territoryId, out uint mapId, out string resolved)
    {
        territoryId = 0; mapId = 0; resolved = mapName;
        var wanted = Normalize(mapName);
        if (wanted.Length == 0) return false;

        var maps = this.dataManager.GetExcelSheet<Map>();
        if (maps is not null)
        {
            Map? fuzzy = null;
            foreach (var row in maps)
            {
                var name = SafeMapName(row);
                if (name.Length == 0 || row.TerritoryType.RowId == 0) continue;
                var n = Normalize(name);
                if (n == wanted) { mapId = row.RowId; territoryId = row.TerritoryType.RowId; resolved = name; return true; }
                if (fuzzy is null && n.Contains(wanted, StringComparison.Ordinal)) fuzzy = row;
            }
            if (fuzzy is { } f) { mapId = f.RowId; territoryId = f.TerritoryType.RowId; resolved = SafeMapName(f); return true; }
        }

        var sheet = this.dataManager.GetExcelSheet<TerritoryType>();
        if (sheet is null) return false;
        foreach (var row in sheet)
        {
            var name = SafePlaceName(row);
            if (name.Length == 0 || row.Map.RowId == 0) continue;
            if (Normalize(name) == wanted) { territoryId = row.RowId; mapId = row.Map.RowId; resolved = name; return true; }
        }
        return false;
    }

    // ─────────────────────────── 自动前往：跨服 + 传最近水晶 + 切分线 ───────────────────────────

    private readonly record struct GotoResult(bool Ok, string Message);

    private GotoResult Goto(string map, float x, float y, string world, int line,
        string huntCn = "", bool nav = false, List<Pt>? route = null)
    {
        // v1.2.9：不强锁。传送前只拍一张窗口开关快照，过图时窗口该关就关，落地后按快照恢复。
        this.CaptureWindowStateBeforeTeleport();
        this.Resume();          // 上一次点过停止也没关系，这是一次新的动作
        var fr = this.Flag(map, x, y);
        if (!fr.Ok) return new GotoResult(false, fr.Message);

        // 有绿点、且没给这只怪指定水晶时，选「离点位最近」的那座，落地就少飞一截
        var ae = (route is { Count: > 0 } && !HuntAetheryte.ContainsKey(huntCn))
            ? this.AetheryteForRoute(map, route) ?? this.NearestAetheryte(map, x, y, huntCn)
            : this.NearestAetheryte(map, x, y, huntCn);
        var needTravel = !string.IsNullOrWhiteSpace(world) &&
                         !string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase);
        var alreadyIn = this.InMap(map);

        // 落地后：先按触发条件做准备（换烹调师/青魔法师、召宠物），再切分线、上坐骑寻路
        void Arrived() => this.WaitCorrectWorldThen(world, huntCn, () => this.RunPrepThen(huntCn, () =>
        {
            if (line > 0) this.Say("/pdr insc " + line);
            if (nav) this.WaitNavReadyThen(() => this.MountUpThen(() =>
            {
                // 绿点逐个走：只有一个点也走这条路，不然「到点后的触发动作」（多智兽自爆、
                // 天气预报机器人放狂风暴雪）在单点怪上永远不触发
                if (route is { Count: > 0 }) this.StartGreenRoute(map, route, huntCn);
                else this.Say("/vnav flyflag");
            }));
        }));

        // 传最近大水晶
        void Teleport()
        {
            if (ae is { } a) this.Say("/pdrtelepo " + a.Name);
            if (alreadyIn) this.Sched(Arrived, TimeSpan.FromMilliseconds(3000));
            // 落地等不到也别静默丢掉 Arrived：以前用 WaitUntil，120 秒超时后
            // 「切职业 → 上坐骑 → 巡绿点 → 自爆」整条链无声无息断掉，看着像没做触发。
            else this.WaitUntilOr(() => this.InMap(map), Arrived, () =>
            {
                this.Print($"[前往] 等了 120 秒还没确认落地到「{map}」，仍继续触发动作");
                Arrived();
            }, 240, 500);
        }

        if (needTravel)
        {
            this.SendWorldTravel(world);
            // 落地到目标服就立刻传送，不再干等固定 7 秒
            this.WaitUntilOr(
                () => !this.condition[ConditionFlag.BetweenAreas] &&
                      string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase),
                Teleport,
                () => this.Print($"[跨服] 240 秒还没到 {world}，这次跨服放弃"),
                240, 500);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(world)) this.Print("已在目标服务器 " + world + "，跳过跨服");
            this.Sched(Teleport, TimeSpan.FromMilliseconds(250));
        }

        return new GotoResult(true, (needTravel ? "已跨服 + 开图插旗，落地即传送" : "已开图插旗 + 立即传送")
            + (ae is { } a2 ? $"（最近水晶：{a2.Name}）" : "（未找到最近水晶）")
            + (nav ? "，落地先做准备再飞向绿点" : ""));
    }

    /// <summary>某些 S（如遗产之地·阿提卡斯）在部分大区有「分线」，触发前要先切到指定实例线。
    /// 返回要切到的线号（0 = 不切）。目前仅「先驱勇士 阿提卡斯」在猫小胖 / 陆行鸟大区需要切到 2 线；
    /// 其余大区（豆豆柴 / 莫古力）阿提卡斯不分线，走 0。线号集中在此，要改只动这里。</summary>
    private int HuntLineOverride(string huntEn, string world)
    {
        if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(huntEn)) return 0;
        var dc = this.srankData?.dcs?
            .FirstOrDefault(x => x.worlds.Contains(world, StringComparer.OrdinalIgnoreCase))?.dc ?? "";
        if (huntEn == "Atticus the Primogenitor" && (dc == "猫小胖" || dc == "陆行鸟"))
            return 2;
        return 0;
    }

    /// <summary>传送的固定开销：读条 + 过图 + 落地重新上坐骑大约 7 秒，
    /// 按飞行 25 亚尔/秒折算成 170 亚尔。只有「省下的飞行距离比它多」才值得传一次。</summary>
    private const float TeleportCostYalms = 170f;

    /// <summary>「快到触发点」就可以停寻路、下坐骑做触发动作的距离（亚尔）。
    /// 自爆 / 放技能只要求人站在点位附近（S 刷新后 20+ 亚尔内技能都够得着），
    /// 不必死等 vnav 精确飞到点上停稳 3 秒。只对 QuickActionHunts 那几只启用。
    /// 这几个点有多个绿点时仍会逐个走完，不会只试一个就收工。
    /// 采集类（珂露西亚岛炫学）不在此列；自巡方案已搁置，现由 GatherBuddy 自动采集。</summary>
    private const float EarlyTriggerYalms = 30f;
    /// <summary>多智兽 / 阿姆斯特朗 的「严格到达半径」（亚尔，≈米）。
    /// 这两只自爆怪只在「人实打实落在触发点上（已落地）且 ≤ 此半径」才动手，
    /// 飞行掠过 / 半路 / 还差很远都绝不触发——杜绝「快到时间就在半路自爆」。
    /// 落稳但略偏（点位压在障碍物里、vnav 只能落到附近）时用 2× 容差兜底放行。</summary>
    private const float StrictArriveYalms = 5f;

    /// <summary>这两只走「严格坐标判定」到达：多智兽 / 阿姆斯特朗。
    /// 加怪只需往这里加名字；其余 QuickActionHunts（如天气预报机器人）仍走「快到就动手」。</summary>
    private static readonly HashSet<string> StrictArriveHunts = new(StringComparer.Ordinal)
    {
        "多智兽", "阿姆斯特朗",
    };

    /// <summary>把这张图的绿点按顺序逐个走完：每点插旗 + /vnav flyflag，到了或超时就下一个。
    /// 自爆 / 放技能那几只的 S 刷在哪个候选点是随机的，一个点没出提示就接着试下一个，
    /// 直到聊天出现刷新提示（AutoPause 会把整条链作废）或全部点试完。
    /// 为了少飞，做了两点优化：
    /// ① 每步从「现在站的地方」重新挑最近的那个点（中途传过水晶后顺序会跟着变）；
    /// ② 如果这个点离本图某座大水晶明显更近，就先 /pdrtelepo 过去再飞——
    ///    触发点位大多围着水晶分布，这一步经常能一次省掉横穿半张图。</summary>
    private void StartGreenRoute(string map, List<Pt> pts, string huntCn)
    {
        // 自动前往用它判断「点位动作链有没有真的起来」；断在换装/上坐骑时 30 秒后会补排一次
        this.autoRouteStartedAt = DateTime.UtcNow;
        if (!this.TryResolveZone(map, out _, out var mapId, out _) || pts.Count == 0)
        {
            this.WaitNavReadyThen(() => this.Say("/vnav flyflag"));
            return;
        }
        this.Print($"[{huntCn}] 开始巡绿点：共 {pts.Count} 个");

        var row = this.dataManager.GetExcelSheet<Map>()?.GetRow(mapId) ?? default;
        var sizeFactor = row.RowId != 0 ? row.SizeFactor : (ushort)100;
        var aets = row.RowId != 0 ? this.AetherytesOnMap(row) : new List<(string Name, float X, float Y)>();

        var left = new List<Pt>(pts);
        var done = 0;
        void Step()
        {
            if (left.Count == 0)
            {
                // 自动前往靠这个时间戳判「触发动作已经做完了」：走完就只再等 5 秒提示
                this.autoRouteDoneAt = DateTime.UtcNow;
                this.Print($"[{huntCn}] 绿点已全部逐个试完（{pts.Count} 个），没刷出来就换下一只 S");
                return;
            }

            // ① 从当前位置重新挑最近的那个点
            var pick = 0;
            var pickD = float.MaxValue;
            for (var k = 0; k < left.Count; k++)
            {
                var d = this.YalmsTo(mapId, left[k].X, left[k].Y);
                if (d < pickD) { pickD = d; pick = k; }
            }
            var g = left[pick];
            left.RemoveAt(pick);
            done++;

            // 已经在点上就跳过插旗，直接做这个点的触发动作。
            // 严格怪（多智兽 / 阿姆斯特朗）要「实打实落在点上」（≤StrictArriveYalms）；
            // 其余 QuickActionHunts「快到」就够（30 亚尔），其它怪仍要精确停到 12 亚尔内。
            var nearEnough = StrictArriveHunts.Contains(huntCn) ? StrictArriveYalms
                : QuickActionHunts.Contains(huntCn) ? EarlyTriggerYalms : 12f;
            if (this.YalmsTo(mapId, g.X, g.Y) <= nearEnough)
            {
                this.AtGreenPoint(g, map, huntCn, Step);
                return;
            }

            this.Print($"[{huntCn}] 去 {g.Id} 号点（{done}/{pts.Count}，后面还剩 {left.Count} 个待试）");
            this.Flag(map, g.X, g.Y);

            // ② 本图水晶里有没有一座「传过去再飞」明显更快
            var curD = this.YalmsTo(mapId, g.X, g.Y);
            (string Name, float X, float Y, float Dist)? near = null;
            foreach (var a in aets)
            {
                var d = YalmsBetween(sizeFactor, a.X, a.Y, g.X, g.Y);
                if (near is null || d < near.Value.Dist) near = (a.Name, a.X, a.Y, d);
            }
            var worthTp = near is { } na && na.Dist + TeleportCostYalms < curD;

            this.Sched(() =>
            {
                // 每一段都重新确认坐骑：上一段落地进战斗、被怪打下马、或者本来就只走到了一半，
                // 不补上就会整条线变成步行。
                void Fly()
                {
                    this.FlyFlagThen(() =>
                    {
                        // 真的到点上（8 亚尔）才动手，不让技能丢在半路
                        this.WaitArrive(mapId, g.X, g.Y,
                            () => this.AtGreenPoint(g, map, huntCn, Step),
                            () =>
                            {
                                this.Print($"[{huntCn}] {g.Id} 号点 3 分钟没飞到，先跳过去");
                                Step();
                            },
                            earlyYalms: StrictArriveHunts.Contains(huntCn) ? 0f
                                : QuickActionHunts.Contains(huntCn) ? EarlyTriggerYalms : 0f,
                            strictArrive: StrictArriveHunts.Contains(huntCn),
                            label: huntCn);
                    });
                }

                if (!worthTp)
                {
                    Fly();
                    return;
                }

                var a2 = near!.Value;
                this.Print($"[{huntCn}] {g.Id} 号点离「{a2.Name}」{a2.Dist:F0} 亚尔（从这儿飞要 {curD:F0} 亚尔），先传大水晶");
                this.Say("/pdrtelepo " + a2.Name);
                // 真落到水晶边上再飞；没解锁 / 传送失败就照原样从当前位置飞，不卡流程
                this.WaitUntilOr(
                    () => !this.condition[ConditionFlag.BetweenAreas] &&
                          this.PlayerMapCoords(mapId) is { } pc &&
                          YalmsBetween(sizeFactor, pc.X, pc.Y, a2.X, a2.Y) <= 30f,
                    Fly, Fly, 80, 250);
            }, TimeSpan.FromMilliseconds(600));
        }
        Step();
    }

    // ──────────────────── 到点后的触发动作（天气预报机器人 / 多智兽） ────────────────────

    /// <summary>青魔法走「/青魔法技能 技能名」（英文别名 /blueaction），不是 /ac。</summary>
    private const string BluBlizzard = "/青魔法技能 狂风暴雪";
    private const string BluSelfDestruct = "/青魔法技能 自爆";
    /// <summary>即刻咏唱是法系通用技能，走 /ac；用来让自爆瞬发。</summary>
    private const string Swiftcast = "/ac 即刻咏唱";

    /// <summary>收起武器（/武器状态 关；on=拔出 off=收回）。</summary>
    private const string SheathWeapon = "/bm off";

    /// <summary>自爆留下的 debuff：带着它再爆一次不算数，必须先清掉。</summary>
    private const string WeaknessName = "意志薄弱";

    /// <summary>自己身上有没有某个状态（按当前语言的状态名匹配，中文客户端就是中文名）。</summary>
    private bool HasStatus(string name)
    {
        try
        {
            var p = this.objectTable.LocalPlayer;
            if (p is null) return false;
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            if (sheet is null) return false;
            var list = p.StatusList;
            for (var i = 0; i < list.Length; i++)
            {
                var st = list[i];
                if (st is null || st.StatusId == 0) continue;
                var row = sheet.GetRow(st.StatusId);
                if (row.RowId == 0) continue;
                if (row.Name.ToString().Contains(name)) return true;
            }
        }
        catch (Exception ex) { this.log.Debug(ex, "[SrankFlag] 状态读取失败"); }
        return false;
    }

    /// <summary>踩到绿点后先做这只怪特有的动作，做完再 next()。
    /// 天气预报机器人：下坐骑放狂风暴雪，唱完再走；
    /// 多智兽 / 阿姆斯特朗：下坐骑自爆 → 倒地（两只都只认「在这个点位上 HP 归零」，
    ///   自己爆死同样算数，比站着等野怪打更可控）；
    /// 倒地后走同一条收尾：自动点「返回大水晶」→ 复活 → /pdr joinexitduty 进退副本一次
    /// （清掉意志薄弱）→ 下一个绿点。不在表里的怪直接 next()，行为和以前一致。</summary>
    private void AtGreenPoint(Pt g, string map, string huntCn, System.Action next, int retry = 0,
        bool buffCleared = false)
    {
        // 到点了才动手（停稳 ≤25 亚尔都算到，和 WaitArrive 的容差一致——S 刷新后 20+ 亚尔够得着）。
        // 没到点绝不下坐骑——飞行中按坐骑会直接往下掉，技能也丢在半路。
        if (this.TryResolveZone(map, out _, out var gpMap, out _))
        {
            var dist = this.YalmsTo(gpMap, g.X, g.Y);
            var tol = StrictArriveHunts.Contains(huntCn) ? StrictArriveYalms * 2f
                : QuickActionHunts.Contains(huntCn) ? EarlyTriggerYalms : 25f;
            if (dist > tol)
            {
                if (retry >= 2)
                {
                    this.Print($"[{huntCn}] {g.Id} 号点几次都没飞到（还差 {dist:F0} 亚尔），跳过");
                    next();
                    return;
                }
                this.Print($"[{huntCn}] 还没到 {g.Id} 号点（还差 {dist:F0} 亚尔），不下坐骑，继续飞");
                this.Flag(map, g.X, g.Y);
                this.Sched(() => this.FlyFlagThen(() =>
                {
                    this.WaitArrive(gpMap, g.X, g.Y,
                        () => this.AtGreenPoint(g, map, huntCn, next, retry + 1),
                        next,
                        earlyYalms: StrictArriveHunts.Contains(huntCn) ? 0f
                            : QuickActionHunts.Contains(huntCn) ? EarlyTriggerYalms : 0f,
                        strictArrive: StrictArriveHunts.Contains(huntCn),
                        label: huntCn);
                }), TimeSpan.FromMilliseconds(400));
                return;
            }
        }

        switch (huntCn)
        {
            case "顾尼图":
                // 黑风海固定点位：下坐骑后跑「水蛭飞斧 → 挑衅冰海天使 → 等巨大化/最大化/口锥」整套。
                this.DismountThen(() => this.GroundedThen(() =>
                {
                    this.Print($"[{huntCn}] {g.Id} 号点 · 已到触发点，开始顾尼图触发流程");
                    this.GounituCore();
                }));
                return;

            case "天气预报机器人":
                this.DismountThen(() => this.GroundedThen(() =>
                {
                    this.Print($"[{huntCn}] {g.Id} 号点 · 下坐骑放狂风暴雪");
                    this.Say(BluBlizzard);
                    // 缓一下再判读条：命令要下一帧才开始咏唱，立刻判会误判成「没在唱」，
                    // 然后上坐骑把刚起的读条打断。没在唱就说明放不出来，直接走下一个点。
                    this.Sched(
                        () => this.WaitUntilOr(() => !this.condition[ConditionFlag.Casting], next, next, 40, 250),
                        TimeSpan.FromMilliseconds(800));
                }));
                return;

            case "多智兽":
            case "阿姆斯特朗":
                // 这两只的判定都只认「在这个点位上 HP 归零」：自己爆死同样满足，
                // 比干等着野怪来打可控得多，所以走同一条自爆链（牛头黑神走大水晶那条，见 GotoAetheryte）。
                this.SelfDestructThen(huntCn, g.Id + " 号点", map, next,
                    () => this.AtGreenPoint(g, map, huntCn, next, retry, buffCleared: true));
                return;
        }

        next();
    }

    /// <summary>自爆触发（多智兽 / 阿姆斯特朗 / 牛头黑神共用）。
    /// resume 是「清掉意志薄弱后从这里继续」的回调：绿点＝重新飞回该点、大水晶＝原地再爆；
    /// next 是死亡链结束后要做的（绿点＝下一个点、大水晶＝再爆一次，循环到出怪为止）。
    /// label 只用于日志（绿点写「N 号点」、大水晶写「大水晶」）。</summary>
    private void SelfDestructThen(string huntCn, string label, string map, System.Action next, System.Action resume, bool buffCleared = false)
    {
        var needUniform = huntCn == "阿姆斯特朗";

        // 自爆会留下「意志薄弱」，带着它爆等于白爆。所以每次动手前先看一眼：
        // 有就先进一次副本再出来把它清掉，然后回到这个点继续。
        if (!buffCleared && this.HasStatus(WeaknessName))
        {
            this.Print($"[{huntCn}] {label} · 身上有「意志薄弱」，先进退副本清掉再自爆");
            this.WaitDutyResetThen(() =>
            {
                if (this.HasStatus(WeaknessName))
                {
                    this.Print($"[{huntCn}] 意志薄弱还在，跳过（确认 DailyRoutines 的自动进退副本是否正常工作）");
                    next();
                    return;
                }
                this.Print($"[{huntCn}] 意志薄弱已清掉，回到 {label} 准备自爆");
                resume();
            }, map, huntCn);
            return;
        }

        this.DismountThen(() => this.GroundedThen(() =>
        {
            this.Print(needUniform
                ? $"[{huntCn}] {label} · 准备即刻咏唱 + 自爆（记得先穿好帝国套，判定要「穿着帝国套时 HP 归零」）"
                : $"[{huntCn}] {label} · 准备即刻咏唱 + 自爆");

            // 下坐骑 / 落地瞬间有几帧动作硬直（落地锁）：这期间 /ac 会被服务器静默吞掉，
            // 表现就是「命令发了、日志也打了，游戏里却看不到自爆」。先固定等 500ms 让动作锁解除，
            // 再开始施法链；施法链内部每 ~110ms 轮询重试、确认技能真的生效才推进下一步。
            this.Sched(() =>
            {
                // 1) 即刻咏唱：生效标志是身上出现「即刻咏唱」buff（instant，几乎瞬间可判）。
                this.CastThen(Swiftcast,
                    () => this.HasStatus("即刻咏唱"),
                    () =>
                    {
                        // 2) 即刻咏唱已生效：再等它的动画锁解除（~400ms）才自爆，避免自爆被动画锁吃掉。
                        this.Sched(() =>
                        {
                            // 3) 自爆（青魔法技能）：生效标志是在读条或已倒地。每 ~110ms 重试直到真的按出来。
                            this.CastThen(BluSelfDestruct,
                                () =>
                                {
                                    var casting = false;
                                    try { casting = this.condition[ConditionFlag.Casting]; } catch { /* ignore */ }
                                    return casting || this.Dead();
                                },
                                () => this.WaitUntilOr(this.Dead,
                                    () => this.AfterDeathChain(map, huntCn, next),
                                    () =>
                                    {
                                        this.Print(needUniform
                                            ? $"[{huntCn}] 自爆没生效——确认切成青魔法师并学会自爆，跳过"
                                            : $"[{huntCn}] 自爆没生效——确认切成青魔法师、且已经学会自爆，跳过");
                                        next();
                                    }, 45, 200),
                                "自爆");
                        }, TimeSpan.FromMilliseconds(400));
                    },
                    "即刻咏唱");
            }, TimeSpan.FromMilliseconds(500));
        }));
    }

    // ───────────── 顾尼图（黑风海）专属触发：诱钓 + 挑衅 + 等巨大化/最大化/口锥 ─────────────

    /// <summary>顾尼图的固定触发点（黑风海）。主窗「黑风海触发」和自动前往共用同一点位。</summary>
    private static readonly Pt GounituPt = new(1, 29.8f, 3.9f, 'g');

    /// <summary>顾尼图流程结束回调（true=确认被口锥打中）。自动前往靠它当场收尾，平时为 null。</summary>
    private System.Action<bool>? gounituDone;

    /// <summary>顾尼图流程走完的墙上时间（default = 还没走完），以及它自己判的结果。
    /// 自动前往用这两个值收尾：流程一结束就再给 20 秒看聊天有没有刷新提示，没有就换下一只。
    /// 不直接用回调推进队列，是因为流程里任何一步提前 return 都会让回调收不到，时间戳却一定写得到。</summary>
    private DateTime gounituFinishedAt;
    private bool gounituFlowOk;

    /// <summary>顾尼图流程真正开始（下坐骑落地、开始诱钓）的时刻。default = 还没开始。
    /// 自动前往用它识别「到点了但流程没起来」，避免一直干等到 300 秒兜底。</summary>
    private DateTime gounituStartedAt;

    /// <summary>牛头黑神在大水晶自爆的次数上限（0 = 不限，主窗手动点「前往大水晶」就是 0）。</summary>
    private int aetheryteBombLimit;

    private void GounituSignal(bool ok)
    {
        this.gounituFinishedAt = DateTime.UtcNow;
        this.gounituFlowOk = ok;
        var cb = this.gounituDone;
        this.gounituDone = null;
        if (cb is null) return;
        try { cb(ok); } catch (Exception ex) { this.log.Warning(ex, "[SrankFlag] 顾尼图回调出错"); }
    }

    /// <summary>顾尼图（黑风海）整套触发：切战士 → 传黑风海大水晶 → 飞到固定点位 (29.8, 3.9) →
    /// 到点自动下坐骑 → 对三个深海水蛭丢飞斧 → 挑衅冰海天使 → 等巨大化/最大化 → 受口锥伤害完成触发。
    /// 黑风海有普通大水晶，所以走和其它绿点 S 完全相同的「传水晶 → 飞点位 → WaitArrive」机制（已验证可用），
    /// 不再用独立的自定义导航。点位 (29.8, 3.9) 是固定触发位，硬写在按钮动作里（不依赖会重建的排点数据）。
    /// 停止按钮随时可打断。</summary>
    private void GounituTrigger(string world)
    {
        this.Resume();
        var map = "黑风海";
        var g = GounituPt;
        this.Print("[顾尼图] 开始：切战士 → 传黑风海大水晶 → 飞到固定点位 (29.8, 3.9)");
        // 和其它绿点 S 走同一条路：传最近大水晶 → 落地准备（切战士）→ 上坐骑 → 飞向点位 → WaitArrive 到点
        var wr = this.Goto(map, g.X, g.Y, world, 0, "顾尼图", true, new List<Pt> { g });
        this.Print($"[顾尼图] {wr.Message}");
    }

    private void GounituCore()
    {
        this.gounituStartedAt = DateTime.UtcNow;
        this.Print("[顾尼图] 已到触发点，开始诱钓：对身边的深海水蛭逐个丢飞斧（3 只不同的）");
        this.LeechLoop(3, new HashSet<ulong>());
    }

    /// <summary>对深海水蛭逐个丢飞斧：每次锁「没打过的最近一只」，射程不够就改用 /vnav movetarget 走过去。
    /// 飞斧有 5 秒复唱，所以每只之间等 5.5 秒；走到目标旁边后自动补飞斧。</summary>
    private void LeechLoop(int remaining, HashSet<ulong> hit)
    {
        if (remaining <= 0) { this.ProvokeAngel(); return; }
        this.ActOnTarget("深海水蛭", "/ac 飞斧", 20f, hitObj =>
        {
            hit.Add(hitObj.EntityId);
            this.Print($"[顾尼图] 已打 {3 - remaining + 1}/3 只深海水蛭，等飞斧复唱");
            this.Sched(() => this.LeechLoop(remaining - 1, hit), TimeSpan.FromMilliseconds(5500));
        }, 10, hit, sheatheAfter: true);
    }

    /// <summary>锁定冰海天使并挑衅，挑衅完立刻收武器（用户要求：别一直举着武器贴着天使砍，
    /// 收武器再等它巨大化 / 最大化）。锁不到会重试。</summary>
    private void ProvokeAngel() =>
        this.ActOnTarget("冰海天使", "/ac 挑衅", 25f, _ => this.WaitAngelGrow(), 10, sheatheAfter: true);

    /// <summary>等冰海天使出现「巨大化」或「最大化」buff（后者是最终形态）。</summary>
    private void WaitAngelGrow() =>
        this.WaitUntilOr(() => this.TargetHasStatus("巨大化") || this.TargetHasStatus("最大化"),
            this.WaitAngelMax,
            () =>
            {
                this.Print("[顾尼图] 60s 没看到巨大化/最大化 buff，超时（请确认冰海天使已被挑衅）");
                this.GounituSignal(false);
            },
            120, 500);

    /// <summary>已出现巨大化，等它变成「最大化」；同时记下当前血量，作为之后判定受击的基线。</summary>
    private void WaitAngelMax()
    {
        this.Print("[顾尼图] 已出现巨大化，等待最大化");
        try { this.gounituHpMark = this.objectTable.LocalPlayer?.CurrentHp ?? 0; } catch { this.gounituHpMark = 0; }
        this.WaitUntilOr(() => this.TargetHasStatus("最大化"),
            this.WaitAngelHit,
            () =>
            {
                this.Print("[顾尼图] 看到巨大化但 60s 没到最大化，超时");
                this.GounituSignal(false);
            },
            120, 500);
    }

    /// <summary>已最大化，等玩家血量下降（被口锥打中）= 触发完成。</summary>
    private void WaitAngelHit() =>
        this.WaitUntilOr(() =>
        {
            var hp = 0f;
            try { hp = this.objectTable.LocalPlayer?.CurrentHp ?? 0; } catch { }
            return hp > 0 && hp < this.gounituHpMark - 1f;
        },
            () =>
            {
                this.Say(SheathWeapon);
                this.Print("[顾尼图] ✅ 触发完成：已受到冰海天使口锥伤害");
                this.GounituSignal(true);
            },
            () =>
            {
                this.Say(SheathWeapon);
                this.Print("[顾尼图] 已最大化但 30s 内没检测到受击，超时（触发可能仍已完成，请确认 S 是否刷新）");
                this.GounituSignal(false);
            },
            60, 500);

    /// <summary>按名字锁怪并发技能：在对象表里找名字带 name 的最近对象（可排除已打过的），
    /// 用 ITargetManager.Target 直接设成当前目标（比 /target 聊天命令可靠，实测可秒锁），
    /// 射程不够就改用 /vnav movetarget 走到目标旁边再放技能（替代原来的「猛攻」突进那条腿）。
    /// 锁不到会自动重试，用完 attempts 才放弃。</summary>
    private void ActOnTarget(string name, string action, float range, System.Action<IGameObject> next,
        int attempts = 10, HashSet<ulong>? exclude = null, bool sheatheAfter = true)
    {
        if (this.halted) return;
        if (attempts <= 0)
        {
            this.Print($"[顾尼图] 多次尝试仍没法对「{name}」动手，跳过该步继续");
            this.Say(SheathWeapon);
            next(null!);
            return;
        }
        var lp = this.objectTable.LocalPlayer;
        var obj = this.FindNearestByName(name, exclude);
        if (lp is null || obj is null)
        {
            this.Print($"[顾尼图] 暂时没找到「{name}」，1.5s 后重试（剩 {attempts - 1} 次）");
            this.Sched(() => this.ActOnTarget(name, action, range, next, attempts - 1, exclude),
                TimeSpan.FromMilliseconds(1500));
            return;
        }
        try { this.targets.Target = obj; } catch { /* 设不上就靠对象表数据继续 */ }
        var d = Vector3.Distance(lp.Position, obj.Position);
        if (d > range)
        {
            // 用户要求：用 /vnav movetarget 走到目标旁边，替代「猛攻」突进那条腿。
            // 设好目标后让 vnavmesh 自己寻路过去，进了射程就停导航、放技能；
            // 走不过去（目标太远 / 卡住）就在超时里停导航、跳过这一下继续，不卡死。
            this.Print($"[顾尼图] 「{obj.Name}」约 {d:F0} 亚尔（射程 {range:F0}），改用 /vnav movetarget 走过去");
            this.Say("/vnav movetarget");
            this.WaitUntilOr(() =>
            {
                var lp2 = this.objectTable.LocalPlayer;
                return lp2 != null && obj != null && Vector3.Distance(lp2.Position, obj.Position) <= range;
            },
            () =>
            {
                this.StopNav();
                this.Say(action);
                this.Print($"[顾尼图] 已走到「{obj.Name}」（{range:F0} 亚尔内），使用 {action}");
                if (sheatheAfter) this.Say(SheathWeapon);
                next(obj);
            },
            () =>
            {
                this.StopNav();
                this.Say(SheathWeapon);
                this.Print($"[顾尼图] 走到「{obj.Name}」超时（{range:F0} 亚尔内没进去），跳过这一下继续");
                next(obj);
            },
            140, 300);
            return;
        }
        this.Say(action);
        this.Print($"[顾尼图] 对「{obj.Name}」（{d:F0} 亚尔）使用 {action}");
        if (sheatheAfter) this.Say(SheathWeapon);   // 放完技能收起武器
        next(obj);
    }

    /// <summary>背包里某物品的 HQ 数量（-1=读不到）。</summary>
    private unsafe int HqItemCount(uint itemId)
    {
        try
        {
            var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (im is null) return -1;
            return im->GetInventoryItemCount(itemId, true);
        }
        catch { return -1; }
    }

    /// <summary>Artisan 是否在忙（做单/列表/待机任务）。读不到 IPC 视为不忙。</summary>
    private bool ArtisanBusy()
    {
        try { return this.pi.GetIpcSubscriber<bool>("Artisan.IsBusy")?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    /// <summary>设置 Artisan 耐久（自动生产）模式开关。做完单要退出来，不然它一直挂着。</summary>
    private void ArtisanSetEndurance(bool on)
    {
        try { this.pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus")?.InvokeAction(on); }
        catch { /* IPC 不在就算了 */ }
    }

    /// <summary>GatherBuddy 自动采集开关。注意它的内部名是 GatherBuddyReborn（不是 GatherBuddy），
    /// IPC 前缀跟着内部名走，所以两个前缀都试一遍。返回是否调用成功。</summary>
    private bool GatherBuddySetAuto(bool on)
    {
        foreach (var name in new[] { "GatherBuddyReborn.SetAutoGatherEnabled", "GatherBuddy.SetAutoGatherEnabled" })
        {
            try
            {
                var sub = this.pi.GetIpcSubscriber<bool, object>(name);
                if (sub is null) continue;
                sub.InvokeAction(on);
                return true;
            }
            catch { /* 换下一个前缀 */ }
        }
        return false;
    }

    /// <summary>让 Artisan 自动制作指定配方 amount 个（IPC: Artisan.CraftItem，Artisan 自己选配方+跑 solver）。
    /// 等 Artisan 忙完（或超时）后数背包 HQ 增量并回报；无论成败最后都调 then()（路线不阻塞）。</summary>
    private void CraftViaArtisan(ushort recipeId, uint itemId, int amount, string tag, System.Action then)
    {
        try
        {
            var craftSub = this.pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
            if (craftSub is null)
            {
                this.Print($"{tag} 找不到 Artisan IPC（Artisan.CraftItem），请确认 Artisan 已启用，或手动制作");
                then();
                return;
            }
            if (this.ArtisanBusy())
            {
                this.Print($"{tag} Artisan 正在忙别的制作，不插队，请手动处理");
                then();
                return;
            }
            var hqBefore = this.HqItemCount(itemId);
            this.Print($"{tag} 调用 Artisan 自动制作：配方 {recipeId} × {amount}");
            var gen = this.runGen;
            this.Sched(() =>
            {
                if (gen != this.runGen || this.halted) return;
                try { craftSub.InvokeAction(recipeId, amount); }
                catch (Exception ex)
                {
                    this.Print($"{tag} Artisan 调用失败：{ex.Message}，请手动制作");
                    then();
                    return;
                }

                void Poll(int phase, int left)
                {
                    this.Sched(() =>
                    {
                        if (gen != this.runGen || this.halted) return;
                        try
                        {
                            var busy = this.ArtisanBusy();
                            if (phase == 0)
                            {
                                // 等 Artisan 接单开做（20 秒）
                                if (busy) { Poll(1, 300); return; }   // 开做后最多等 10 分钟
                                if (left <= 0)
                                {
                                    this.ArtisanSetEndurance(false);   // 没开做也要退出来
                                    this.Print($"{tag} Artisan 20 秒内没开始制作（材料不够/没启用？），请手动制作");
                                    then();
                                    return;
                                }
                            }
                            else if (!busy)
                            {
                                this.ArtisanSetEndurance(false);   // 做完自动退出耐久模式
                                var after = this.HqItemCount(itemId);
                                this.Print($"{tag} Artisan 制作结束：背包 HQ {hqBefore} → {after}" +
                                    (after > hqBefore ? "，HQ 已做出 ✓" : "（没做出 HQ，请检查材料）"));
                                then();
                                return;
                            }
                            else if (left <= 0)
                            {
                                this.ArtisanSetEndurance(false);
                                this.Print($"{tag} Artisan 制作超过 10 分钟没结束，请手动检查");
                                then();
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            this.Print($"{tag} 等 Artisan 出错：{ex.Message}，请手动制作");
                            then();
                            return;
                        }
                        Poll(phase, left - 1);
                    }, TimeSpan.FromSeconds(phase == 0 ? 1 : 2));
                }
                Poll(0, 20);
            }, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            this.Print($"{tag} Artisan IPC 读取失败：{ex.Message}，请手动制作");
            then();
        }
    }

    /// <summary>让 Artisan 做出指定配方的 HQ 成品（先驱勇士 阿提卡斯的犎牛牛排）。
    /// 触发条件是做出 HQ。每一步失败都不阻塞路线：打日志后继续往下走，玩家可以手动做。</summary>
    private void CraftStartThen(string recipeName, string tag, System.Action then)
    {
        var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
        if (sheet is null)
        {
            this.Print($"{tag} 配方表读不到，请手动制作「{recipeName}」");
            then();
            return;
        }

        // 配方表里按成品名找第一行（犎牛牛排没有同名配方冲突）
        uint rowId = 0;
        uint itemId = 0;
        foreach (var row in sheet)
        {
            var nm = row.ItemResult.Value.Name.ToString();
            if (nm.Contains(recipeName, StringComparison.Ordinal))
            {
                rowId = row.RowId;
                itemId = row.ItemResult.Value.RowId;
                break;
            }
        }
        if (rowId == 0 || rowId > ushort.MaxValue)
        {
            this.Print($"{tag} 配方表里没找到「{recipeName}」（没学会？），请手动制作");
            then();
            return;
        }

        this.CraftViaArtisan((ushort)rowId, itemId, 1, tag, then);
    }


    // ─────── 得到宽恕的炫学（珂露西亚岛）：插件自己巡草场 + 点采集窗口 ───────

    /// <summary>图姆拉村南侧「等级80 草场」的 6 组刷点（地图坐标），每组 2 个候选位置。
    /// ★ 11-08 用 DalamudMCP 直连游戏逐个验证：这 12 个坐标就是游戏里真实刷出来的
    ///   「草场」EventObj 位置。插件 7 次 movetarget 打出的世界坐标与 GBR 节点数据
    ///   完全重合（误差 &lt; 0.1 世界单位），此前所谓「草场比插旗点偏 6.8 亚尔」是分析脚本
    ///   换算常数写错造成的假象，不是真实偏移。
    ///   例：(-477.6227, -423.1174) → (11.93, 13.02)①、(-454.49, -407.9195) → (12.39, 13.32)②、
    ///       (-415.8268, -271.7809) → (13.16, 16.04)④、(-301.1088, -390.8202) → (15.46, 13.66)⑥。
    ///   换算：Map 492 / SizeFactor 100 / Offset 24，map = world * 0.02 + 21.48（1 地图坐标 = 50 亚尔）。
    ///   来源：矮人棉(Item 27759) → GatheringItem 594 → GatheringPointBase 625（等级80 草场）→
    ///   GatheringPoint 32759..32764 的 world_locations.json 精确世界坐标。
    /// 用户最初给的 6 个 1 位小数坐标是这批点的粗略近似（1 位小数＝5 亚尔量化误差），
    /// 其中 (12.7,15.5)/(15.3,13.7) 偏 20+ 亚尔，故仍以这 12 个精确点为权威。
    /// 规律：每组是同一个草场槽位，命中其中一个、确实采不动才跳下一组；组内两点相距 5~24 亚尔，
    /// 30 亚尔查找范围通常能覆盖另一个候选。</summary>
    private static readonly (float X, float Y)[][] XuanxueGrassGroups =
    {
        new[] { (11.93f, 13.02f), (12.25f, 13.37f) }, // ① GP32762
        new[] { (12.39f, 13.32f), (12.44f, 13.47f) }, // ② GP32761 / 32762
        new[] { (12.78f, 15.89f), (12.81f, 15.54f) }, // ③ GP32760
        new[] { (13.16f, 16.04f), (13.27f, 16.03f) }, // ④ GP32760 / 32759
        new[] { (15.24f, 14.02f), (15.43f, 13.76f) }, // ⑤ GP32764
        new[] { (15.46f, 13.66f), (15.51f, 13.85f) }, // ⑥ GP32763 / 32764
    };

    /// <summary>炫学巡点的推进方式：同组换另一个候选 / 整组采完直接进下一组。</summary>
    private enum XuanxueAdvance { NextSlot, NextGroup }
    private const string XuanxueMap = "珂露西亚岛";
    private const string XuanxueGrass = "草场";
    private const string XuanxueGrassAlt = "图姆拉村";
    private const string XuanxueItem = "矮人棉";
    /// <summary>一轮最多采多少次。11-08 实测：手动在同一草场连采 492 次才触发（概率随次数
    /// 从 13.9% 涨到 14.35%），旧版 50 次就收工等于放弃触发，故上调到 300。
    /// 触发成功会由 AutoHandleHit → AutoPause → StopAll 直接作废整条链，不会白采。</summary>
    private const int XuanxueStrikes = 300;
    /// <summary>同一个草场连续「窗口关了 / 开不了窗」的重试上限，超过才换下一组。
    /// 采集点有耐久，采完会自己「恢复 1 次」/「再次发现恢复 4 次」，手动挖就是原地反复采；
    /// 旧版窗口一关就飞走，回来时草场还在 CD，于是 16 次「8 秒没等到采集窗口」空转。</summary>
    private const int XuanxueWindowRetry = 6;
    private const int XuanxueReopenGapMs = 1500;
    /// <summary>只认「当前候选点旁边」的草场：超过这个距离就当这个候选没草场，换同组另一个候选。
    /// 组内两点相距 5~24 亚尔，所以站在一个候选点常常能直接看到同组另一个候选刷出来的草场。
    /// 不限制距离会锁到别组的草场，表现成「这个点明明没草场，却跑老远去采」。</summary>
    private const float XuanxueGrassRange = 30f;
    /// <summary>飞进插旗点这个距离就算到点，直接下坐骑找草场；不等「停稳 3 秒」。</summary>
    private const float XuanxueArriveYalms = 8f;
    /// <summary>每次点「矮人棉」之间留的时间：采集有约 2~3 秒的动作 / 进度条，
    /// 点太快会在动画里连点，点击全被吞掉，反被判成「采不动」提前换点。</summary>
    private const int XuanxueGatherGapMs = 3000;
    /// <summary>连续几次没采到东西才切点击方式 / 换点（配合上面的间隔，约 12 秒）。</summary>
    private const int XuanxueStallLimit = 4;

    /// <summary>当前开着的采集窗口，没有就返回 null。</summary>
    private unsafe AddonGathering* GatheringAddon()
    {
        try
        {
            var w = this.gameGui.GetAddonByName("Gathering", 1);
            if (w.IsNull || !w.IsVisible) return null;
            return (AddonGathering*)w.Address;
        }
        catch { return null; }
    }

    /// <summary>点采集窗口里第 index 行 = 采一次，并先「勾选矮人棉」。
    /// strategy 0：ECommons 同款「真实点击 checkbox」（用这一行自己注册的事件）；
    /// strategy 1：visland 同款「直接回调 (2, index)」。两种都在社区里验证过，
    /// 客户端版本有差异时由上层自动切换，避免整条触发卡死。</summary>
    private unsafe bool GatherOnce(int index, int strategy)
    {
        var addon = this.GatheringAddon();
        if (addon is null) return false;
        AtkComponentCheckBox* cb = addon->GatheredItemComponentCheckbox[index];
        if (cb is null || !cb->IsEnabled) return false;
        if (!cb->IsChecked) cb->SetChecked(true);
        var unit = (AtkUnitBase*)addon;
        try
        {
            if (strategy == 0)
            {
                var node = cb->OwnerNode;
                if (node is null) return false;
                var evt = node->AtkResNode.AtkEventManager.Event;
                if (evt is null) return false;
                var data = stackalloc AtkEventData[1];
                unit->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
                return true;
            }
            var values = stackalloc AtkValue[2];
            values[0].Type = AtkValueType.Int; values[0].Int = 2;
            values[1].Type = AtkValueType.UInt; values[1].UInt = (uint)index;
            unit->FireCallback(1, values);
            return true;
        }
        catch (Exception ex)
        {
            this.Print($"[采集窗口] 点击第 {index} 行失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>【v1.1.70 起搁置：代码完整保留但不再调用，方便以后切回来】
    /// 重构后的炫学触发：vnavmesh 逐个飞草场点 → 下坐骑靠近「草场」交互开采集窗口 →
    /// 窗口里按 ItemIds 找到矮人棉、勾选后一次一次采；窗口关了就原地重开继续采（耐久会恢复），
    /// 直到采满上限 / 触发成功 / 这个草场确实采不动才换下一组。
    /// 不依赖 GatherBuddy；计数仍用背包事件（按入库批次记「次数」，增加获得量也只算一次）。
    /// 当前炫学改回调用 GatherBuddy Reborn 自动采集，见 PrepKind.Garden 分支与 GatherStrikesThen。</summary>
    private unsafe void XuanxueGatherThen(string huntCn, System.Action done)
    {
        uint itemId = 0;
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet is not null)
                foreach (var it in sheet)
                    if (it.Name.ToString() == XuanxueItem) { itemId = it.RowId; break; }
        }
        catch { }

        if (itemId == 0)
        {
            this.Print($"[{huntCn}] 物品表里没找到「{XuanxueItem}」，这次触发跳过");
            this.autoGatherDoneAt = DateTime.UtcNow;
            done();
            return;
        }
        if (!this.TryResolveZone(XuanxueMap, out _, out var mapId, out _))
        {
            this.Print($"[{huntCn}] 找不到地图「{XuanxueMap}」，这次触发跳过");
            this.autoGatherDoneAt = DateTime.UtcNow;
            done();
            return;
        }

        var gen = this.runGen;
        var count = 0;
        var groupIdx = 0;   // 当前草场组 0..5
        var slotIdx = -1;   // 组内候选 0..1；从 -1 起，第一次 NextSlot 正好落到第 1 组第 1 个候选
        var round = 1;      // 6 组巡完一轮后循环（草场采完会刷新），直到采满 50 次
        var stall = 0;
        var strategy = 0;
        var windowRetry = 0;   // 同一草场连续「窗口关 / 开不了窗」次数；能重新开窗口就清零
        var started = DateTime.UtcNow;
        var lastProgress = started;

        void OnInvChanged(IReadOnlyCollection<Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryEventArgs> events)
        {
            if (gen != this.runGen) return;
            foreach (var e in events)
            {
                if (e is null || e.Item.ItemId != itemId) continue;
                if (e.Type == Dalamud.Game.Inventory.GameInventoryEvent.Added) { count++; return; }
                if (e.Type == Dalamud.Game.Inventory.GameInventoryEvent.Changed
                    && e is Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryItemChangedArgs ch
                    && ch.Item.Quantity > ch.OldItemState.Quantity) { count++; return; }
            }
        }

        void Finish(string msg)
        {
            try { this.gameInventory.InventoryChanged -= OnInvChanged; } catch { }
            this.StopNav();
            this.CloseAddonIfOpen("Gathering");
            this.autoGatherDoneAt = DateTime.UtcNow;
            this.Print($"[{huntCn}] {msg}");
            done();
        }

        try { this.gameInventory.InventoryChanged += OnInvChanged; }
        catch (Exception ex)
        {
            this.Print($"[{huntCn}] 背包事件订阅失败：{ex.Message}，这次触发跳过");
            this.autoGatherDoneAt = DateTime.UtcNow;
            done();
            return;
        }

        this.Resume();
        this.StopNav();
        this.Print($"[{huntCn}] 开始巡草场：{XuanxueGrassGroups.Length} 组 × 每组 2 个候选（点位已用游戏内草场对象逐个验证），" +
                   $"命中一个就原地反复采（耐久会恢复），最多采「{XuanxueItem}」{XuanxueStrikes} 次或触发成功");

        // 采集窗口关了 / 交互没开出窗口时，原地重开继续采，而不是立刻飞下一组。
        // 11-08 实测：草场采空后耐久会自己恢复，手动挖就是在同一个点连采 492 次触发；
        // 旧版「窗口一关就换组」回来时草场还在 CD，于是 16 次「8 秒没等到采集窗口」空转。
        void RetrySameGrass(string why)
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            if (windowRetry >= XuanxueWindowRetry)
            {
                this.Print($"[{huntCn}] {why}；连续 {XuanxueWindowRetry} 次都开不了窗口，去下一组（{count}/{XuanxueStrikes}）");
                this.CloseAddonIfOpen("Gathering");
                // 等最后一次采集的背包入库事件落地，别刚点完就漏计一次
                this.Sched(() => NextPoint(XuanxueAdvance.NextGroup), TimeSpan.FromMilliseconds(800));
                return;
            }
            windowRetry++;
            this.Print($"[{huntCn}] {why}，原地等耐久恢复后继续采（第 {windowRetry}/{XuanxueWindowRetry} 次，{count}/{XuanxueStrikes}）");
            this.Sched(() =>
            {
                if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
                this.StopNav();
                TryFindGrass(0);
            }, TimeSpan.FromMilliseconds(XuanxueReopenGapMs));
        }

        void GatherAtPoint()
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            if (count >= XuanxueStrikes) { NextPoint(XuanxueAdvance.NextGroup); return; }

            var addon = this.GatheringAddon();
            if (addon is null)
            {
                // 采集窗口被游戏关掉 ≠ 这个草场采完了：耐久会恢复，手动挖就是原地反复采。
                RetrySameGrass("采集窗口关了");
                return;
            }
            windowRetry = 0;   // 窗口还在 / 又开出来了，这个草场仍可采

            var idx = -1;
            var ids = addon->ItemIds;
            for (var i = 0; i < ids.Length; i++)
                if (ids[i] == itemId) { idx = i; break; }

            if (idx < 0)
            {
                var names = new List<string>();
                for (var i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == 0) continue;
                    var nm = "?";
                    try
                    {
                        var row = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(ids[i]);
                        if (row is { } rr) nm = rr.Name.ToString();
                    }
                    catch { }
                    names.Add($"{i}:{nm}");
                }
                this.Print($"[{huntCn}] 采集窗口里没有「{XuanxueItem}」（本窗口：{string.Join("、", names)}），换下一组");
                this.CloseAddonIfOpen("Gathering");
                this.Sched(() => NextPoint(XuanxueAdvance.NextGroup), TimeSpan.FromMilliseconds(400));
                return;
            }
            if (idx >= addon->GatheredItemComponentCheckbox.Length)
            {
                this.CloseAddonIfOpen("Gathering");
                this.Sched(() => NextPoint(XuanxueAdvance.NextGroup), TimeSpan.FromMilliseconds(400));
                return;
            }

            var before = count;
            if (!this.GatherOnce(idx, strategy))
            {
                if (++stall < XuanxueStallLimit)
                {
                    this.Sched(GatherAtPoint, TimeSpan.FromMilliseconds(XuanxueGatherGapMs));
                    return;
                }
                this.Print($"[{huntCn}] 「{XuanxueItem}」这一行一直点不动，退出采集窗口换下一组（{count}/{XuanxueStrikes}）");
                this.CloseAddonIfOpen("Gathering");
                this.Sched(() => NextPoint(XuanxueAdvance.NextGroup), TimeSpan.FromMilliseconds(500));
                return;
            }

            this.Sched(() =>
            {
                if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
                if (count > before)
                {
                    stall = 0;
                    lastProgress = DateTime.UtcNow;
                    if (count % 10 == 0) this.Print($"[{huntCn}] 采集进度：{count}/{XuanxueStrikes} 次");
                }
                else if (++stall >= XuanxueStallLimit)
                {
                    stall = 0;
                    if (strategy == 0)
                    {
                        strategy = 1;
                        this.Print($"[{huntCn}] 点窗口没采到东西，切换另一种点击方式再试");
                    }
                    else
                    {
                        strategy = 0;
                        this.Print($"[{huntCn}] 这个草场点采不动，换下一组（{count}/{XuanxueStrikes}）");
                        this.CloseAddonIfOpen("Gathering");
                        NextPoint(XuanxueAdvance.NextGroup);
                        return;
                    }
                }
                GatherAtPoint();
            }, TimeSpan.FromMilliseconds(XuanxueGatherGapMs));
        }

        void OpenWindow(IGameObject obj)
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            try
            {
                var ptr = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                var r = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(ptr);
                this.Print(r != 0
                    ? $"[{huntCn}] 已点「{XuanxueGrass}」，等采集窗口"
                    : $"[{huntCn}] 点「{XuanxueGrass}」没响应（可能还差几步 / 没切园艺工）");
            }
            catch (Exception ex) { this.Print($"[{huntCn}] 交互「{XuanxueGrass}」出错：{ex.Message}"); }

            this.WaitUntilOr(() => this.GatheringAddon() is not null, GatherAtPoint,
                // 交互没开出窗口，多半是草场采空进 CD / 还差一点距离 / 采集硬直没结束。
                // 旧版直接飞下一组，回来照样开不了 → 全轮空转；现在原地重试交互。
                () => RetrySameGrass("8 秒没等到采集窗口"), 16, 500);
        }

        void ApproachGrass()
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            this.Print($"[{huntCn}] 已到草场点，下坐骑找旁边的「{XuanxueGrass}」");
            this.StopNav();
            this.DismountThen(() => this.GroundedThen(() =>
            {
                if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
                TryFindGrass(0);
            }));
        }

        // 到点位后按名字找「草场」：先在插件对象表里找，找不到就发游戏原生 /target
        // 按名字选最近的目标（采集点 EventObj 常常不在 Dalamud objectTable 里，但游戏
        // 自己能选中），选中后一律交给 /vnav movetarget 走到交互距离再开采集窗口。
        // 两次尝试都没有 → 按「每组只刷一个」规则直接去下一组，不在同组另一个候选上磨蹭。
        void TryFindGrass(int attempt)
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            var obj = this.FindNearestByName(XuanxueGrass, maxDist: XuanxueGrassRange)
                      ?? this.FindNearestByName(XuanxueGrassAlt, maxDist: XuanxueGrassRange);
            if (obj is not null) { UseGrass(obj); return; }

            // objectTable 没刷出来：用游戏原生 /target 按名字选最近的「草场」再回读目标。
            // 第一次试「草场」，第二次试「图姆拉村」，避开不同客户端的命名差异。
            var keyword = attempt == 0 ? XuanxueGrass : XuanxueGrassAlt;
            try { this.targets.Target = null; } catch { }
            this.Say("/target " + keyword);
            this.Sched(() =>
            {
                if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
                IGameObject? t = null;
                try { t = this.targets.Target; } catch { }
                var nm = t?.Name?.ToString() ?? string.Empty;
                var lp = this.objectTable.LocalPlayer;
                var d = lp is not null && t is not null
                    ? Vector3.Distance(lp.Position, t.Position) : float.MaxValue;
                if (t is not null && d <= XuanxueGrassRange
                    && (nm.Contains(XuanxueGrass, StringComparison.OrdinalIgnoreCase)
                        || nm.Contains(XuanxueGrassAlt, StringComparison.OrdinalIgnoreCase)))
                {
                    this.Print($"[{huntCn}] /target 选中「{nm}」（{d:F1} 亚尔），交给 vnav movetarget");
                    UseGrass(t);
                    return;
                }
                if (attempt < 1)
                {
                    this.Sched(() => TryFindGrass(attempt + 1), TimeSpan.FromMilliseconds(500));
                    return;
                }
                var near = new List<string>();
                try
                {
                    var lp0 = this.objectTable.LocalPlayer;
                    if (lp0 is not null)
                        foreach (var o in this.objectTable)
                        {
                            if (o is null) continue;
                            var onm = o.Name?.ToString() ?? string.Empty;
                            if (onm.Length == 0) continue;
                            var d0 = Vector3.Distance(lp0.Position, o.Position);
                            if (d0 <= XuanxueGrassRange) near.Add($"{onm}@{d0:F0}");
                        }
                }
                catch { }
                this.Print($"[{huntCn}] 这个点位旁边（{XuanxueGrassRange:F0} 亚尔内）没有「{XuanxueGrass}」，直接去下一组；" +
                           $"附近对象：{(near.Count > 0 ? string.Join("、", near.Take(12)) : "（对象表里没有）")}");
                this.Sched(() => NextPoint(XuanxueAdvance.NextGroup), TimeSpan.FromMilliseconds(300));
            }, TimeSpan.FromMilliseconds(400));
        }

        // 已经拿到「草场」对象：把 vnav 要去的目标点位输出到聊天框，再 movetarget 过去交互。
        void UseGrass(IGameObject obj)
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            try { this.targets.Target = obj; } catch { }

            var gp = XuanxueGrassGroups[groupIdx][slotIdx];
            var nm = obj.Name?.ToString() ?? XuanxueGrass;
            this.Print($"[{huntCn}] 【vnav 点位】movetarget → 「{nm}」世界({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})；" +
                       $"本组点位 第 {groupIdx + 1}/{XuanxueGrassGroups.Length} 组 候选 {slotIdx + 1}/2 ({gp.X:F2}, {gp.Y:F2})");

            bool Near()
            {
                try
                {
                    var lp = this.objectTable.LocalPlayer;
                    return lp is not null && Vector3.Distance(lp.Position, obj.Position) <= 3.2f;
                }
                catch { return false; }
            }

            if (Near()) { OpenWindow(obj); return; }
            this.Say("/vnav movetarget");
            this.WaitUntilOr(Near,
                () => { this.StopNav(); OpenWindow(obj); },
                () =>
                {
                    this.StopNav();
                    RetrySameGrass($"靠不到「{XuanxueGrass}」");
                }, 40, 250);
        }

        // how = NextSlot：只是插旗失败 / 没飞到（还没到点）→ 试同组另一个候选位置，
        //                  组内两个都用完才进下一组。
        // how = NextGroup：这一组采完、或这个点位旁边根本没草场 / 靠不到 / 开不了窗 →
        //                  按「每组只随机刷一个」规则，同组另一个候选必然是空的，
        //                  直接跳到下一组，省掉一半无效寻路。
        void NextPoint(XuanxueAdvance how)
        {
            if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
            if (count >= XuanxueStrikes)
            {
                Finish($"已采「{XuanxueItem}」{count} 次（≥{XuanxueStrikes}），自动退出采集窗口 ✓");
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - started).TotalMinutes >= 30) { Finish($"总超时，只采到 {count}/{XuanxueStrikes} 次，退出"); return; }
            if ((now - lastProgress).TotalMinutes >= 4)
            {
                Finish($"4 分钟没采到「{XuanxueItem}」（{count}/{XuanxueStrikes} 次），退出");
                return;
            }

            // 去下一点位之前先把采集窗口退干净：带着 Gathering 窗口上坐骑 / 寻路，
            // 窗口会卡住，下一次交互也打不开；等它确实关掉再插旗。
            void Advance()
            {
                var group = XuanxueGrassGroups[groupIdx];
                var groupDone = how == XuanxueAdvance.NextGroup || slotIdx >= group.Length - 1;
                if (groupDone)
                {
                    if (how == XuanxueAdvance.NextGroup)
                        this.Print($"[{huntCn}] 这一组只刷一个草场，已采完，跳过同组另一个候选（{count}/{XuanxueStrikes}）");
                    groupIdx++;
                    slotIdx = -1;
                    if (groupIdx >= XuanxueGrassGroups.Length)
                    {
                        groupIdx = 0;
                        round++;
                        this.Print($"[{huntCn}] {XuanxueGrassGroups.Length} 组草场巡完一轮，开始第 {round} 轮（已采 {count}/{XuanxueStrikes}）");
                    }
                }
                slotIdx++;
                group = XuanxueGrassGroups[groupIdx];
                var p = group[slotIdx];
                stall = 0;
                windowRetry = 0;   // 换了新点位，重新给这一组完整重试机会
                this.Print($"[{huntCn}] 第 {round} 轮 · 第 {groupIdx + 1}/{XuanxueGrassGroups.Length} 组 · " +
                           $"候选 {slotIdx + 1}/{group.Length} → ({p.X:F2}, {p.Y:F2})（已采 {count}/{XuanxueStrikes}）");

                // 聊天框输出 vnav 这次要飞的点位（地图坐标，和小地图一致），方便逐个核对
                this.Print($"[{huntCn}] 【vnav 点位】flyflag → {XuanxueMap} ({p.X:F2}, {p.Y:F2})" +
                           $" 〔第 {round} 轮 · 第 {groupIdx + 1}/{XuanxueGrassGroups.Length} 组 · 候选 {slotIdx + 1}/{group.Length}〕");
                var fr = this.Flag(XuanxueMap, p.X, p.Y);
                if (!fr.Ok)
                {
                    this.Print($"[{huntCn}] 插旗失败：{fr.Message}");
                    this.Sched(() => NextPoint(XuanxueAdvance.NextSlot), TimeSpan.FromSeconds(2));
                    return;
                }
                // 采集类：飞进插旗点 8 亚尔就直接下坐骑找草场。
                // 既不用自爆那几只「还差 12 亚尔提前停飞」，也不等 WaitArrive 的「停稳 3 秒」。
                // 停导航 / 下坐骑统一由 ApproachGrass 负责；旁边没草场由 TryFindGrass 换同组另一候选。
                // 加一道「确实起飞了」的闸门：同组相邻候选只隔 5~8 亚尔，人还没动就可能已经
                // 落在新点的 8 亚尔内，那样会原地反复发 flyflag 又立刻停掉，永远飞不到下一组。
                System.Numerics.Vector3 flyFrom;
                try { flyFrom = this.objectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero; }
                catch { flyFrom = System.Numerics.Vector3.Zero; }
                this.FlyFlagThen(() => this.WaitUntilOr(
                    () =>
                    {
                        var lp = this.objectTable.LocalPlayer;
                        if (lp is null) return false;
                        if (System.Numerics.Vector3.Distance(flyFrom, lp.Position) < 3f) return false;
                        return this.YalmsTo(mapId, p.X, p.Y) <= XuanxueArriveYalms;
                    },
                    ApproachGrass,
                    () => { this.Print($"[{huntCn}] 这个候选点没飞到，自动跳过"); NextPoint(XuanxueAdvance.NextSlot); },
                    600, 150));   // 90 秒：坐骑失败按地面寻路时跨簇会慢一些
            }

            if (this.GatheringAddon() is not null)
            {
                this.CloseAddonIfOpen("Gathering");
                this.Print($"[{huntCn}] 采集已收尾，退出采集窗口，再去下一个点位（{count}/{XuanxueStrikes}）");
                this.Sched(() =>
                {
                    if (gen != this.runGen || this.halted) { Finish("已停止，退出采集"); return; }
                    if (this.GatheringAddon() is not null) this.CloseAddonIfOpen("Gathering");
                    Advance();
                }, TimeSpan.FromMilliseconds(600));
            }
            else Advance();
        }

        // slotIdx 从 -1 起，第一次 NextSlot 正好落到第 1 组第 1 个候选
        NextPoint(XuanxueAdvance.NextSlot);
    }

    /// <summary>【v1.1.70 重新启用】让 GatherBuddy 的自动采集去采指定物品，按「采集次数」计数
    /// （增加获得量也只算一次，所以不能按背包数量算）。每次敲节点入库至少产生一个背包事件，
    /// 事件数 ≤ 真实次数，数到 strikes 时真实次数一定 ≥ strikes，多做无害。</summary>
    /// 前提：GatherBuddy 的自动采集清单里有该物品（数量设大些，如 999，清单数量归零它就停了）。
    private void GatherStrikesThen(string itemName, int strikes, string tag, System.Action then)
    {
        // 按名字在物品表里找 itemId
        uint itemId = 0;
        try
        {
            var items = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (items is not null)
            {
                foreach (var it in items)
                {
                    if (it.Name.ToString() == itemName) { itemId = it.RowId; break; }
                }
            }
        }
        catch { }
        if (itemId == 0)
        {
            this.Print($"{tag} 物品表里没找到「{itemName}」，请手动采集 {strikes} 次");
            then();
            return;
        }

        var count = 0;               // 观测到的采集次数（事件批数，保守值）
        var gen = this.runGen;
        var lastTick = DateTime.UtcNow;
        var lastCount = 0;
        var lastReport = 0;
        var hinted = false;
        var draining = false;    // 已到目标次数，等节点窗口收尾
        var drainTries = 20;     // 收尾最多等 10 秒（2s × 20）

        // 每次入库批（同一帧内的新增+数量变化合并为一批）至多记 1 次：一次敲击的产物
        // （如 NQ+HQ 分两格）会落在同一批里，不会重复计；两下敲击间隔数秒、不会同批。
        void OnInvChanged(IReadOnlyCollection<Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryEventArgs> events)
        {
            if (gen != this.runGen) return;
            foreach (var e in events)
            {
                if (e is null) continue;
                if (e.Item.ItemId != itemId) continue;
                if (e.Type == Dalamud.Game.Inventory.GameInventoryEvent.Added) { count++; return; }
                if (e.Type == Dalamud.Game.Inventory.GameInventoryEvent.Changed
                    && e is Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryItemChangedArgs ch
                    && ch.Item.Quantity > ch.OldItemState.Quantity)
                { count++; return; }
            }
        }

        try { this.gameInventory.InventoryChanged += OnInvChanged; }
        catch (Exception ex)
        {
            this.Print($"{tag} 背包事件订阅失败：{ex.Message}，请手动采集 {strikes} 次");
            then();
            return;
        }

        this.Print($"{tag} 开始自动采集：目标 {itemName} × {strikes} 次（GatherBuddy 自动采集）。" +
            "请确认 GatherBuddy 采集清单里有该物品且数量 ≥ 999");

        if (!this.GatherBuddySetAuto(true))
        {
            this.Print($"{tag} 调用 GatherBuddy 自动采集失败（IPC 没接上），请手动采集 {strikes} 次");
            try { this.gameInventory.InventoryChanged -= OnInvChanged; } catch { }
            then();
            return;
        }
        // 3 秒后回读一次状态，确认真的开了（方便排查）
        this.Sched(() =>
        {
            if (gen != this.runGen) return;
            var st = this.GatherBuddyAutoStatus();
            if (st.Length > 0) this.Print($"{tag} GatherBuddy 状态：{st}");
        }, TimeSpan.FromSeconds(3));

        void Finish(string msg, bool disableAuto)
        {
            try { this.gameInventory.InventoryChanged -= OnInvChanged; } catch { }
            if (disableAuto)
            {
                this.GatherBuddySetAuto(false);
            }
            // 关自动采集会中止它的收尾任务，采集窗口可能还开着 → 主动关掉
            this.CloseAddonIfOpen("Gathering");
            // 采集本身就是这只怪的触发动作：正常收工写完成时间戳，自动前往只再等 5 秒提示就换下一只。
            // 只有本任务链还有效时才写——换任务 / StopAll 作废旧链时不能写，否则会污染下一只的判断。
            if (gen == this.runGen && !this.halted) this.autoGatherDoneAt = DateTime.UtcNow;
            this.Print(msg);
            then();
        }

        void Poll()
        {
            this.Sched(() =>
            {
                if (gen != this.runGen || this.halted) { Finish($"{tag} 流程已停止，自动采集退出", true); return; }
                try
                {
                    // 到目标次数后不立刻关：先让 GatherBuddy 把当前节点收尾（关采集窗口、起身），
                    // 窗口关了（或等满 10 秒）再关自动采集，避免它的收尾任务被中止留窗口。
                    if (count >= strikes) draining = true;

                    var gatheringOpen = false;
                    try
                    {
                        var w = this.gameGui.GetAddonByName("Gathering", 1);
                        gatheringOpen = !w.IsNull && w.IsVisible;
                    }
                    catch { }

                    if (draining)
                    {
                        if (!gatheringOpen || drainTries <= 0)
                        {
                            Finish($"{tag} 采集完成：已采 {itemName} {count} 次（≥{strikes}）✓", true);
                            return;
                        }
                        drainTries--;
                    }

                    var now = DateTime.UtcNow;
                    if (count != lastCount) { lastCount = count; lastTick = now; hinted = false; }

                    // 每 10 次播报一次进度
                    if (count > 0 && count / 10 != lastReport / 10)
                    {
                        lastReport = count;
                        this.Print($"{tag} 采集进度：{count}/{strikes} 次");
                    }

                    // 5 分钟没有任何入库 → 大概率清单里没有该物品 / 位置不对
                    if ((now - lastTick).TotalMinutes >= 5 && !hinted)
                    {
                        hinted = true;
                        this.Print($"{tag} 5 分钟没有采到 {itemName}：检查 GatherBuddy 采集清单里有没有它（数量 ≥ 999）、当前是不是园艺工");
                    }

                    // 40 分钟总超时，别把整条路挂死
                    if ((now - lastTick).TotalMinutes >= 40)
                    {
                        Finish($"{tag} 自动采集超时（只采到 {count}/{strikes} 次），退出并继续，剩下的请手动补", true);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Finish($"{tag} 采集计数出错：{ex.Message}，退出自动采集", true);
                    return;
                }
                Poll();
            }, TimeSpan.FromSeconds(2));
        }
        Poll();
    }

    /// <summary>如果某个 UI 窗口开着就关掉（AtkUnitBase.Close）。主线程调用。</summary>
    private unsafe bool CloseAddonIfOpen(string addonName)
    {
        try
        {
            var w = this.gameGui.GetAddonByName(addonName, 1);
            if (w.IsNull || !w.IsVisible) return false;
            var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)w.Address;
            if (unit is null) return false;
            unit->Close(true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>读 GatherBuddy 自动采集状态文字（开/关 + 当前在干什么），读不到返回空串。</summary>
    private string GatherBuddyAutoStatus()
    {
        foreach (var p in new[] { "GatherBuddyReborn.", "GatherBuddy." })
        {
            try
            {
                var on = this.pi.GetIpcSubscriber<bool>(p + "IsAutoGatherEnabled").InvokeFunc();
                var txt = this.pi.GetIpcSubscriber<string>(p + "GetAutoGatherStatusText").InvokeFunc();
                return $"{(on ? "已开启" : "未开启")} / {txt}";
            }
            catch { }
        }
        return string.Empty;
    }

    /// <summary>在对象表里找名字包含 name 的最近对象；exclude 里的 EntityId 跳过（已打过的水蛭），
    /// maxDist 限制只找附近的对象（炫学巡点用，避免锁到别的草场簇）。</summary>
    private IGameObject? FindNearestByName(string name, HashSet<ulong>? exclude = null,
        float maxDist = float.MaxValue)
    {
        try
        {
            var lp = this.objectTable.LocalPlayer;
            if (lp is null) return null;
            IGameObject? best = null;
            var bestD = float.MaxValue;
            foreach (var o in this.objectTable)
            {
                if (o is null) continue;
                if (exclude is not null && exclude.Contains(o.EntityId)) continue;
                var nm = o.Name?.ToString() ?? string.Empty;
                if (!nm.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                var d = Vector3.Distance(lp.Position, o.Position);
                if (d > maxDist) continue;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }
        catch { return null; }
    }

    private IGameObject? CurrentTargetObj()
    {
        try
        {
            var lp = this.objectTable.LocalPlayer;
            if (lp is null) return null;
            var tid = lp.TargetObjectId;
            if (tid == 0) return null;
            return this.objectTable.FirstOrDefault(o => o is { } go && go.EntityId == tid);
        }
        catch { return null; }
    }

    private bool TargetHasStatus(string name)
    {
        try
        {
            var t = this.CurrentTargetObj();
            if (t is not IBattleChara ch) return false;
            var st = ch.StatusList;
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            if (sheet is null) return false;
            for (var i = 0; i < st.Length; i++)
            {
                var s = st[i];
                if (s is null || s.StatusId == 0) continue;
                var row = sheet.GetRow(s.StatusId);
                if (row.RowId == 0) continue;
                if (row.Name.ToString().Contains(name)) return true;
            }
        }
        catch { /* 读不到就当没有 */ }
        return false;
    }

    /// <summary>飞到触发点并落地（不在空中、水平距离 < 16 亚尔）后执行 then，超时 90s 放弃。</summary>
    /// <summary>人是否倒地（昏迷状态或血量 0）。</summary>
    private bool Dead()
    {
        try
        {
            if (this.condition[ConditionFlag.Unconscious]) return true;
            var p = this.objectTable.LocalPlayer;
            return p is not null && p.CurrentHp == 0;
        }
        catch { return false; }
    }

    /// <summary>等落地再执行 then。飞行坐骑上直接下马会往下掉，坠落中放技能会被判「正在坠落」而失败，
    /// 所以下马之后先等 InFlight / Jumping 结束（最多 4 秒）再动手。</summary>
    private void GroundedThen(System.Action then)
    {
        var tries = 0;
        void Tick()
        {
            var air = false;
            try { air = this.condition[ConditionFlag.InFlight] || this.condition[ConditionFlag.Jumping]; }
            catch { /* 读不到状态就当已落地 */ }

            if (!air || ++tries > 20)
            {
                then();
                return;
            }

            this.Sched(Tick, TimeSpan.FromMilliseconds(200));
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(150));
    }

    /// <summary>倒地后自动点「是否返回大水晶？」的「是」。
    /// 只在昏迷期间点，避免误点到别的确认框；重新站起来且不在过图就算完成。</summary>
    private void ConfirmReturnThen(string map, string huntCn, System.Action then)
    {
        var wasDead = false;
        var left = 240;   // 60 秒
        void Tick()
        {
            try
            {
                if (this.Dead())
                {
                    if (!wasDead) this.Print($"[{huntCn}] 已倒地，等「返回大水晶」弹窗");
                    wasDead = true;
                    this.ClickYesNo("SelectYesno");
                }
                else if (wasDead && !this.condition[ConditionFlag.BetweenAreas])
                {
                    if (!this.InMap(map))
                    {
                        // 返回点在别的图也一样往下走：不再要求玩家把「返回点」设到本图大水晶，
                        // 直接传本图大水晶落地，再接原来的收尾（进退副本清意志薄弱 → 下一个绿点）。
                        var ae = this.NearestAetheryte(map, 0f, 0f, huntCn);
                        if (ae is { } a)
                        {
                            this.Print($"[{huntCn}] 复活后不在「{map}」——不用改返回点，直接传本图大水晶，继续下一个绿点");
                            this.Say("/pdrtelepo " + a.Name);
                            this.WaitUntilOr(
                                () => this.InMap(map) && !this.condition[ConditionFlag.BetweenAreas],
                                () => { this.Print($"[{huntCn}] 已传回「{map}」大水晶，继续下一个绿点"); then(); },
                                () => { this.Print($"[{huntCn}] 传回大水晶没确认落地，仍继续下一个绿点"); then(); },
                                120, 500);
                        }
                        else
                        {
                            this.Print($"[{huntCn}] 复活后不在「{map}」，本图也没找到大水晶，直接继续下一个绿点");
                            then();
                        }
                        return;
                    }

                    this.Print($"[{huntCn}] 已在本图复活，继续下一个绿点");
                    then();
                    return;
                }
            }
            catch { /* 读状态失败就下一轮再看 */ }

            if (--left > 0) this.Sched(Tick, TimeSpan.FromMilliseconds(250));
            else { this.Print($"[{huntCn}] 等复活超时，继续下一个点"); then(); }
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>点一个确认框的「是」（0 号按钮）。没弹窗就什么都不做。</summary>
    private unsafe void ClickYesNo(string addonName)
    {
        try
        {
            var addon = this.gameGui.GetAddonByName<AddonSelectYesno>(addonName, 1);
            if (addon == null || !addon->IsVisible) return;
            addon->FireCallbackInt(0);
        }
        catch { /* ignore */ }
    }

    /// <summary>倒下之后的统一收尾：自动点「返回大水晶」→ 复活 → 进退副本一次（清意志薄弱）。
    /// 多智兽和阿姆斯特朗都是自爆致死，只是判定条件不同，后面这段完全一样。</summary>
    private void AfterDeathChain(string map, string huntCn, System.Action next) =>
        this.ConfirmReturnThen(map, huntCn, () => this.WaitDutyResetThen(next, map, huntCn));

    /// <summary>复活后进退副本一次，清掉自爆 / 被击倒留下的「意志薄弱」。
    /// 直接用 DailyRoutines 的 /pdr joinexitduty（进退一步到位），发完等它真的走完这一轮再继续。
    /// 排队 + 读条 + 两次过图实际要几十秒，所以判定放宽：每 0.5 秒看一次状态，
    /// 必须「进过副本」→「回到野外且连续安静 6 秒」才算完成，总时限 8 分钟。
    /// 早前是 0.25 秒轮询、安静 2 秒就算完，出本后地图还在加载就被当成完成，
    /// 下一段插旗寻路会发在加载中，直接丢命令。</summary>
    private void WaitDutyResetThen(System.Action then, string map = "", string huntCn = "")
    {
        this.Print("[意志薄弱] /pdr joinexitduty：进退副本一次");
        this.Say("/pdr joinexitduty");

        var ticks = 0;          // 每格 0.5 秒
        var sawDuty = false;    // 真的进过副本
        var sawBusy = false;    // 见到过「在副本里」或「过图中」
        var quiet = 0;          // 出本后连续安静的格数
        var warned = false;     // 催过一次没有
        void Tick()
        {
            var duty = false;
            var busy = false;
            try
            {
                duty = this.condition[ConditionFlag.BoundByDuty];
                busy = duty || this.condition[ConditionFlag.BetweenAreas];
            }
            catch { /* 读状态失败就下一轮再看 */ }

            if (duty) sawDuty = true;
            if (busy)
            {
                if (!sawBusy) this.Print("[意志薄弱] 已进本，等它退出");
                sawBusy = true;
                quiet = 0;
            }
            else if (sawBusy)
            {
                quiet++;
                if (quiet >= 12)   // 安静 6 秒 = 过图真结束、地图也加载完了
                {
                    this.Print("[意志薄弱] 已出本，继续");
                    this.ResumeAfterDutyThen(map, huntCn, then);
                    return;
                }
            }

            ticks++;

            if (!sawBusy && ticks == 120 && !warned)
            {
                warned = true;
                this.Print("[意志薄弱] 60 秒还没进本——确认 DailyRoutines 的「自动进退副本」模块是开着的");
            }

            // 90 秒连副本都没进去，说明这一步根本没跑起来，别在这儿干等满 8 分钟
            if (!sawBusy && ticks > 180)
            {
                this.Print("[意志薄弱] 90 秒还没进本，这次进退副本跳过");
                this.ResumeAfterDutyThen(map, huntCn, then);
                return;
            }

            if (ticks > 960)   // 8 分钟还没走完就先往下走，别把整条路线卡死
            {
                this.Print(sawDuty
                    ? "[意志薄弱] 8 分钟还没退出副本，先继续下一个绿点"
                    : "[意志薄弱] 8 分钟没等到进退副本完成，先继续下一个绿点");
                this.ResumeAfterDutyThen(map, huntCn, then);
                return;
            }

            this.Sched(Tick, TimeSpan.FromMilliseconds(500));
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(800));
    }

    /// <summary>进退副本出来后，先确认人真的回到野外目标图、能上坐骑，再接着巡点自爆。
    /// 以前出本判定只看「安静 6 秒」就 then()，这时常还在过图加载 / 寻路网格重建中：
    /// MountUpThen 会干等 12 秒后按步行发 flyflag，看起来就是「退本后不下来、不去触发点」。
    /// 不在目标图就直接传本图大水晶（不用改返回点）；在本图则不额外传，交给 Step 选最近水晶，
    /// 免得连传两次白等一轮读条。</summary>
    private void ResumeAfterDutyThen(string map, string huntCn, System.Action then)
    {
        if (string.IsNullOrWhiteSpace(map)) { then(); return; }

        var ticks = 0;
        var teleported = false;
        void Tick()
        {
            var inMap = false;
            var busy = false;
            var ready = false;
            try
            {
                inMap = this.InMap(map);
                busy = this.condition[ConditionFlag.BetweenAreas] ||
                       this.condition[ConditionFlag.BoundByDuty];
                // Mounted() 时 CanMountNow() 会返回 false，所以「已经骑着」也算就绪
                ready = !busy && !this.Dead() && (this.Mounted() || this.CanMountNow());
            }
            catch { /* 读状态失败就下一轮再看 */ }

            if (inMap && ready)
            {
                this.Print($"[{huntCn}] 出本后已在本图落地，马上飞下一个触发点");
                then();
                return;
            }

            // 退本后人落在别的图（返回点在别处）：直接传本图大水晶，不要求改返回点。
            if (!teleported && !inMap && !busy && ticks >= 4)
            {
                var ae = this.NearestAetheryte(map, 0f, 0f, huntCn);
                if (ae is { } a)
                {
                    this.Print($"[{huntCn}] 退本后不在「{map}」，直接传本图大水晶（不用改返回点）");
                    this.Say("/pdrtelepo " + a.Name);
                    teleported = true;
                }
            }

            if (++ticks > 60)   // 30 秒兜底，别把整条路线卡死
            {
                this.Print($"[{huntCn}] 退本后没等到完全就绪，仍继续下一个触发点");
                then();
                return;
            }
            this.Sched(Tick, TimeSpan.FromMilliseconds(500));
        }
        this.Sched(Tick, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>玩家是否已经站在某个 map 坐标附近（用 WorldToMap 换算，误差按 map 单位算）。</summary>
    /// <summary>玩家到某个插旗坐标的直线距离，单位是亚尔（yalm）。
    /// 注意 1 个地图坐标 ≠ 1 亚尔：地图坐标 = 世界坐标 × 2 / SizeFactor，
    /// 也就是 SizeFactor=100 的图 1 个地图坐标 = 50 亚尔，整张图横跨 2048 亚尔。
    /// 早前这里直接拿地图坐标差当距离、容差给了 4，等于 200 亚尔——人还在天上飞就被判到点，
    /// 技能提前丢在半空里，落到点上反而什么都不发。所以统一换算成亚尔再比较。</summary>
    private float YalmsTo(uint mapId, float x, float y)
    {
        try
        {
            if (this.PlayerMapCoords(mapId) is not { } pc) return float.MaxValue;
            var row = this.dataManager.GetExcelSheet<Map>()?.GetRow(mapId) ?? default;
            if (row.RowId == 0) return float.MaxValue;
            return YalmsBetween(row.SizeFactor, pc.X, pc.Y, x, y);
        }
        catch { return float.MaxValue; }
    }

    /// <summary>玩家当前所在地图坐标（与插旗用的 (x, y) 同一制式）；不在本图 / 读不到返回 null。</summary>
    private (float X, float Y)? PlayerMapCoords(uint mapId)
    {
        try
        {
            var p = this.objectTable.LocalPlayer;
            if (p is null) return null;
            var row = this.dataManager.GetExcelSheet<Map>()?.GetRow(mapId) ?? default;
            if (row.RowId == 0) return null;
            // 世界坐标 → 地图坐标（与插旗用的 (x, y) 同一制式）
            return (
                (float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(p.Position.X, row.SizeFactor, row.OffsetX),
                (float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(p.Position.Z, row.SizeFactor, row.OffsetY));
        }
        catch { return null; }
    }

    /// <summary>两个地图坐标之间的直线距离（亚尔）：1 地图坐标 = SizeFactor/2 亚尔。</summary>
    private static float YalmsBetween(float sizeFactor, float x1, float y1, float x2, float y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy) * sizeFactor / 2f;
    }

    /// <summary>本图里玩家已解锁的大水晶，带地图坐标。
    /// 坐标来源是 MapMarker 子行表（DataType=3 就是大水晶图标，DataKey 指回 Aetheryte 行）：
    /// 那里给的是 2048×2048 的地图贴图坐标，先按 SizeFactor 还原成世界坐标，
    /// 再用和寻路同一套 ConvertWorldCoordXZToMapCoord 换算成插旗用的地图坐标，两边才可比。
    /// （Aetheryte.Level 那条路走不通：本版 Lumina 里它的 RowRef 是失效的，取 .Value 直接抛异常。）
    /// 名字取 PlaceName——/pdrtelepo 认的就是这个名字。</summary>
    private List<(string Name, float X, float Y)> AetherytesOnMap(Map mapRow)
    {
        var list = new List<(string, float, float)>();
        try
        {
            var aet = this.dataManager.GetExcelSheet<Aetheryte>();
            var markers = this.dataManager.GetSubrowExcelSheet<MapMarker>();
            if (aet is null || markers is null) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unit = mapRow.SizeFactor / 100f;   // 贴图坐标 → 世界坐标的倍率（SizeFactor=100 时 1:1）
            foreach (var group in markers)
            {
                foreach (var m in group)
                {
                    if (m.DataType != 3 || m.DataKey.RowId == 0) continue;   // 3 = 大水晶图标
                    Aetheryte row;
                    try { row = aet.GetRow(m.DataKey.RowId); }
                    catch { continue; }
                    if (row.RowId == 0 || !row.IsAetheryte || row.Map.RowId != mapRow.RowId) continue;
                    var name = SafePlaceNameFrom(row);
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;   // 同名水晶只算一次
                    if (!this.AetheryteUnlocked(row.RowId)) continue;

                    var wx = (m.X - 1024f) * unit;
                    var wz = (m.Y - 1024f) * unit;
                    list.Add((name,
                        (float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(wx, mapRow.SizeFactor, mapRow.OffsetX),
                        (float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(wz, mapRow.SizeFactor, mapRow.OffsetY)));
                }
            }
        }
        catch (Exception ex) { this.log.Debug(ex, "[SrankFlag] 大水晶坐标读取失败"); }
        return list;
    }

    /// <summary>挑一座「离这些绿点最近」的大水晶：触发点一般围着水晶分布，
    /// 先落到离点位最近的那座，能省掉落地后横穿半张图。</summary>
    private AetheryteHit? AetheryteForRoute(string map, List<Pt> pts)
    {
        try
        {
            if (!this.TryResolveZone(map, out _, out var mapId, out _)) return null;
            var row = this.dataManager.GetExcelSheet<Map>()?.GetRow(mapId) ?? default;
            if (row.RowId == 0) return null;
            AetheryteHit? best = null;
            foreach (var a in this.AetherytesOnMap(row))
            {
                var d = float.MaxValue;
                foreach (var p in pts) d = MathF.Min(d, YalmsBetween(row.SizeFactor, a.X, a.Y, p.X, p.Y));
                if (best is null || d < best.Value.Dist) best = new AetheryteHit(a.Name, a.X, a.Y, d);
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>这座大水晶解没解锁（没解锁的传不过去，白白浪费一次读条）。读不到状态时当已解锁。</summary>
    private unsafe bool AetheryteUnlocked(uint aetheryteId)
    {
        try
        {
            var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (ui == null) return true;
            return ui->IsAetheryteUnlocked(aetheryteId);
        }
        catch { return true; }
    }

    /// <summary>等角色到达目标坐标点。
    /// 普通怪：仍用「快到（earlyYalms）就停寻路动手」+「停稳才算到」两套；
    /// 严格怪（strictArrive，多智兽 / 阿姆斯特朗）：只用严格的坐标准确判定——
    /// 必须「人实打实落在触发点上（已落地）」才回调，飞行掠过 / 半路 / 还差很远都绝不触发，
    /// 杜绝「快到时间就在半路自爆」。落稳但略偏（点位压在障碍物里）时用 2× 容差兜底放行。</summary>
    private void WaitArrive(uint mapId, float x, float y, System.Action then, System.Action? giveUp = null,
        float earlyYalms = 0f, bool strictArrive = false, string label = "")
    {
        var left = 1200;                       // 1200 × 150ms ≈ 3 分钟
        var still = 0;
        var hasLast = false;
        var last = System.Numerics.Vector3.Zero;
        var retries = 0;
        void Tick()
        {
            var dist = this.YalmsTo(mapId, x, y);

            // 每帧追踪玩家真实位置（Vector3），更新「停稳计数」：只在能读到位置时累加
            var canTrack = false;
            try
            {
                var p = this.objectTable.LocalPlayer;
                if (p is not null)
                {
                    var pos = p.Position;
                    if (hasLast && System.Numerics.Vector3.Distance(pos, last) < 0.4f) still++;
                    else still = 0;
                    last = pos;
                    hasLast = true;
                    canTrack = true;
                }
            }
            catch { /* 读位置失败就继续等 */ }

            // ── 多智兽 / 阿姆斯特朗：严格坐标判定，绝不提前自爆 ──
            if (strictArrive)
            {
                // 飞行中（坐骑 / 腾空）即使水平距离≈0 也不算——必须落地。
                var grounded = !this.Mounted() && !this.condition[ConditionFlag.InFlight];
                if (dist <= StrictArriveYalms && grounded)
                {
                    this.Print($"[{label}] 已严格到达触发点（{dist:F1} 亚尔，已落地），立即停寻路并自爆");
                    this.StopNav();
                    then();                       // 立即触发，不加 500ms 等多余延迟
                    return;
                }
                // 落稳但略偏（点位在障碍物里、vnav 只能落到附近）：差 ≤ 2× 容差且已停稳也放行
                if (canTrack && still >= 20 && dist <= StrictArriveYalms * 2f)
                {
                    this.Print($"[{label}] 已落在触发点附近（{dist:F1} 亚尔，落稳），容错放行自爆");
                    this.StopNav();
                    then();
                    return;
                }
                // 还没到 / 还在飞：继续等，绝不在此自爆
                if (--left <= 0) { giveUp?.Invoke(); return; }
                this.Sched(Tick, TimeSpan.FromMilliseconds(150));
                return;
            }

            // 快到触发点就动手：先停寻路（别飞过头 / 绕圈），再让 AtGreenPoint 下坐骑做动作。
            if (earlyYalms > 0f && dist <= earlyYalms)
            {
                this.Print($"还差 {dist:F0} 亚尔，快到触发点：停止寻路，下坐骑做触发动作");
                this.StopNav();
                this.Sched(then, TimeSpan.FromMilliseconds(500));
                return;
            }

            // 只有「人停稳」才算到点（still 已在每帧开头累计，3 秒没位移）。
            // 高速掠过点位、或者被地形卡着还在往前蹭时 still 不会累加，不会误判。
            if (canTrack && still >= 20)   // 3 秒没位移
            {
                if (dist <= 12f)
                {
                    this.Print($"已在点上停稳（{dist:F0} 亚尔），动手");
                    then();
                    return;
                }

                // 卡住但已经很近：S 刷新后 20+ 亚尔内技能都够得着，
                // 为最后十几亚尔卡 3 分钟再跳过整条路不值——直接算到点。
                if (dist <= 25f)
                {
                    this.Print($"还差 {dist:F0} 亚尔停稳——够近了，直接算到点");
                    then();
                    return;
                }

                // 停住了却还差一截 = 卡在半路。绝不下坐骑，补发 flyflag 接着飞；
                // 连发三次都不动才放弃这个点。
                still = 0;
                if (retries < 2)
                {
                    retries++;
                    this.Print($"寻路卡住了（还差 {dist:F0} 亚尔），继续飞，不下坐骑");
                    this.Say("/vnav flyflag");
                }
                else
                {
                    this.Print($"这个点没能飞到（还差 {dist:F0} 亚尔），跳过");
                    giveUp?.Invoke();
                    return;
                }
            }

            if (--left <= 0)
            {
                giveUp?.Invoke();
                return;
            }

            this.Sched(Tick, TimeSpan.FromMilliseconds(150));
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(120));
    }

    /// <summary>玩家当前所在的地图坐标（与插旗用的 (x, y) 同一制式）；取不到返回 (0,0)。</summary>
    private (float X, float Y) MapPos(uint mapId)
    {
        try
        {
            var p = this.objectTable.LocalPlayer;
            var maps = this.dataManager.GetExcelSheet<Map>();
            if (p is null || maps is null) return (0f, 0f);
            var row = maps.GetRow(mapId);
            if (row.RowId == 0) return (0f, 0f);
            return ((float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(p.Position.X, row.SizeFactor, row.OffsetX),
                    (float)Dalamud.Utility.MapUtil.ConvertWorldCoordXZToMapCoord(p.Position.Z, row.SizeFactor, row.OffsetY));
        }
        catch { return (0f, 0f); }
    }

    /// <summary>最基本的前往：只跨服 + 传这张图的大水晶，不需要触发点坐标。
    /// 给没有触发点数据的 S（俄菲翁尼厄斯、戾虫那几只）兜底用。</summary>
    private GotoResult GotoAetheryte(string map, string world, string huntCn = "")
    {
        this.CaptureWindowStateBeforeTeleport();
        this.Resume();
        var ae = this.NearestAetheryte(map, 0f, 0f, huntCn);
        var needTravel = !string.IsNullOrWhiteSpace(world) &&
                         !string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase);
        var alreadyIn = this.InMap(map);

        void Arrived() => this.WaitCorrectWorldThen(world, huntCn, () => this.RunPrepThen(huntCn, () =>
        {
            // 牛头黑神：传到大水晶后自动自爆（概率触发，循环到出怪为止）
            // aetheryteBombLimit > 0 时只爆这么多次（自动前往用），0 = 一直爆到手动停止
            if (huntCn == "牛头黑神")
            {
                var limit = this.aetheryteBombLimit;
                var done = 0;
                System.Action loop = null;
                loop = () =>
                {
                    if (limit > 0 && done >= limit)
                    {
                        // 爆完这么多次就是「触发动作做完了」：自动前往据此只再等 5 秒提示
                        this.autoRouteDoneAt = DateTime.UtcNow;
                        this.Print($"[牛头黑神] 已自爆 {limit} 次没出怪，先停手（概率触发，可以稍后重试）");
                        return;
                    }
                    done++;
                    this.SelfDestructThen("牛头黑神", "大水晶", map, loop, loop);
                };
                loop();
            }
        }));

        void Teleport()
        {
            if (ae is { } a) this.Say("/pdrtelepo " + a.Name);
            // 已经在这张图时给传送 3 秒生效时间，别在马还在读条时就换职业
            if (alreadyIn) this.Sched(Arrived, TimeSpan.FromMilliseconds(3000));
            // 落地等不到也别静默丢掉 Arrived：以前用 WaitUntil，120 秒超时后
            // 「切职业 → 上坐骑 → 巡绿点 → 自爆」整条链无声无息断掉，看着像没做触发。
            else this.WaitUntilOr(() => this.InMap(map), Arrived, () =>
            {
                this.Print($"[前往] 等了 120 秒还没确认落地到「{map}」，仍继续触发动作");
                Arrived();
            }, 240, 500);
        }

        if (needTravel)
        {
            this.SendWorldTravel(world);
            this.WaitUntilOr(
                () => !this.condition[ConditionFlag.BetweenAreas] &&
                      string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase),
                Teleport,
                () => this.Print($"[跨服] 240 秒还没到 {world}，这次跨服放弃"),
                240, 500);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(world)) this.Print("已在目标服务器 " + world + "，直接传大水晶");
            this.Sched(Teleport, TimeSpan.FromMilliseconds(250));
        }

        return new GotoResult(
            ae is not null,
            ae is { } a2
                ? (needTravel ? $"已跨服到 {world}，" : "") + $"落地即传大水晶：{a2.Name}"
                : $"在「{map}」找不到大水晶");
    }

    /// <summary>触发前的服务器闸门：确认人真的在目标服、且不在过图中，才允许做触发动作。
    /// 跨服读条时 CurrentWorldName 可能已变成目标服，旧服的同名地图也会让 InMap 提前成立；
    /// 这时切职业 / 做牛排 / 自爆会全打在错的服上。等不到就放弃这只，绝不在错服触发。</summary>
    private bool WorldReady(string world)
    {
        if (string.IsNullOrWhiteSpace(world)) return true;
        try
        {
            return !this.condition[ConditionFlag.BetweenAreas] &&
                   string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void WaitCorrectWorldThen(string world, string huntCn, System.Action then)
    {
        if (string.IsNullOrWhiteSpace(world) || this.WorldReady(world)) { then(); return; }

        this.Print($"[{huntCn}] 当前还不是 {world}（现在是 {this.CurrentWorldName()}），等落地确认后再开始触发");
        this.WaitUntilOr(() => this.WorldReady(world),
            () => { this.Print($"[{huntCn}] 已确认在 {world}，开始触发"); then(); },
            () =>
            {
                this.Print($"[{huntCn}] 90 秒没确认到 {world}，这只跳过（不在错的服上触发）");
                this.autoSkipped.Add((huntCn, "跨服没确认", $"90 秒没确认到 {world}"));
                this.AutoStep();
            },
            180, 500);
    }

    // ─────────────────────── 落地后按触发条件做准备：换职业 / 召宠物 ───────────────────────

    private enum PrepKind { Pet, Cook, Blu, Garden, Warrior }

    private readonly record struct HuntPrep(PrepKind Kind, string Arg);

    private const uint JobCulinarian = 15;  // 烹调师
    private const uint JobBlueMage = 36;    // 青魔法师
    private const uint JobBotanist = 17;    // 园艺工
    private const uint JobWarrior = 21;     // 战士

    /* 到图里大水晶后自动做的事。表里没有的 S 落地什么都不发，保持干净。
       数据来自灰机 Wiki 的 S 怪触发方法：
         - 宠物踩点：阿格拉俄珀(朱孔雀) / 布弗鲁(小巨魔) / 狭缝(小异亚) / 伊努索奇(蓝闪蝶)
         - 烹调师：先驱勇士 阿提卡斯（制作高品质犎牛牛排）
         - 青魔法师：多智兽(自爆) / 天气预报机器人(狂风暴雪) / 牛头黑神(传大水晶自爆)
         - 园艺工：得到宽恕的炫学 */
    private static readonly Dictionary<string, HuntPrep> HuntPreps = new(StringComparer.Ordinal)
    {
        ["阿格拉俄珀"] = new(PrepKind.Pet, "朱孔雀"),
        ["布弗鲁"] = new(PrepKind.Pet, "小巨魔"),
        ["狭缝"] = new(PrepKind.Pet, "小异亚"),
        ["伊努索奇"] = new(PrepKind.Pet, "蓝闪蝶"),
        ["先驱勇士 阿提卡斯"] = new(PrepKind.Cook, "犎牛牛排"),
        ["多智兽"] = new(PrepKind.Blu, "自爆"),
        ["阿姆斯特朗"] = new(PrepKind.Blu, "自爆"),
        ["天气预报机器人"] = new(PrepKind.Blu, "狂风暴雪"),
        ["牛头黑神"] = new(PrepKind.Blu, "自爆"),
        ["得到宽恕的炫学"] = new(PrepKind.Garden, "园艺工"),
        ["顾尼图"] = new(PrepKind.Warrior, "战士"),
    };

    /// <summary>界面上的小提示（「落地切烹调师」之类）；没有前置返回空串。</summary>
    private static string PrepHint(string huntCn) => HuntPreps.TryGetValue(huntCn, out var p)
        ? p.Kind switch
        {
            PrepKind.Cook => " · 落地切烹调师",
            PrepKind.Blu => " · 落地切青魔法师",
            PrepKind.Garden => " · 落地切园艺工·自动采集",
            PrepKind.Warrior => " · 落地切战士",
            _ => " · 落地召" + p.Arg,
        }
        : "";

    /// <summary>换职业、召宠物都得站在地上：骑着就先用「/mount（不带坐骑名）＝下坐骑」下来。</summary>
    private void DismountThen(System.Action then)
    {
        if (!this.Mounted())
        {
            then();
            return;
        }

        // 骑在坐骑上再按一次「随机坐骑」就是下马（和上马同一个通用技能，最稳的一条路）；
        // /mount 不带参数那条在部分状态下会被吃掉，留着当第二次重试。
        this.Say(DefaultMountCmd);
        var tries = 0;
        void Tick()
        {
            if (!this.Mounted())
            {
                then();
                return;
            }

            tries++;
            if (tries == 6) this.Say("/mount");                 // 0.9 秒还在马上，换不带参数的下马命令
            else if (tries == 12) this.Say(DefaultMountCmd);     // 1.8 秒，再按一次坐骑
            else if (tries >= 20)                                // 3 秒，别把整条路卡死
            {
                this.Print("[坐骑] 下坐骑没成功，先继续（还骑着的话技能会因为坐骑状态放不出来）");
                then();
                return;
            }

            this.Sched(Tick, TimeSpan.FromMilliseconds(150));
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(150));
    }

    /// <summary>落地大水晶后按 S 的触发条件做准备，做完再继续（骑坐骑 / 寻路）。</summary>
    private void RunPrepThen(string huntCn, System.Action then)
    {
        if (string.IsNullOrEmpty(huntCn) || !HuntPreps.TryGetValue(huntCn, out var prep))
        {
            then();
            return;
        }

        this.DismountThen(() =>
        {
            switch (prep.Kind)
            {
                case PrepKind.Cook:
                    this.Print($"[{huntCn}] 切烹调师：触发要制作{prep.Arg}");
                    this.SwitchJobThen(JobCulinarian, "烹调师", () =>
                        this.CraftStartThen(prep.Arg, $"[{huntCn}]", () =>
                        {
                            // Artisan 做完 HQ 后常常把「配方笔记（制作菜单）」留在屏幕上，
                            // 只关 Synthesis 一次关不掉；窗口开着会挡住后面跨服 / 传送 / 上坐骑，
                            // 表现为「牛排做完了却停在做菜界面，不去下一只 S」。
                            // 连试 4 轮把三个制作窗口都收掉，再继续；手动搓完也一样收。
                            var cont = then;
                            var rounds = 4;
                            var logged = false;
                            void CloseCraft()
                            {
                                var any = this.CloseAddonIfOpen("Synthesis")
                                        | this.CloseAddonIfOpen("SynthesisSimple")
                                        | this.CloseAddonIfOpen("RecipeNote");
                                if (any && !logged)
                                {
                                    logged = true;
                                    this.Print($"[{huntCn}] 已自动退出制作菜单");
                                }
                                if (--rounds > 0)
                                {
                                    this.Sched(CloseCraft, TimeSpan.FromMilliseconds(400));
                                    return;
                                }

                                // 牛排做好就是这只怪的触发动作做完：自动前往据此只再等 5 秒提示就换下一只，
                                // 不用干等到 200 秒的兜底超时（阿提卡斯没有绿点路线，别处不会写这个时间戳）。
                                this.autoRouteDoneAt = DateTime.UtcNow;
                                cont();
                            }
                            this.Sched(CloseCraft, TimeSpan.FromMilliseconds(600));
                        }));
                    break;

                case PrepKind.Blu:
                    this.Print($"[{huntCn}] 切青魔法师：触发要用{prep.Arg}");
                    this.SwitchJobThen(JobBlueMage, "青魔法师", then);
                    break;

                case PrepKind.Garden:
                    this.Print($"[{huntCn}] 切园艺工：交给 GatherBuddy 自动采「矮人棉」");
                    // 得到宽恕的炫学：全服共采矮人棉若干次（按次数不按数量，增加获得量也只算一次）。
                    // v1.1.70：插件自巡草场方案（XuanxueGatherThen）先搁置，改回调用 GatherBuddy Reborn
                    // 的自动采集——它自己按节点数据巡草场、开窗口、逐次采；插件只负责开 IPC 开关、
                    // 数入库次数、收尾关窗。上限沿用 300 次（11-08 实测 492 次才触发，旧版 50 次等于放弃）。
                    if (huntCn == "得到宽恕的炫学")
                        this.SwitchJobThen(JobBotanist, "园艺工",
                            () => this.GatherStrikesThen("矮人棉", 300, $"[{huntCn}]", then));
                    else
                        this.SwitchJobThen(JobBotanist, "园艺工", then);
                    break;

                case PrepKind.Warrior:
                    this.Print($"[{huntCn}] 切战士：触发要用战士");
                    this.SwitchJobThen(JobWarrior, "战士", then);
                    break;

                default:
                    this.PetSummonThen(huntCn, prep.Arg, then);
                    break;
            }
        });
    }

    private uint CurrentJobId()
    {
        try { return this.objectTable.LocalPlayer?.ClassJob.RowId ?? 0; }
        catch { return 0; }
    }

    /// <summary>换成指定职业：按职业 ID **实时查**套装序号（不写死 17，也不依赖套装名），
    /// 先发「/gs change 序号」；序号没切成再按名字补一次。两次都没确认到职业切换，
    /// 自动前往里就判定这只触发失败、直接去下一只（用户要求），不再继续发寻路 / 自爆命令。</summary>
    private void SwitchJobThen(uint jobId, string label, System.Action then)
    {
        if (this.CurrentJobId() == jobId)
        {
            then();
            return;
        }

        var idx = this.FindGearsetIndex(jobId);
        var byIndex = idx >= 0 ? "/gs change " + (idx + 1) : null;
        var byName = "/gs change " + label;
        var first = byIndex ?? byName;
        this.Say(first);
        this.log.Info($"[SrankFlag] 触发前置：{first}（目标 {label}，查到套装 " +
                     (idx >= 0 ? $"#{idx + 1}" : "未找到") + "）");

        // 第一段等 5 秒；没切成再换另一种写法补一次（序号映射不对 / 套装改过名都能覆盖）
        this.WaitUntilOr(() => this.CurrentJobId() == jobId, then, () =>
        {
            if (byIndex is null) { this.AutoPrepFail(label, first, then); return; }
            this.Say(byName);
            this.log.Info($"[SrankFlag] 触发前置：序号没切成，改按名字 {byName}");
            this.WaitUntilOr(() => this.CurrentJobId() == jobId, then,
                () => this.AutoPrepFail(label, first + " → " + byName, then), 12, 250);
        }, 20, 250);
    }

    /// <summary>换装两次都没切成：自动前往判负，交给监控链换下一只；手动触发仍继续动作。</summary>
    private void AutoPrepFail(string label, string cmds, System.Action then)
    {
        var msg = $"[{label}] 换装失败（已试 {cmds}）";
        if (this.autoRunning || this.autoPaused)
        {
            this.autoPrepFailed = true;
            this.Print(msg + "，判定触发失败，去下一只");
            return;   // 故意不调 then：不再上坐骑 / 巡点 / 自爆
        }
        this.Print(msg + "；手动模式仍继续触发动作");
        then();
    }

    private unsafe int FindGearsetIndex(uint jobId)
    {
        try
        {
            var gs = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (gs == null) return -1;
            for (var i = 0; i < 100; i++)
            {
                var e = gs->GetGearset(i);
                if (e == null) continue;
                if (e->ClassJob == jobId) return i;
            }
        }
        catch { /* ignore */ }
        return -1;
    }

    /// <summary>召唤指定名字的宠物（按 Companion 表反查 id，不写死 id）。</summary>
    private unsafe void PetSummonThen(string huntCn, string petName, System.Action then)
    {
        try
        {
            var id = this.FindMinionId(petName);
            if (id == 0)
            {
                this.Print($"[{huntCn}] 找不到宠物「{petName}」，跳过召唤");
                then();
                return;
            }

            var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (ui == null || !ui->IsCompanionUnlocked(id))
            {
                this.Print($"[{huntCn}] 宠物「{petName}」还没解锁，跳过召唤");
                then();
                return;
            }

            var am = ActionManager.Instance();
            if (am == null)
            {
                then();
                return;
            }

            if (am->GetActionStatus(ActionType.Companion, id) != 0)
            {
                this.Print($"[{huntCn}] 宠物「{petName}」现在召不出来（战斗中或冷却）");
                then();
                return;
            }

            am->UseAction(ActionType.Companion, id);
            this.Print($"[{huntCn}] 已召唤宠物「{petName}」");
            this.log.Info($"[SrankFlag] 触发前置：召唤宠物 {petName}(#{id})");
        }
        catch { /* ignore */ }

        // 留 1 秒让宠物出场，再继续上坐骑 / 寻路
        this.Sched(then, TimeSpan.FromMilliseconds(1000));
    }

    private uint FindMinionId(string name)
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Companion>();
            if (sheet is null) return 0;
            var want = Normalize(name);
            foreach (var row in sheet)
            {
                if (Normalize(row.Singular.ToString()) == want) return row.RowId;
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>每 intervalMs 检查一次 cond，成立后执行 then；最多试 tries 次（RunOnTick 派发，结束条件不阻塞游戏帧）。</summary>
    private void WaitUntil(Func<bool> cond, System.Action then, int tries = 120, int intervalMs = 500)
    {
        void Step(int left)
        {
            this.Sched(() =>
            {
                try
                {
                    if (cond())
                    {
                        then();
                        return;
                    }
                }
                catch { /* 条件里读游戏状态失败就当没满足 */ }
                if (left > 0) Step(left - 1);
            }, TimeSpan.FromMilliseconds(intervalMs));
        }
        Step(tries);
    }

    /// <summary>等 cond 成立后执行 then；tries 次都不成立就执行 giveUp（可为 null）。</summary>
    private void WaitUntilOr(Func<bool> cond, System.Action then, System.Action? giveUp, int tries = 120, int intervalMs = 500)
    {
        void Step(int left)
        {
            this.Sched(() =>
            {
                try
                {
                    if (cond())
                    {
                        then();
                        return;
                    }
                }
                catch { /* 读游戏状态失败就当没满足 */ }

                if (left > 0) Step(left - 1);
                else giveUp?.Invoke();
            }, TimeSpan.FromMilliseconds(intervalMs));
        }

        Step(tries);
    }

    /// <summary>施放技能并轮询重试：每 ~110ms 重新发一次命令、并判定是否真的生效，
    /// 直到 took() 为真（或约 3 秒预算耗尽，交后续死亡监控兜底）。
    /// 专治「下坐骑 / 落地锁把第一下发掉的技能静默吞掉」——反复重发直到 buff / 读条 / 倒地出现。</summary>
    private void CastThen(string cmd, Func<bool> took, System.Action after, string dbg)
    {
        var tries = 0;
        const int budget = 27;   // 27 × 110ms ≈ 3 秒
        void Step()
        {
            this.Print($"[触发] 尝试释放{dbg}（第 {tries + 1} 次）");
            this.Say(cmd);
            this.Sched(() =>
            {
                try { if (took()) { after(); return; } }
                catch { /* 读游戏状态失败：当作还没生效，下一轮继续重试 */ }

                if (++tries >= budget)
                {
                    this.Print($"[触发] {dbg} 3 秒内未确认生效，继续（交给死亡监控兜底）");
                    after();
                    return;
                }
                Step();
            }, TimeSpan.FromMilliseconds(110));
        }
        Step();
    }

    /// <summary>寻路默认坐骑：通用技能「随机坐骑」——直接在聊天框敲这个命令就上马，
    /// 不依赖坐骑名（国服「专属陆行鸟」这类名字容易对不上，召不出来还没提示）。</summary>
    private const string DefaultMountCmd = "/gaction 随机坐骑";

    /// <summary>随机坐骑发不出去时的兜底：按名字召专属陆行鸟。
    /// 注意：/mount 不带坐骑名在游戏里是「下坐骑」，裸发等于下马，所以必须带参数。 </summary>
    private const string FallbackMountCmd = "/mount 专属陆行鸟";

    private bool Mounted()
    {
        try { return this.condition[ConditionFlag.Mounted] || this.condition[ConditionFlag.RidingPillion]; }
        catch { return false; }
    }

    private bool Mounting()
    {
        try
        {
            return this.condition[ConditionFlag.Mounting] ||
                   this.condition[ConditionFlag.Mounting71] ||
                   this.condition[ConditionFlag.MountOrOrnamentTransition];
        }
        catch { return false; }
    }

    /// <summary>现在能不能上坐骑：过图、剧情、战斗、正在召唤中都不行。
    /// 传完大水晶有一段淡入，这时候发 /mount 会被游戏静默吞掉——
    /// 之前「点了前往绿点却走路过去」就是三次重试全撞在这段里。</summary>
    private bool CanMountNow()
    {
        try
        {
            if (this.Mounted() || this.Mounting()) return false;
            if (this.condition[ConditionFlag.BetweenAreas]) return false;
            if (this.condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
            if (this.condition[ConditionFlag.WatchingCutscene]) return false;
            if (this.condition[ConditionFlag.InCombat]) return false;
            return true;
        }
        catch { return true; }
    }

    /// <summary>先上坐骑再干活：vnav 的飞行寻路必须骑着坐骑，走路过去会慢一大截。
    /// 命令用聊天框那套「/gaction 随机坐骑」，比重试 /mount 指定坐骑名可靠。
    /// 每 300ms 探一次：只有真能骑的时候才发命令（过图 / 剧情 / 战斗中先等着），
    /// 2.4 秒还没骑上就补发，最多 3 次，12 秒仍骑不上就说明这地方骑不了，
    /// 说一声后照常继续，不卡流程。</summary>
    private void MountUpThen(System.Action then)
    {
        this.Resume();          // 上坐骑也是「一次动作」，停止之后再来一次要能跑
        if (this.Mounted())
        {
            then();
            return;
        }

        const int stepMs = 300;
        const int budgetMs = 12000;
        var elapsed = 0;
        var nextSend = 0;
        var sent = 0;

        void Tick()
        {
            try
            {
                if (this.Mounted())
                {
                    this.Print("[坐骑] 已上坐骑，继续寻路");
                    // 上马动作没做完就发 /vnav 会被吃掉，缓半秒再走
                    this.Sched(then, TimeSpan.FromMilliseconds(500));
                    return;
                }

                if (elapsed >= budgetMs)
                {
                    this.Print("[坐骑] 12 秒没召唤成功（采集硬直 / 副本 / 剧情 / 战斗中都骑不上），改用 /vnav moveflag 地面寻路");
                    then();
                    return;
                }

                if (elapsed >= nextSend && this.CanMountNow())
                {
                    sent++;
                    var cmd = sent < 3 ? DefaultMountCmd : FallbackMountCmd;
                    this.Say(cmd);
                    this.Print(sent == 1 ? $"[坐骑] {cmd}" : $"[坐骑] 还没骑上，重试第 {sent} 次（{cmd}）");
                    nextSend = elapsed + 3500;   // 上坐骑读条约 2 秒，间隔留够，不然会把读条自己打断
                }

                elapsed += stepMs;
                this.Sched(Tick, TimeSpan.FromMilliseconds(stepMs));
            }
            catch
            {
                then();   // 读游戏状态出错也别把流程卡死
            }
        }

        this.Sched(Tick, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>当前是否已经在目标地图（按 territory 名比对）。</summary>
    private bool InMap(string map)
    {
        try
        {
            if (this.condition[ConditionFlag.BetweenAreas]) return false;
            return Normalize(this.TerritoryName(this.clientState.TerritoryType)) == Normalize(map);
        }
        catch { return false; }
    }

    private FlagResult TeleportNearest(string map, float x, float y)
    {
        var ae = this.NearestAetheryte(map, x, y);
        if (ae is not { } a) return new FlagResult(false, $"在「{map}」找不到大水晶");
        this.Say("/pdrtelepo " + a.Name);
        return new FlagResult(true, $"已传最近大水晶：{a.Name}");
    }

    private FlagResult WorldTravel(string world)
    {
        if (string.IsNullOrWhiteSpace(world)) return new FlagResult(false, "服务器名不能为空");
        var cur = this.CurrentWorldName();
        if (string.Equals(cur, world, StringComparison.OrdinalIgnoreCase))
            return new FlagResult(true, "已经在 " + world + "，无需跨服");
        this.SendWorldTravel(world);
        return new FlagResult(true, "已发送跨服传送：" + world);
    }

    /// <summary>发跨服。只用 DailyRoutines 的「跨界传送指令」模块：/pdr worldtravel 服务器名
    /// （/pdr 的帮助里明确列出这条子命令，支持不区分大小写 / 部分匹配）。
    /// 不走 OmniToolbox 的 /omni worldtravel——那会打开它自己的跨界传送窗口，行为不一样。
    /// 重发时仍然是同一条命令，不换实现。</summary>
    private void SendWorldTravel(string world)
    {
        if (string.IsNullOrWhiteSpace(world)) return;
        this.Say("/pdr worldtravel " + world);
    }

    private FlagResult ChangeInstance(int line)
    {
        if (line <= 0) return new FlagResult(false, "分线编号要 > 0");
        this.Say("/pdr insc " + line);
        return new FlagResult(true, "已发送切分线：" + line + " 线");
    }

    /// <summary>找某地图的大水晶名（用于 /pdrtelepo）。
    /// 说明：本版 Lumina 的 Aetheryte 表无法直接读到世界坐标（Position/Scale 已被生成器改名），
    /// 所以默认取“该地图内第一个 IsAetheryte 的水晶名”；
    /// huntCn 命中 <see cref="HuntAetheryte"/> 时改取指定那座（例如夏劳尼荒野只传谢申内青磷泉）。</summary>
    private AetheryteHit? NearestAetheryte(string mapName, float x, float y, string huntCn = "")
    {
        var maps = this.dataManager.GetExcelSheet<Map>();
        if (maps is null) return null;
        uint mapId = 0;
        foreach (var row in maps)
        {
            if (Normalize(SafeMapName(row)) != Normalize(mapName)) continue;
            mapId = row.RowId; break;
        }
        if (mapId == 0) return null;

        var aet = this.dataManager.GetExcelSheet<Aetheryte>();
        if (aet is null) return null;

        var prefer = huntCn.Length > 0 && HuntAetheryte.TryGetValue(huntCn, out var frag) ? frag : null;
        // 片段可以写多个，用 | 分隔（客户端译名差一两个字也能命中）
        var wants = prefer is null
            ? Array.Empty<string>()
            : prefer.Split('|').Select(Normalize).Where(x => x.Length > 0).ToArray();
        AetheryteHit? fallback = null;
        foreach (var row in aet)
        {
            if (row.Map.RowId != mapId) continue;
            if (!row.IsAetheryte) continue;
            var name = SafePlaceNameFrom(row);
            if (string.IsNullOrEmpty(name)) continue;
            var hit = new AetheryteHit(name, x, y, 0);
            if (wants.Length == 0) return hit;                                  // 没指定：第一座
            var n = Normalize(name);
            if (wants.Any(w => n.Contains(w, StringComparison.Ordinal))) return hit; // 指定且命中
            fallback ??= hit;                                                   // 指定但图上没有：退第一座
        }
        return fallback;
    }

    private readonly record struct AetheryteHit(string Name, float X, float Y, double Dist);

    // ─────────────────────────── 自动丢弃触发条件道具 ───────────────────────────

    private readonly record struct DiscardResult(bool Ok, string Message);

    /// <summary>自动丢弃触发条件道具：从大堆里拆出「正好 Amount 个」的整组，然后整组一次性丢出去。
    /// 厄尔庇斯一次丢 5 个、亚克特尔树海一次丢 50 个、雷克兰德一次丢 1 个；绝不把整堆丢掉。</summary>
    private unsafe DiscardResult DiscardTrigger()
    {
        var terr = this.clientState.TerritoryType;
        if (!TriggerDiscardItems.TryGetValue(terr, out var info))
        {
            this.Print($"当前地图（territory {terr}）没有需要自动丢弃的触发道具。");
            return new DiscardResult(false, $"当前地图不支持自动丢弃（territory {terr}）");
        }
        if (this.condition[ConditionFlag.BetweenAreas] || this.condition[ConditionFlag.BetweenAreas51])
            return new DiscardResult(false, "正在过图，等落地再试");

        var mgr = InventoryManager.Instance();
        if (mgr is null) return new DiscardResult(false, "背包系统未就绪");

        var itemId = info.ItemId;
        var amount = info.Amount;

        // 找一个数量 >= amount 的格子
        var stacks = BuildStacks(mgr, itemId);
        var target = stacks.FirstOrDefault(s => s.Quantity >= amount);
        if (target.Quantity < amount)
        {
            this.Print($"背包里「{itemId}」不够 {amount} 个，无法按组丢弃。");
            return new DiscardResult(false, $"数量不足（需要 {amount} 个）");
        }

        // 数量刚好的整组 → 直接整组丢；大堆 → 先拆出正好 amount 个
        if (target.Quantity > amount)
        {
            if (mgr->SplitItem(target.Type, (ushort)target.Slot, amount) != 0)
            {
                this.Print("拆分失败（背包可能满了）。");
                return new DiscardResult(false, "拆分失败（背包可能满了）");
            }
            var split = BuildStacks(mgr, itemId).FirstOrDefault(s => s.Quantity == amount);
            if (split.Quantity != amount)
                return new DiscardResult(false, "拆分后没找到整组，请手动检查背包");
            target = split;
        }

        // 整组一次丢出去
        if (mgr->DiscardItem(target.Type, (ushort)target.Slot) != 0)
            return new DiscardResult(false, "丢弃失败");

        this.Print($"已整组丢弃「{itemId}」×{amount}。");
        return new DiscardResult(true, $"已整组丢弃 ×{amount}");
    }

    private unsafe List<(InventoryType Type, short Slot, int Quantity)> BuildStacks(InventoryManager* mgr, uint itemId)
    {
        var list = new List<(InventoryType, short, int)>();
        foreach (var container in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var c = mgr->GetInventoryContainer(container);
            if (c is null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                ref var item = ref c->Items[i];
                if (item.ItemId == itemId)
                    list.Add((item.GetInventoryType(), item.Slot, item.Quantity));
            }
        }
        return list;
    }

    // ─────────────────────────── 查小异亚（territory 960 的触发机制） ───────────────────────────

    private readonly record struct WeeEaResult(bool Ok, string Message, List<string> Names);

    private WeeEaResult QueryWeeEa()
    {
        var terr = this.clientState.TerritoryType;
        if (terr != 960)
            return new WeeEaResult(false, $"当前不在小异亚地图（territory {terr}，需在 960）", new List<string>());

        var names = new List<string>();
        var local = this.objectTable.LocalPlayer;
        if (local is null) return new WeeEaResult(false, "找不到本地玩家", names);
        var lp = local.Position;

        foreach (var obj in this.objectTable)
        {
            if (obj is null || obj.ObjectKind != ObjectKind.Companion) continue;
            if (obj.Name.TextValue != "小异亚") continue;
            var dx = obj.Position.X - lp.X;
            var dz = obj.Position.Z - lp.Z;
            if (Math.Sqrt(dx * dx + dz * dz) > 10) continue;
            var owner = this.objectTable.FirstOrDefault(o => o is IPlayerCharacter player && player.EntityId == obj.OwnerId);
            if (owner is IPlayerCharacter pc)
                names.Add($"{pc.Name.TextValue}@{pc.HomeWorld.Value.Name}");
        }
        return new WeeEaResult(true, $"找到 {names.Count} 个小异亚", names);
    }

    // ─────────────────────────── 自动寻路（vnavmesh-cn） ───────────────────────────

    /// <summary>开图插旗 + 让 vnavmesh-cn 寻路到旗子。
    /// 依赖 AtmoOmen/ffxiv_navmesh-cn 的 /vnav flyflag 命令（读到我们刚插的旗子就飞过去）。
    /// world 非空且不等于当前服时，先发 /pdr worldtravel 跨服，等落地（9s）再寻路。</summary>
    private FlagResult NavTo(string mapName, float x, float y, string world = "", string huntCn = "")
    {
        this.Resume();
        var fr = this.Flag(mapName, x, y);
        if (!fr.Ok) return fr;

        var crossing = false;
        if (!string.IsNullOrWhiteSpace(world))
        {
            var cur = this.CurrentWorldName();
            if (!string.Equals(cur, world, StringComparison.OrdinalIgnoreCase))
            {
                this.SendWorldTravel(world);
                crossing = true;
                this.Print("跨服目标 " + world + "，落地后自动寻路。");
            }
            else
            {
                this.Print("已在 " + world + "，无需跨服。");
            }
        }

        void StartNav() => this.RunPrepThen(huntCn, () => this.FlyFlagThen(() => { }));

        if (crossing)
        {
            // 跨服：等真正落地到目标服再动，不再死等 9 秒
            this.WaitUntil(
                () => !this.condition[ConditionFlag.BetweenAreas] &&
                      string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase),
                StartNav, 240, 500);
        }
        else
        {
            this.Sched(StartNav, TimeSpan.FromMilliseconds(1200));
        }

        return new FlagResult(true,
            $"{fr.Message}，{(crossing ? "跨服落地后" : "马上")}上坐骑 + 自动飞行寻路（/vnav flyflag）",
            fr.TerritoryId, fr.MapId);
    }

    private void StopNav() => this.Say("/vnav stop");

    /// <summary>vnavmesh-cn 的 Dalamud IPC：当前地图的寻路网格（navmesh）是否已经加载好。
    /// 刚落地 / 切地图时网格还在后台构建，这时发 /vnav flyflag 会被 vnav 静默忽略，
    /// 表现就是「插了旗、上了坐骑，但站着不动」。所以真正发 flyflag 之前要先等它就绪。</summary>
    private bool VnavReady()
    {
        try { return this.pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc(); }
        catch { return true; }   // vnavmesh 没装 / 接口版本对不上 → 不阻塞，照常发命令
    }

    private float VnavBuildProgress()
    {
        try { return this.pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress").InvokeFunc(); }
        catch { return 1f; }
    }

    /// <summary>等 vnavmesh 把当前地图的寻路网格加载完成，再执行 then（发 flyflag 等）。
    /// 最多等 NavReadyTimeout 秒；超时才继续并提示，避免无限挂起；停止时被 Sched 的 runGen 一并作废。</summary>
    private void WaitNavReadyThen(System.Action then, double timeoutSec = 120)
    {
        var start = DateTime.UtcNow;
        System.Action poll = null!;
        poll = () =>
        {
            if (this.VnavReady())
            {
                then();
                return;
            }
            var waited = (DateTime.UtcNow - start).TotalSeconds;
            if (waited >= timeoutSec)
            {
                this.Print("[寻路] 等 vnavmesh 加载超过 " + (int)timeoutSec + " 秒仍未就绪，仍尝试寻路" +
                           "（若一直不动请手动 /vnav reload）");
                then();
                return;
            }
            if ((int)waited % 5 == 0)
            {
                var p = this.VnavBuildProgress();
                this.Print("[寻路] 等待 vnavmesh 加载当前地图寻路网格…（" +
                           (p > 0 ? (p * 100).ToString("F0") + "%" : "加载中") + "）");
            }
            this.Sched(poll, TimeSpan.FromMilliseconds(1000));
        };
        poll();
    }

    /// <summary>上坐骑后发 /vnav flyflag，但先等 vnavmesh 把当前地图网格加载好，避免「插旗后站着不动」。</summary>
    private void FlyFlagThen(System.Action after)
    {
        this.WaitNavReadyThen(() => this.MountUpThen(() =>
        {
            // 没骑上坐骑时 flyflag 会被 vnav 静默忽略，人原地站着不动，最后被判「没飞到」。
            // 11-08 实测炫学第 2 轮连续 3 次坐骑失败 → 每个点空转 60 秒 → 4 分钟退出。
            // 没坐骑就改用地面寻路到旗标（vnavmesh 的 MoveFlagCommand / /vnav moveflag）。
            if (this.Mounted())
            {
                this.Say("/vnav flyflag");
            }
            else
            {
                this.Print("[寻路] 没坐骑，改用地面寻路 /vnav moveflag");
                this.Say("/vnav moveflag");
            }
            after();
        }));
    }

    // ─────────────────────────── 自动召唤随从（按地图） ───────────────────────────

    private void ClientState_OnLogin() => this.RefreshUnlockedMinions();

    /// <summary>登录后把映射表里已解锁的随从记下来，避免对着没解锁的随从空发技能。</summary>
    private unsafe void RefreshUnlockedMinions()
    {
        try
        {
            if (!this.clientState.IsLoggedIn) return;
            this.unlockedMinions.Clear();
            foreach (var kv in MinionMap)
                if (FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->IsCompanionUnlocked(kv.Value))
                    this.unlockedMinions.Add(kv.Value);
        }
        catch { /* ignore */ }
    }

    private void Framework_OnUpdate(IFramework framework)
    {
        // v1.2.9：窗口恢复调度放在业务 try 之外，不依赖任何调用成功、不受 200ms 节流影响。
        // 只在「过图落地 + 延迟 500ms」后按快照恢复一次，绝不每帧强制开窗。
        try { this.TickTeleportWindowRestore(); } catch { /* 窗口恢复不能拖垮每帧调度 */ }
        try
        {
            if (this.autoSummonMinion) this.TryAutoSummon();
            if (this.autoLeaveDuty) this.TryAutoLeave();
            this.TryAutoConfirmReturn();
            this.MaybeStartBearFetch(false);
            this.MaybeStartPointsFetch(false);
            // v1.2.7：全局重启时间 T 的刷新原先只写在 ImGui 的 DrawHuntList 里，
            // v1.2.6 封死所有 ImGui 窗口后那段成了死代码 → restartAt 永远是 0，
            // 维护分支不生效、概率用错 rt/mt。移到每帧调度里（60s 节流）。
            this.MaybeRefreshRestart(false);
            // v1.2.12：自动前往途中若这只突然被拉黑（Sonar 播报/主站信号），立刻掐断寻路换下一只
            this.TickTriggeredGuard();
            // v1.2.12：黑名单窗口三段同步（冷却锁 / 闸内放行 / 闸后未死），闸会随时间自然换段
            this.TickTriggeredSweep();
            this.TickNativeHud();
        }
        catch { /* 每帧跑，任何异常都吞掉 */ }
    }

    // ───────── v1.2.9：窗口状态直驱 + 过图落地恢复（无强锁） ─────────
    // 【/水桶 为什么会失效】v1.2.6 把 DrawMainWindow() 顶部写死 return; 之后，
    // ImGui 主窗这条显示路径彻底消失，窗口显隐只剩 TickMain 一条路；
    // 而 TickMain 用 KTK 的 IsOpen 缓存判断要不要 Open，游戏过图 hide addon 时
    // IsOpen 可能仍为 true，命令把 mainWindowOpen 切回 true 也不会重新 Open。
    // 更糟的是旧命令分支还读写 miniMode / legacyMainOpen / settingsOpen 三个旧 ImGui 状态，
    // wantMain 里再叠一层 !miniMode && !legacyMainOpen，等于双重耦合。
    // 【修法】命令只切一个 bool，并直接调用原生 Runtime 的 SetMainWindowOpen()，
    // Close→Open 强制重建显示状态，不依赖 Draw、不依赖节流、不碰任何 ImGui API。
    private void ApplyMainWindowNow()
    {
        try
        {
            // v1.2.10 急救：指令优先。先清掉「单窗异常」软停用标志，
            // 保证任何一次 /水桶 都能重新驱动原生窗口，不受之前某帧异常影响。
            this.nativeHud?.ReviveAfterError();
            this.nativeHud?.SetMainWindowOpen(this.mainWindowOpen);
        }
        catch (Exception ex) { try { this.log.Warning(ex, "[魔法水桶] 主窗状态同步失败"); } catch { } }
    }

    /// <summary>v1.2.12：历史窗口同样走「只切一个 bool + 直驱 KTK」，不依赖 Draw / 节流 / IsOpen 缓存。</summary>
    private void ApplyHistoryWindowNow()
    {
        try
        {
            this.nativeHud?.ReviveAfterError();
            this.nativeHud?.SetHistoryWindowOpen(this.historyWindowOpen);
            this.nativeHud?.ForceHistoryRefresh();
        }
        catch (Exception ex) { try { this.log.Warning(ex, "[魔法水桶] 历史窗口状态同步失败"); } catch { } }
    }

    /// <summary>传送 / 跨服 / 执行触发前拍一张窗口开关快照。过图时窗口该关就关，
    /// 不强锁、不拦截 OnHide 回写；落地后按这份快照恢复。</summary>
    private void CaptureWindowStateBeforeTeleport()
    {
        // v1.2.11：已有快照时只刷新时间戳，绝不覆盖 bool。
        // 自动前往队列会连续多次 Goto，且过图时游戏 hide addon 已经把开关写成 false；
        // 这时候重拍会把「全关」当成用户原本的状态，落地后窗口就再也恢复不回来。
        if (this.teleportSnapshotPending)
        {
            this.teleportSnapshotAtUtc = DateTime.UtcNow;
            return;
        }

        // 优先采用稳定态寄存器：它记录的是过图前最后一帧用户真正看到的窗口。
        // 当前仍在稳定态就用当前 bool 刷新一次（用户刚切换窗口也能立即生效）。
        if (!this.stableWindowValid) this.RecordStableWindowState();
        bool betweenNow = false;
        try { betweenNow = this.condition[ConditionFlag.BetweenAreas]; } catch { }
        if (!betweenNow) this.RecordStableWindowState();

        this.teleportSnapshotMain = this.stableWindowMain;
        this.teleportSnapshotHud = this.stableWindowHud;
        this.teleportSnapshotAuto = this.stableWindowAuto;
        this.teleportSnapshotSettings = this.stableWindowSettings;
        this.teleportSnapshotHistory = this.stableWindowHistory;   // v1.2.12：第 5 窗
        this.teleportSnapshotPending = true;
        this.teleportSnapshotAtUtc = DateTime.UtcNow;
        this.restoreAfterTeleportUtc = DateTime.MinValue;
    }

    /// <summary>把用户当前可见的 5 个窗口开关记进稳定态寄存器。</summary>
    private void RecordStableWindowState()
    {
        this.stableWindowMain = this.mainWindowOpen;
        this.stableWindowHud = this.autoHudOpen;
        this.stableWindowAuto = this.autoRunOpen;
        this.stableWindowSettings = this.settingsWindowOpen;
        this.stableWindowHistory = this.historyWindowOpen;   // v1.2.12
        this.stableWindowValid = true;
    }

    /// <summary>传送尚未落地时，用户手动开 / 关窗口也要同步进快照。</summary>
    private void SyncSnapshotFromStable()
    {
        if (!this.teleportSnapshotPending) return;
        this.teleportSnapshotMain = this.stableWindowMain;
        this.teleportSnapshotHud = this.stableWindowHud;
        this.teleportSnapshotAuto = this.stableWindowAuto;
        this.teleportSnapshotSettings = this.stableWindowSettings;
        this.teleportSnapshotHistory = this.stableWindowHistory;   // v1.2.12
    }

    /// <summary>用户主动开 / 关窗口：立刻把新意图记进寄存器与待恢复快照。
    /// v1.2.9 这里清 pending，会让落地后其它窗口丢失；现在只更新快照值。</summary>
    private void NoteUserWindowChange()
    {
        this.RecordStableWindowState();
        this.SyncSnapshotFromStable();
    }

    /// <summary>每帧检测「过图 → 落地」。真正站稳（已登录 + LocalPlayer 存在）后
    /// 延迟 500ms 再恢复，避开落地瞬间游戏仍在批量隐藏 addon 的窗口期。
    /// 快照超过 90 秒没有进入过图就丢弃，避免传送取消后误触发一次重开。</summary>
    private void TickTeleportWindowRestore()
    {
        bool between;
        try { between = this.condition[ConditionFlag.BetweenAreas]; }
        catch { return; }

        bool landed;
        try { landed = this.clientState.IsLoggedIn && this.objectTable.LocalPlayer is not null; }
        catch { return; }

        if (between)
        {
            // 玩家手动传送（没走插件的 Goto）也要补拍快照（此时快照取自稳定态寄存器）。
            if (!this.teleportSnapshotPending) this.CaptureWindowStateBeforeTeleport();
            this.teleportSnapshotAtUtc = DateTime.UtcNow;   // 过图中不计时，防止长时间排队被误判超时
            this.wasBetweenAreas = true;
            this.restoreAfterTeleportUtc = DateTime.MinValue;
            return;
        }

        // v1.2.11：稳定态每帧记录用户当前可见的 4 个窗口开关。
        // 传送还没进入落地倒计时时，用户手动开 / 关窗口也同步进快照，
        // 否则落地恢复会用旧值盖掉用户的新意图。一旦开始倒计时就冻结快照，
        // 避免落地瞬间 hide addon 的 false 回写污染它。
        if (landed)
        {
            this.RecordStableWindowState();
            if (this.teleportSnapshotPending && this.restoreAfterTeleportUtc == DateTime.MinValue)
                this.SyncSnapshotFromStable();
        }

        var sawBetweenAreas = this.wasBetweenAreas;
        this.wasBetweenAreas = false;
        if (!this.teleportSnapshotPending) return;
        if ((DateTime.UtcNow - this.teleportSnapshotAtUtc).TotalSeconds > 90)
        {
            this.teleportSnapshotPending = false;
            this.restoreAfterTeleportUtc = DateTime.MinValue;
            return;
        }
        if (!landed) return;

        if (this.restoreAfterTeleportUtc == DateTime.MinValue)
        {
            // 观察到完整过图 → 落地后 500ms 恢复；没观察到过图（同图传送 / 传送被取消）
            // 给 2s 缓冲，避免刚点「前往」就把窗口白重开一次。
            var delayMs = sawBetweenAreas ? 500 : 2000;
            this.restoreAfterTeleportUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        }
        if (DateTime.UtcNow < this.restoreAfterTeleportUtc) return;

        this.restoreAfterTeleportUtc = DateTime.MinValue;
        this.teleportSnapshotPending = false;

        // ① 先把 5 个开关写回快照值，让 Tick* 的 wantOpen 与命令的 toggle 保持一致；
        this.mainWindowOpen = this.teleportSnapshotMain;
        this.autoHudOpen = this.teleportSnapshotHud;
        this.autoRunOpen = this.teleportSnapshotAuto;
        this.settingsWindowOpen = this.teleportSnapshotSettings;
        this.historyWindowOpen = this.teleportSnapshotHistory;   // v1.2.12
        this.RecordStableWindowState();
        // ② 再直接驱动原生 KTK：Close→Open 绕过 IsOpen 缓存；底层报错由 Runtime 捕获并释放重开。
        // 只复活快照里原本开着的窗口，原本关着的绝不点亮。
        try
        {
            this.nativeHud?.RestoreAfterTeleport(this.teleportSnapshotMain,
                this.teleportSnapshotHud, this.teleportSnapshotAuto,
                this.teleportSnapshotSettings, this.teleportSnapshotHistory);
        }
        catch (Exception ex)
        {
            try { this.log.Warning(ex, "[魔法水桶] 落地窗口恢复失败"); } catch { }
        }
    }

    // ───────────── v1.1.71/72 原生窗口：桥接方法 ─────────────
    // KTK 相关类型全部集中在 NativeHud.cs / NativeAutoWindow.cs；这里只碰 NativeHudRuntime
    // 与本程序集的快照/委托类型，所以即使两个 KTK DLL 缺失，插件本体仍能加载。
    private void InitNativeHud()
    {
        try
        {
            this.nativeHud = new NativeHudRuntime(this.BuildHudActions(),
                this.BuildAutoWindowActions(),
                this.BuildMainActions(),
                this.BuildSettingsActions(),
                this.BuildHistoryActions(),      // v1.2.12：第 5 个原生窗口
                msg => this.log.Info("[魔法水桶] " + msg));
            this.nativeHud.BeginInit(this.pi);
        }
        catch (Exception ex)
        {
            this.nativeHud = null;
            try { this.log.Error(ex, "[魔法水桶] 原生窗口初始化失败，回退 ImGui"); } catch { }
        }
    }

    private void TickNativeHud()
    {
        var h = this.nativeHud;
        if (h is null) return;
        try { h.Tick(this.autoHudOpen, this.BuildHudSnapshot()); }
        catch (Exception ex)
        {
            this.nativeHud = null;
            try { this.log.Error(ex, "[魔法水桶] 原生 HUD 同步异常，回退 ImGui 悬浮窗"); } catch { }
            return;
        }
        // 自动前往窗口独立失败开关：这里的异常只记日志，不影响 HUD 与触发逻辑。
        try { h.TickAuto(this.autoRunOpen, this.BuildAutoWindowSnapshot()); }
        catch (Exception ex)
        {
            try { this.log.Error(ex, "[魔法水桶] 自动前往原生窗口同步异常，本帧跳过"); } catch { }
        }
        // 主窗口独立失败开关：切完整版 / 收起时 wantOpen=false，由 Runtime 主动 Close。
        try
        {
            // v1.2.9：主窗显隐只由 mainWindowOpen 一个开关决定，彻底不再看
            // miniMode / legacyMainOpen / settingsOpen 这些被 v1.2.6 封死的旧 ImGui 残留。
            var wantMain = this.mainWindowOpen;
            h.TickMain(wantMain, this.BuildMainSnapshot());
        }
        catch (Exception ex)
        {
            try { this.log.Error(ex, "[魔法水桶] 主窗口原生同步异常，本帧跳过"); } catch { }
        }
        // v1.2.7：原生设置面板（齿轮）。独立于主窗，异常只记日志不影响其它窗口。
        try { h.TickSettings(this.settingsWindowOpen, this.BuildSettingsSnapshot()); }
        catch (Exception ex)
        {
            try { this.log.Error(ex, "[魔法水桶] 设置面板原生同步异常，本帧跳过"); } catch { }
        }
        // v1.2.12：S 怪历史窗口（主窗 [历史] 按钮 / /水桶 history）。独立 200ms 节流。
        try { h.TickHistory(this.historyWindowOpen, this.BuildHistorySnapshot()); }
        catch (Exception ex)
        {
            try { this.log.Error(ex, "[魔法水桶] 历史窗口原生同步异常，本帧跳过"); } catch { }
        }
    }

    // ───────────── v1.2.7 原生设置面板：数据 / 委托桥 ─────────────
    private SettingsActions BuildSettingsActions() => new SettingsActions
    {
        Toggle = key =>
        {
            switch (key)
            {
                case "main":
                    this.mainWindowOpen = !this.mainWindowOpen;
                    this.ApplyMainWindowNow();
                    break;
                case "auto":
                    this.autoRunOpen = !this.autoRunOpen;
                    this.nativeHud?.SetAutoWindowOpen(this.autoRunOpen);
                    break;
                case "hud":
                    this.autoHudOpen = !this.autoHudOpen;
                    this.nativeHud?.SetHudWindowOpen(this.autoHudOpen);
                    break;
                case "summon":
                    this.autoSummonMinion = !this.autoSummonMinion;
                    if (this.autoSummonMinion) this.RefreshUnlockedMinions();
                    this.SaveAutoSettings();
                    break;
                case "leave":
                    this.autoLeaveDuty = !this.autoLeaveDuty;
                    this.SaveAutoSettings();
                    break;
                case "return":
                    this.autoConfirmReturn = !this.autoConfirmReturn;
                    this.SaveAutoSettings();
                    break;
                case "theme": this.SetLightTheme(!this.lightMode); break;   // theme.txt 已持久化
            }
            this.nativeHud?.ForceSettingsRefresh();
            this.nativeHud?.ForceMainRefresh();
        },
        // 和主窗「刷新」完全同一条链路：熊站击杀时间 + 实时排点 + 维护 T（force，立即对齐主站）
        Refresh = () =>
        {
            if (this.srankData?.data is null) this.LoadSrankData();
            this.MaybeStartBearFetch(true);
            this.MaybeStartPointsFetch(true);
            this.MaybeRefreshRestart(true);
            this.nativeHud?.ForceMainRefresh();
            this.Print("正在从熊站拉取最新击杀时间、维护 T 和实时排点…");
        },
        Closed = () =>
        {
            this.settingsWindowOpen = false;
            this.nativeHud?.SetSettingsWindowOpen(false);
        },
    };

    private SettingsSnapshot BuildSettingsSnapshot() => new SettingsSnapshot
    {
        ShowMain = this.mainWindowOpen,
        ShowAuto = this.autoRunOpen,
        ShowHud = this.autoHudOpen,
        AutoSummon = this.autoSummonMinion,
        AutoLeave = this.autoLeaveDuty,
        ConfirmReturn = this.autoConfirmReturn,
        LightTheme = this.lightMode,
        Status = this.restartAt > 0 ? $"维护 T 已同步 · {this.currentDc}" : $"当前大区 · {this.currentDc}",
    };

    // ───────────── v1.2.12 原生历史窗口：数据 / 委托桥 ─────────────
    private HistoryActions BuildHistoryActions() => new HistoryActions
    {
        SetFilter = f =>
        {
            this.historyFilter = f;
            this.historyPage = 0;                 // 换筛选一定回第一页
            this.nativeHud?.ForceHistoryRefresh();
        },
        PageBy = delta =>
        {
            this.historyPage = Math.Max(0, this.historyPage + delta);
            this.nativeHud?.ForceHistoryRefresh();
        },
        Clear = () =>
        {
            this.history?.Clear();
            this.historyPage = 0;
            this.nativeHud?.ForceHistoryRefresh();
            this.Print("已清空 S 怪历史记录（srank_history.json）");
        },
        Closed = () =>
        {
            this.historyWindowOpen = false;
            this.nativeHud?.SetHistoryWindowOpen(false);
            this.NoteUserWindowChange();   // v1.2.11：用户主动关窗要同步进快照
        },
    };

    private HistorySnapshot BuildHistorySnapshot()
    {
        var s = this.history is { } h
            ? h.Snapshot(this.historyFilter, this.historyPage)
            : new HistorySnapshot { Status = "历史库未就绪" };
        // Snapshot 会把越界页夹回最后一页；写回字段，避免反复点「下一页」让页码无限增大。
        if (s.Page != this.historyPage) this.historyPage = s.Page;
        return s;
    }

    private sealed class AutoSettings
    {
        public bool autoSummonMinion { get; set; }
        public bool autoLeaveDuty { get; set; }
        public bool autoConfirmReturn { get; set; } = true;
    }

    private string AutoSettingsFile => Path.Combine(this.pluginDirectory, "auto_settings.json");

    private void LoadAutoSettings()
    {
        try
        {
            if (!File.Exists(this.AutoSettingsFile)) return;
            var x = JsonSerializer.Deserialize<AutoSettings>(File.ReadAllText(this.AutoSettingsFile));
            if (x is null) return;
            this.autoSummonMinion = x.autoSummonMinion;
            this.autoLeaveDuty = x.autoLeaveDuty;
            this.autoConfirmReturn = x.autoConfirmReturn;
        }
        catch { /* 读不到就用旧版默认（全关 / 返回点确定默认开） */ }
    }

    private void SaveAutoSettings()
    {
        try
        {
            var x = new AutoSettings
            {
                autoSummonMinion = this.autoSummonMinion,
                autoLeaveDuty = this.autoLeaveDuty,
                autoConfirmReturn = this.autoConfirmReturn,
            };
            File.WriteAllText(this.AutoSettingsFile, JsonSerializer.Serialize(x));
        }
        catch { /* 写不进去只影响下次启动 */ }
    }

    // ───────────── v1.1.74 原生主窗口：数据 / 委托桥 ─────────────
    // 与另外两个窗口一样：快照和委托里没有任何 KTK 类型，Plugin 侧不引用 KTK。
    private MainActions BuildMainActions() => new MainActions
    {
        Search = t => this.searchText = t,
        NextDc = () =>
        {
            var dcs = this.srankData?.dcs;
            if (dcs is null || dcs.Count == 0) return;
            var i = dcs.FindIndex(x => x.dc == this.currentDc);
            if (i < 0) i = 0;
            this.SelectDc(dcs[(i + 1) % dcs.Count].dc);
        },
        ToggleOnlyOpen = () => this.onlyOpen = !this.onlyOpen,
        Refresh = () =>
        {
            if (this.srankData?.data is null) this.LoadSrankData();
            this.MaybeStartBearFetch(true);
            this.MaybeStartPointsFetch(true);
            // v1.2.7：手动刷新立即拉一次维护重启时间 T（不等 60s 节流），让维护期概率
            // 当场按 mt 重算，与主站对齐。T 回来后下一帧 Apply 就会用新值。
            this.MaybeRefreshRestart(true);
            this.nativeHud?.ForceMainRefresh();
            this.Print("正在从熊站拉取最新击杀时间、维护 T 和实时排点…");
        },
        OpenAuto = () => this.autoRunOpen = !this.autoRunOpen,
        // v1.2.6 核弹级清扫：以下三个回调原本用来唤醒旧版 ImGui（完整版主窗 / 高级设置窗 / 收起小窗），
        // 现在原生 KTK 已是唯一主力 UI。「完整 / 收起」两个按钮已在主窗隐藏，回调保持空操作；
        // 齿轮改为打开 v1.2.7 新增的【原生 KTK 设置面板】，绝不唤起任何 ImGui 窗口。
        Legacy = () => { },
        OpenSettings = () =>
        {
            this.settingsWindowOpen = !this.settingsWindowOpen;
            this.nativeHud?.ForceSettingsRefresh();
        },
        // v1.2.12：主窗齿轮旁的 [历史] 按钮 → 打开第 5 个原生窗口，绝不唤起任何 ImGui 窗口。
        OpenHistory = () =>
        {
            this.historyWindowOpen = !this.historyWindowOpen;
            this.ApplyHistoryWindowNow();
            this.NoteUserWindowChange();
        },
        Mini = () => { },
        StopAll = () => this.StopAll(),
        AllWorlds = () =>
        {
            if (this.srankData?.data is not { } d) return;
            foreach (var w in d.worlds) if (w.Length > 0) this.worldFilter.Add(w);
        },
        ClearWorlds = () => this.worldFilter.Clear(),
        CurrentWorldOnly = () =>
        {
            var w = this.CurrentWorldName();
            this.worldFilter.Clear();
            if (w.Length > 0) this.worldFilter.Add(w);
        },
        ToggleWorld = w =>
        {
            if (!this.worldFilter.Remove(w)) this.worldFilter.Add(w);
        },
        TogglePatch = k =>
        {
            var before = this.patchSel.Count;
            if (k == "ALL") this.patchSel.Clear();
            else if (!this.patchSel.Remove(k)) this.patchSel.Add(k);
            try { this.log.Info($"[SrankFlag.UI] 左键点击版本: {k}, 当前选中数量: {before} -> {this.patchSel.Count}"); }
            catch { }
        },
        SelectDc = dc => this.SelectDc(dc),
        SelectPatch = k =>
        {
            // v1.1.96：右键「独奏」—— 清空其他，只留当前版本（左键多选走 TogglePatch）。
            if (k == "ALL") this.patchSel.Clear();
            else { this.patchSel.Clear(); this.patchSel.Add(k); }
        },
        Primary = (en, w) => this.MainPrimary(en, w),
        GotoPoint = (en, w, x, y) => this.MainGotoPoint(en, w, x, y),
        GotoAetheryte = (en, w) => this.MainGotoAetheryte(en, w),
        Flag = (m, x, y) => this.Flag(m, x, y),                 // v1.2.2：坐标按钮 → 只在地图插旗，不寻路
        FlagAetheryte = (m, c) => this.FlagAetheryte(m, c),       // v1.2.2：大水晶按钮 → 插旗到该区大水晶位置
        Expand = k => this.mainExpandedKey = k ?? "",
        SelectHuntWorld = (en, w) => this.huntWorld[en] = w,
        // v1.2.7：过图 / 跨服时游戏会临时隐藏所有自定义 addon，OnHide → Closed() 会把开关
        // 永久写成 false，导致「点开始触发后所有窗口都消失且再也回不来」。保护期内忽略回写，
        // TickMain 在过图结束后会自动把窗口重新 Open。
        // v1.2.8 强锁：最顶层拦截，自动化会话中绝不把主窗开关写死为 false。
        Closed = () => this.mainWindowOpen = false,
    };

    /// <summary>复刻 DrawHuntList 的筛选/聚合逻辑，产出原生主窗口的纯数据快照。</summary>
    private MainSnapshot BuildMainSnapshot()
    {
        var s = new MainSnapshot();
        var data = this.srankData?.data;
        s.Ready = data is not null;
        s.DataError = this.dataError.Length > 0 ? this.dataError : "未知原因";
        s.Search = this.searchText;
        s.OnlyOpen = this.onlyOpen;
        s.Fetching = this.bearFetching;
        s.CurrentWorld = this.CurrentWorldName();
        s.Dc = data?.dc ?? this.currentDc;
        s.Status = this.bearStatus;
        if (this.srankData?.dcs is { Count: > 0 } allDcs)
            foreach (var d in allDcs) s.Dcs.Add(d.dc);
        s.DcSelected = this.currentDc;
        s.NeedsManualRefresh = this.currentDc != SelfHostedDc;
        // v1.1.78：按「少点文字」精简，底部只留一行状态，第二行说明不再显示。
        s.BearStatus = "";

        var dcs = this.srankData?.dcs;
        if (dcs is { Count: > 0 })
        {
            var i = dcs.FindIndex(x => x.dc == this.currentDc);
            if (i < 0) i = 0;
            s.DcNext = dcs[(i + 1) % dcs.Count].dc;
        }

        foreach (var kv in new[] { ("ALL", "全部"), ("ARR", "2.0"), ("HW", "3.0"), ("SB", "4.0"),
                                   ("ShB", "5.0"), ("EW", "6.0"), ("DT", "7.0") })
        {
            s.PatchKeys.Add(kv.Item1);
            s.PatchLabels.Add(kv.Item2);
            s.PatchOn.Add(kv.Item1 == "ALL" ? this.patchSel.Count == 0 : this.patchSel.Contains(kv.Item1));
        }

        if (data is null) return s;

        foreach (var w in data.worlds.Take(16))
        {
            s.Worlds.Add(w);
            s.WorldOn.Add(this.worldFilter.Contains(w));
        }
        s.WorldMenuLabel = $"服务器 {this.worldFilter.Count}/{data.worlds.Length}";
        s.Counts = $"{data.worlds.Length}服 · {data.hunts.Length}只 · 已选{this.worldFilter.Count}";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // v1.1.78：详情头做成游戏里「◷ 12:26」那种倒计时。
        // 冷却显示 mm:ss（跨小时补 hh:），开闸归零，其余占位。
        static string ClockText(double sec)
        {
            if (sec <= 0) return "00:00";
            var t = (long)sec;
            var h = t / 3600;
            var m = t % 3600 / 60;
            var ss = t % 60;
            return h > 0 ? $"{h}:{m:00}:{ss:00}" : $"{m:00}:{ss:00}";
        }
        // 每只 S 尾部的进度：5 颗圆点串，每颗 20%
        static string Dots(double pct, string st)
        {
            var n = st == "unknown" ? 0 : Math.Clamp((int)Math.Round(pct / 20.0), 0, 5);
            return new string('●', n) + new string('○', 5 - n);
        }
        var pri = new Dictionary<string, int> { ["open"] = 0, ["cd"] = 1, ["over"] = 2, ["unknown"] = 3 };

        // v1.1.77：不再按怪名聚合，每个「服 × 怪」都是独立一行，
        // 同名 S 在 A 服和 B 服会分别出现，也就不再需要右侧详情面板。
        List<(HuntData h, string world, string st, double eta, double pct)> rows = new();
        foreach (var h in data.hunts)
        {
            if (this.patchSel.Count > 0 && !this.patchSel.Contains(h.patch)) continue;
            if (this.searchText.Length > 0 &&
                !(h.cn + " " + h.en + " " + h.map).Contains(this.searchText, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var w in h.worlds)
            {
                if (w.lastDeath <= 0 || !this.worldFilter.Contains(w.world)) continue;
                var eff = Math.Max(w.lastDeath, this.restartAt);
                var maint = this.restartAt > 0 && this.restartAt > w.lastDeath;
                var useCd = maint && h.mt.Length >= 1 ? h.mt[0] : h.cd;
                var useWin = maint && h.mt.Length >= 2 ? h.mt[1] : h.win;
                var c = Calc(eff, now, useCd, useWin);
                rows.Add((h, w.world, c.State, c.Eta, c.Pct));
            }
        }
        rows.Sort((a, b) =>
        {
            var c = pri.GetValueOrDefault(a.st, 3) - pri.GetValueOrDefault(b.st, 3);
            if (c != 0) return c;
            c = a.eta.CompareTo(b.eta);
            return c != 0 ? c : string.CompareOrdinal(a.h.cn, b.h.cn);
        });
        var listed = this.onlyOpen ? rows.Where(r => r.st == "open").ToList() : rows;
        // v1.2.3：开了「只看开闸」却一条开闸都没有时，回退显示全部，避免窗口空白让人以为没数据
        if (this.onlyOpen && listed.Count == 0) listed = rows;
        var nOpen = rows.Count(r => r.st == "open");
        s.Counts = $"{this.worldFilter.Count}服 · {listed.Count}条 · 开闸{nOpen}";

        foreach (var (h, world, st, eta, pct) in listed)
        {
            var key = h.en + "\u0001" + world;
            var r = new MainHuntRow
            {
                Key = key,
                En = h.en,
                World = world,
                Cn = h.cn,
                Map = h.map,
                State = st,
                Patch = h.patch,
                Pct = st == "unknown" ? "—" : $"{Math.Round(pct)}%",
                Eta = st == "open" ? "可踩" : st == "cd" ? Dur(eta) + "后" : "已过窗",
                BestWorld = world,
                RowLabel = h.cn,
                HasSpecial = HuntPreps.ContainsKey(h.cn),   // v1.2.2：该怪是否有特殊触发脚本（切职/采集/自爆等）
                Triggered = this.IsTriggered(h.en, world),  // v1.2.12：已被他人触发 → 行内红标 + 按钮置灰
                ProgressLabel = Dots(pct, st) + "  " + (st == "unknown" ? "—" : Math.Round(pct) + "%"),
                PctValue = pct,
                Clock = st == "cd" ? ClockText(eta) : st == "open" ? "00:00" : "--:--",
            };

            var aeDiscard = DiscardMaps.ContainsKey(h.map);

            // 绿点 / 条件 / 要点只给「当前展开的那一行」算，
            // 避免每 200ms 把几百行的实时排点与条件预报全算一遍。
            if (key == this.mainExpandedKey)
            {
                r.Expanded = true;
                var greens = this.GreensFor(h, world);
                r.PrimaryLabel = h.cn == "顾尼图" ? "黑风海触发"
                    : greens.Count > 0 ? "前往绿点"
                    : aeDiscard ? "前往大水晶+丢弃" : "前往大水晶";
                r.PointsHint = greens.Count > 0 ? $"可踩绿点 {greens.Count} 个 · {this.pointsStatus}"
                    : NoPointHunts.Contains(h.cn) ? "触发与点位无关 · 到大水晶等刷新" : "";
                if (ConditionForecast.HasSpec(h.en))
                    r.Condition = Trunc("条件 " + ConditionForecast.Describe(h.en, now), 40);
                if (h.tips.Length > 0)
                    r.Tips = Trunc(string.Join(" / ", h.tips.Take(2)), 48);

                if (h.cn == "顾尼图")
                {
                    r.Points.Add(new MainPointRow
                    {
                        Label = "▶ 黑风海触发",
                        Special = true,
                    });
                }
                else
                {
                    for (var i = 0; i < greens.Count && i < 14; i++)
                    {
                        var g = greens[i];
                        r.Points.Add(new MainPointRow
                        {
                            Label = $"◈ ({g.X:0.0}, {g.Y:0.0})",
                            X = g.X, Y = g.Y,
                        });
                    }
                    r.Points.Add(new MainPointRow
                    {
                        Label = $"⬡ 大水晶 · {h.map}",
                        Aetheryte = true,
                    });
                }
            }
            s.Hunts.Add(r);
        }
        return s;
    }

    private static string Trunc(string t, int max) => t.Length <= max ? t : t[..max] + "…";

    private static string StateCn(string st) => st switch
    {
        "open" => "开闸",
        "cd" => "冷却",
        "over" => "过窗",
        _ => "无记录",
    };

    /// <summary>主按钮 / 特殊目的地：复刻原 ImGui 展开行里的前往绿点 / 大水晶 / 丢弃 / 顾尼图逻辑。</summary>
    private void MainPrimary(string en, string world)
    {
        var data = this.srankData?.data;
        if (data is null) return;
        var h = data.hunts.FirstOrDefault(x => x.en == en);
        if (h is null) return;

        // v1.2.12：已被他人触发的一律不许再动 —— UI 上按钮是灰的，这里是最后一道闸，
        // 防止从悬浮窗 / 旧状态 / 快捷键等旁路绕进来还能把人送过去。
        if (this.IsTriggered(en, world))
        {
            this.Print($"[{h.cn}] 已被他人触发（{world}），不再前往。击杀播报或主站击杀时间到了才会解锁。");
            this.nativeHud?.ForceMainRefresh();
            return;
        }

        this.CaptureWindowStateBeforeTeleport();
        var pw = world;

        var greens = this.GreensFor(h, pw);
        var aeDiscard = DiscardMaps.ContainsKey(h.map);
        var walk = greens.Count > 0;

        if (h.cn == "顾尼图")
        {
            this.GounituTrigger(pw);
            this.Print($"[{h.cn}] 开始自动触发（切战士 → 传黑风海 → 飞触发点 → 水蛭飞斧 → 挑衅冰海天使）");
        }
        else if (walk)
        {
            var g0 = greens[0];
            var wr = this.Goto(h.map, g0.X, g0.Y, pw, this.HuntLineOverride(h.en, pw), h.cn, true, greens);
            this.Print($"[{h.cn}] {wr.Message}");
        }
        else
        {
            var ar = this.GotoAetheryte(h.map, pw, h.cn);
            this.Print($"[{h.cn}] {ar.Message}");
            if (ar.Ok && aeDiscard)
                this.WaitUntil(() => this.InMap(h.map), () =>
                    this.Sched(() => this.Print(this.DiscardTrigger().Message),
                        TimeSpan.FromMilliseconds(2500)), 240, 500);
        }
    }

    /// <summary>该怪在该服可踩的绿点：实时排点优先，回退静态点位表。</summary>
    private List<Pt> GreensFor(HuntData h, string world)
    {
        if (!PointHunts.Contains(h.cn) || NoPointHunts.Contains(h.cn)) return new List<Pt>();
        return this.LiveGreens(h.en, world)
               ?? HuntPoints(this.srankData!.points.GetValueOrDefault(h.en), world)
                    .Where(q => q.S == 'g').ToList();
    }

    /// <summary>点目的地列表里的某一颗绿点：直接按这一行的服飞过去。</summary>
    private void MainGotoPoint(string en, string world, float x, float y)
    {
        var data = this.srankData?.data;
        if (data is null) return;
        var h = data.hunts.FirstOrDefault(q => q.en == en);
        if (h is null) return;
        var greens = this.GreensFor(h, world);
        var wr = this.Goto(h.map, x, y, world, this.HuntLineOverride(en, world), h.cn, true, greens);
        this.Print($"[{h.cn}] {wr.Message}");
    }

    /// <summary>点目的地列表里的大水晶：直接传大水晶（丢弃类地图落地后照旧自动丢弃）。</summary>
    private void MainGotoAetheryte(string en, string world)
    {
        var data = this.srankData?.data;
        if (data is null) return;
        var h = data.hunts.FirstOrDefault(q => q.en == en);
        if (h is null) return;
        var ar = this.GotoAetheryte(h.map, world, h.cn);
        this.Print($"[{h.cn}] {ar.Message}");
        if (ar.Ok && DiscardMaps.ContainsKey(h.map))
            this.WaitUntil(() => this.InMap(h.map), () =>
                this.Sched(() => this.Print(this.DiscardTrigger().Message),
                    TimeSpan.FromMilliseconds(2500)), 240, 500);
    }

    /// <summary>倒地后自动点「要传送到返回点吗？」的「确定」。
    /// 只在自动前往「触发中 / 已暂停」时才自动点；空闲（没在跑队列）不打扰正常游玩。两道保险避免误点别的确认框：
    /// ① 自己确实已倒（Unconscious / HP 0）；② SelectYesno 当前可见。
    /// 想在副本里躺平等复活，用「/水桶 倒地 关」临时关掉。</summary>
    private unsafe void TryAutoConfirmReturn()
    {
        if (!this.autoConfirmReturn) return;
        // 只在「自动前往」处于触发中或已暂停时才自动点返回；空闲状态（没在跑队列）不启用，免得干扰正常游玩
        if (!this.autoRunning && !this.autoPaused) return;

        try
        {
            if (!this.Dead())
            {
                this.returnClickLogged = false;
                this.lastReturnClick = default;
                return;
            }
        }
        catch { return; }

        if (DateTime.Now - this.lastReturnClick <= TimeSpan.FromMilliseconds(500)) return;

        try
        {
            var addon = this.gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
            if (addon == null || !addon->IsVisible) return;
            this.lastReturnClick = DateTime.Now;
            addon->FireCallbackInt(0);
            if (!this.returnClickLogged)
            {
                this.returnClickLogged = true;
                this.Print("[自动前往] 已自动点「确定」，传送到返回点");
            }
        }
        catch { /* 读 UI 失败就下一帧再看 */ }
    }

    private unsafe void TryAutoSummon()
    {
        if (DateTime.Now - this.lastSummonCheck <= TimeSpan.FromSeconds(2)) return;
        this.lastSummonCheck = DateTime.Now;

        var terr = this.clientState.TerritoryType;
        if (!MinionMap.TryGetValue(terr, out var minionId)) return;
        if (!this.unlockedMinions.Contains(minionId)) return;
        if (this.condition[ConditionFlag.Mounted] || this.condition[ConditionFlag.RidingPillion]
            || this.condition[ConditionFlag.MountOrOrnamentTransition] || this.condition[ConditionFlag.Mounting]
            || this.condition[ConditionFlag.Mounting71]) return;

        var am = ActionManager.Instance();
        if (am == null) return;
        if (am->GetActionStatus(ActionType.Companion, minionId) != 0) return;

        // 已经带着对的随从就不重复召唤；带着别的随从则切换
        foreach (var o in this.objectTable)
        {
            if (o is ICharacter ch && ch.ObjectKind == ObjectKind.Companion)
            {
                if (ch.BaseId == minionId) return;
                break;
            }
        }
        am->UseAction(ActionType.Companion, minionId);
    }

    private unsafe string SummonForCurrentTerritory()
    {
        var terr = this.clientState.TerritoryType;
        if (!MinionMap.TryGetValue(terr, out var minionId))
            return $"当前地图（territory {terr}）没有配置自动召唤随从。";
        if (!this.unlockedMinions.Contains(minionId))
            return $"随从 {minionId} 还没解锁，无法召唤。";
        var am = ActionManager.Instance();
        if (am == null) return "动作系统未就绪";
        if (am->GetActionStatus(ActionType.Companion, minionId) != 0)
            return $"随从 {minionId} 当前不可用（冷却/条件不满足）。";
        am->UseAction(ActionType.Companion, minionId);
        return $"已召唤随从（minionId {minionId}）。";
    }

    // ─────────────────────────── 独身自动退本 ───────────────────────────

    /// <summary>开启后：独身（没有队伍）且在副本里（BoundByDuty）时自动退出。
    /// 每秒最多尝试一次，CanLeaveCurrentContent 为 false（刚进本等）会自动重试。</summary>
    private void TryAutoLeave()
    {
        if (DateTime.Now - this.lastLeaveTry <= TimeSpan.FromSeconds(1)) return;
        if (IsInParty()) return;
        if (!this.condition[ConditionFlag.BoundByDuty]) return;
        this.lastLeaveTry = DateTime.Now;
        this.DoLeaveDuty();
    }

    /// <summary>是否在队伍里（读游戏原生 GroupManager：独身 MemberCount=0）。</summary>
    private static unsafe bool IsInParty()
    {
        try
        {
            var gm = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
            return gm != null && gm->MainGroup.MemberCount > 0;
        }
        catch { return false; }
    }

    private unsafe void DoLeaveDuty()
    {
        if (!EventFramework.CanLeaveCurrentContent()) return;
        EventFramework.LeaveCurrentContent(true);
        this.Print("独身自动退本：已退出当前副本。");
    }

    // ─────────────────────────── 微软雅黑字体 ───────────────────────────

    private void InitFontAtlasIfNeeded()
    {
        if (this.fontInitTried) return;
        this.fontInitTried = true;
        try
        {
            var path = FindYaheiPath();
            if (path is null)
            {
                this.log.Warning("[SrankFlag] 没找到微软雅黑，继续用 Dalamud 默认字体");
                return;
            }

            var ranges = this.BuildGlyphRanges();
            var atlas = this.uiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.OnNewFrame, false, "SrankFlag");
            this.fontAtlas = atlas;
            this.fontHandleS = AddYaheiFont(atlas, path, ranges, 14f);
            this.fontHandleM = AddYaheiFont(atlas, path, ranges, 22f);
            this.fontHandleL = AddYaheiFont(atlas, path, ranges, 44f);
            this.log.Info($"[SrankFlag] 字体已切换到 {Path.GetFileName(path)}（{ranges.Length / 2} 段字形范围）");
        }
        catch (Exception ex)
        {
            this.fontAtlas = null;
            this.fontHandleS = this.fontHandleM = this.fontHandleL = null;
            this.log.Error(ex, "[SrankFlag] 微软雅黑初始化失败，回退默认字体");
        }
    }

    /// <summary>按优先级找雅黑：系统字体目录 → 插件目录自带副本 → 黑体/宋体兜底。</summary>
    private string? FindYaheiPath()
    {
        string win;
        try { win = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        catch { win = "C:" + Path.DirectorySeparatorChar + "Windows"; }

        var names = new[] { "msyh.ttc", "msyhbd.ttc", "msyhl.ttc", "simhei.ttf", "simsun.ttc" };
        foreach (var n in names)
        {
            var p = Path.Combine(win, "Fonts", n);
            if (File.Exists(p)) return p;
        }

        foreach (var n in names)
        {
            var p = Path.Combine(this.pluginDirectory, n);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>收集界面真正会用到的字符，合成 ImGui 需要的「成对 + 0 结尾」范围表。</summary>
    private ushort[] BuildGlyphRanges()
    {
        var set = new SortedSet<int>();

        void Take(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var ch in text)
            {
                var code = (int)ch;
                if (code is >= 32 and < 0xFFFD) set.Add(code);
            }
        }

        for (var c = 32; c <= 126; c++) set.Add(c);
        Take("　、。，．・：；！？（）【】「」『』《》“”‘’—～·…※×÷°±←↑→↓●○◆◇■□★☆⌃⌄▶▸▾⟳✕✓…魔法桶");
        Take(FontGlyphs);

        // 所有大区的服名 / 怪名 / 触发方式 / 触发要点都要进 atlas。
        // 只取「当前大区」的话，切到猫小胖/陆行鸟后新出现的字（琥珀、曦染、延夏…）
        // 不在图集里，就会渲染成方块（豆腐块乱码）。
        if (this.srankData?.dcs is { Count: > 0 } allDcs)
        {
            foreach (var dc in allDcs)
            {
                Take(dc.dc);
                foreach (var w in dc.worlds) Take(w);
                foreach (var h in dc.hunts)
                {
                    Take(h.cn);
                    Take(h.en);
                    Take(h.map);
                    Take(h.patch);
                    Take(h.kind);
                    if (h.tips is { Length: > 0 })
                        foreach (var t in h.tips) Take(t);
                }
            }
        }

        if (this.srankData?.data is { } d)
        {
            Take(d.dc);
            foreach (var w in d.worlds) Take(w);
            foreach (var h in d.hunts)
            {
                Take(h.cn);
                Take(h.en);
                Take(h.map);
                Take(h.patch);
            }
        }

        foreach (var h in NoPointHunts) Take(h);
        foreach (var n in NavHunts) { Take(n.Cn); Take(n.Map); }
        foreach (var m in DiscardMaps.Keys) Take(m);
        TakeAssemblyLiterals(Take);

        // 连续码位合并成 [start,end] 对
        var arr = set.ToArray();
        var list = new List<ushort>(arr.Length + 8);
        var i = 0;
        while (i < arr.Length)
        {
            var start = arr[i];
            var end = start;
            while (i + 1 < arr.Length && arr[i + 1] == end + 1)
            {
                i++;
                end = arr[i];
            }

            list.Add((ushort)start);
            list.Add((ushort)end);
            i++;
        }

        list.Add(0);
        return list.ToArray();
    }

    /// <summary>把本程序集所有字符串字面量里的字符并进字形表。
    /// 手写 FontGlyphs 只覆盖了生成那一刻的字面量，后来加的 ⌃ / ⌄ / ⟳ 之类就会缺字形渲染成「?」。</summary>
    private static void TakeAssemblyLiterals(System.Action<string?> take)
    {
        try
        {
            var asm = typeof(Plugin).Assembly;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var t in asm.GetTypes())
            {
                foreach (var f in t.GetFields(flags))
                {
                    if (f is { IsLiteral: true } && f.FieldType == typeof(string))
                        take(f.GetRawConstantValue() as string);
                }

                foreach (var m in t.GetMethods(flags)) ScanIlStrings(m, take);
                foreach (var c in t.GetConstructors(flags)) ScanIlStrings(c, take);
            }
        }
        catch
        {
            // 反射失败就退回手写字形表，不影响启动
        }
    }

    private static void ScanIlStrings(MethodBase m, System.Action<string?> take)
    {
        byte[]? il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); }
        catch { return; }
        if (il is null) return;

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72) continue; // ldstr
            var token = BitConverter.ToInt32(il, i + 1);
            try { take(m.Module.ResolveString(token)); }
            catch { /* 不是字符串 token，跳过 */ }
        }
    }

    private static IFontHandle AddYaheiFont(IFontAtlas atlas, string path, ushort[] ranges, float sizePx) =>
        atlas.NewDelegateFontHandle(step => step.OnPreBuild(toolkit =>
        {
            try
            {
                var cfg = new SafeFontConfig
                {
                    SizePx = sizePx,
                    GlyphRanges = ranges,
                    PixelSnapH = true,
                    OversampleH = 1,
                    OversampleV = 1,
                };
                toolkit.AddFontFromFile(path, in cfg);
            }
            catch
            {
                // 单个字号构建失败就让它保持不可用，绘制时自动回退默认字体
            }
        }));

    /// <summary>每帧开头锁一次字体（拿到可用的 ImFontPtr），结束时释放。</summary>
    private void BeginFonts()
    {
        this.fontReady = false;
        if (this.fontAtlas is null) return;
        try
        {
            if (this.fontHandleS?.Available != true ||
                this.fontHandleM?.Available != true ||
                this.fontHandleL?.Available != true)
            {
                if (!this.fontProbeLogged && this.fontHandleS?.LoadException is { } err)
                {
                    this.fontProbeLogged = true;
                    this.log.Error(err, "[SrankFlag] 雅黑字体构建失败，继续用默认字体");
                }

                return;
            }

            this.fontLockS = this.fontHandleS!.Lock();
            this.fontLockM = this.fontHandleM!.Lock();
            this.fontLockL = this.fontHandleL!.Lock();
            this.fontPtrS = this.fontLockS.ImFont;
            this.fontPtrM = this.fontLockM.ImFont;
            this.fontPtrL = this.fontLockL.ImFont;
            this.fontReady = true;
            if (!this.fontProbeLogged)
            {
                this.fontProbeLogged = true;
                this.log.Info("[SrankFlag] 雅黑字形图集就绪（14 / 22 / 44px）");
            }
        }
        catch
        {
            this.EndFonts();
        }
    }

    private void EndFonts()
    {
        this.fontReady = false;
        try { this.fontLockS?.Dispose(); } catch { /* ignore */ }
        try { this.fontLockM?.Dispose(); } catch { /* ignore */ }
        try { this.fontLockL?.Dispose(); } catch { /* ignore */ }
        this.fontLockS = this.fontLockM = this.fontLockL = null;
    }

    /// <summary>按字号挑「不小于目标字号」的最小一档：永远只缩小采样、不放大，中文才不会发虚。</summary>
    private ImFontPtr FontFor(float size)
    {
        if (!this.fontReady) return ImGui.GetFont();
        if (size <= 14f) return this.fontPtrS;   // 11.5–14：原生或微缩
        if (size <= 22f) return this.fontPtrM;   // 14.5–22：缩小 0.66–1.0
        return this.fontPtrL;                    // 25 / 42：从 44px 缩下来
    }

    /// <summary>用对应字体量文字宽度（雅黑和默认字体的字宽不同，不能用默认字体比例硬算）。</summary>
    private float TW(string text, float size)
    {
        if (!this.fontReady) return ImGui.CalcTextSize(text).X * size / ImGui.GetFontSize();
        var f = this.FontFor(size);
        ImGui.PushFont(f);
        var w = ImGui.CalcTextSize(text).X * size / ImGui.GetFontSize();
        ImGui.PopFont();
        return w;
    }

    // ─────────────────────────── 游戏内 ImGui 窗口 ───────────────────────────

    private void DrawMainWindow()
    {
        // v1.2.6 核弹级清扫：原生 KTK 主窗（MainWindow）/ 自动前往（AutoWindow）/ 触发（HudWindow）已成
        // 唯一主力 UI。旧版 ImGui 主窗 / 收起小窗（miniMode）/ 设置窗（settingsOpen）/ 完整版（legacyMainOpen）
        // 全部封死，绝不再绘制任何 ImGui 根窗口，从根本上杜绝「点按钮弹出旧 GUI / 串台」。
        // 本方法仅保留与渲染无关的副作用：筛选变更落盘（切大区 / 重载后原样恢复）。
        this.TrackFilterChanges();
        return;
        if (this.miniMode)
        {
            if (!this.themeLoaded) { this.themeLoaded = true; this.LoadTheme(); }
            this.InitFontAtlasIfNeeded();
            this.DrawMiniWindow();
            return;
        }
        // 主窗关掉时，触发悬浮窗 / 自动前往窗仍要能独立显示（字体和主题照样初始化）
        if (!this.mainWindowOpen)
        {
                if (this.autoRunOpen || this.autoHudOpen)
                {
                    if (!this.themeLoaded) { this.themeLoaded = true; this.LoadTheme(); }
                    this.InitFontAtlasIfNeeded();
                }
            return;
        }
        // v1.1.74：原生主窗口接管时不画 ImGui 主窗；自动前往 / 触发 HUD 仍要能独立显示。
        // v1.1.98：齿轮打开的高级设置要能画出来（原生窗保持打开，不关）
        if (this.nativeHud is { ActiveMain: true } && !this.legacyMainOpen && !this.settingsOpen)
        {
            if (!this.themeLoaded) { this.themeLoaded = true; this.LoadTheme(); }
            this.InitFontAtlasIfNeeded();
            this.TrackFilterChanges();   // 原生版也要把筛选变更落盘，切大区才能原样恢复
            return;
        }
        if (!this.themeLoaded)
        {
            this.themeLoaded = true;
            this.LoadTheme();
        }
        this.InitFontAtlasIfNeeded();
        this.BeginFonts();
        try
        {
            // V1 科技风：近黑底 + 圆角卡片 + 酸黄点缀
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Text, V1Text);
            ImGui.PushStyleColor(ImGuiCol.TextDisabled, V1Dim);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, V1Input);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, V1Card);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, V1BtnAct);
            ImGui.PushStyleColor(ImGuiCol.Button, V1Card);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V1BtnHi);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, V1BtnAct);
            ImGui.PushStyleColor(ImGuiCol.Header, Transparent);        // 选中/悬停底色改成自绘圆角块
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Transparent);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, Transparent);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, V1PopBg);
            ImGui.PushStyleColor(ImGuiCol.Separator, V1Border);
            ImGui.PushStyleColor(ImGuiCol.Border, V1BorderHi);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, V1Lime);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, V1Scroll);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, V1ScrollHi);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, V1ScrollHi);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6f);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
            // 只设首次尺寸：之后用户拖边角/右下角手柄调整，尺寸进 imgui.ini 下次沿用
            if (this.winPendingSize is { } pend)
                ImGui.SetNextWindowSize(pend, ImGuiCond.Always);
            else
                ImGui.SetNextWindowSize(new Vector2(980, 900), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(520, 480), new Vector2(3000, 2400));
            // NoScrollbar：窗口自身不出滚动条（列表在子窗口里滚），否则亮色下右边缘会挂一条灰条
            ImGui.Begin("MagicBucket", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
            try
            {
                this.DrawWindowChrome();
                // 内容从标题区下方开始
                ImGui.SetCursorScreenPos(ImGui.GetWindowPos() + new Vector2(16, HeadH + 6));
                this.DrawHuntList();
                this.TrackFilterChanges();   // 筛选变了就记下来，切大区 / 重载后原样恢复
            }
            catch (Exception ex)
            {
                // 绘制异常不甩给 Dalamud，但要留痕（同一异常只记一次，避免刷屏）
                if (this.drawErrorLogged != ex.GetType().FullName + ex.Message)
                {
                    this.drawErrorLogged = ex.GetType().FullName + ex.Message;
                    this.log.Error(ex, "[SrankFlag] 窗口绘制异常");
                }
            }
            ImGui.PopStyleColor(20);
            ImGui.PopStyleVar(9);
            ImGui.End();
        }
        catch
        {
            // 兜底：style 栈不平衡交给 ImGui 每帧重置
        }
        finally
        {
            this.EndFonts();
        }
    }

    // ─────────────────────── 最小化小方块 ───────────────────────

    /// <summary>插件目录里的 icon.png；读不到就退回画一个「水」字。</summary>
    private Dalamud.Interface.Textures.ISharedImmediateTexture? MiniIcon()
    {
        if (this.miniIcon != null || this.miniIconTried) return this.miniIcon;
        this.miniIconTried = true;
        try
        {
            var p = Path.Combine(this.pluginDirectory, "icon.png");
            if (!File.Exists(p)) return null;
            if (this.pi.GetService(typeof(ITextureProvider)) is ITextureProvider tp)
                this.miniIcon = tp.GetFromFileAbsolute(p);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[魔法水桶] 小方块图标加载失败");
        }
        return this.miniIcon;
    }

    /// <summary>主窗收起后留的那个方形小窗：图标铺满，单击展开，拖动挪位置。</summary>
    private void DrawMiniWindow()
    {
        return; // v1.2.6：旧版 ImGui 收起小窗已彻底废弃，本方法不可达，直接返回，永不绘制。
        const float S = 104f;
        if (this.miniPos is not { } p0)
        {
            var vp = ImGui.GetMainViewport();
            p0 = new Vector2(vp.Pos.X + vp.Size.X - S - 32f, vp.Pos.Y + 72f);
            this.miniPos = p0;
        }

        PushAutoStyle();   // 与「自动前往」窗共用一套主题色，明暗都跟手
        try
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
            ImGui.SetNextWindowPos(p0, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(S, S), ImGuiCond.Always);

            var open = true;
            var vis = ImGui.Begin("###magicbucket-mini", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);
            try
            {
                if (vis)
                {
                    var p = ImGui.GetCursorScreenPos();
                    var avail = ImGui.GetContentRegionAvail();
                    ImGui.InvisibleButton("##minibtn", avail);
                    var hov = ImGui.IsItemHovered();

                    if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
                    {
                        this.miniDragging = true;
                        this.miniPos = ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta;
                    }
                    if (ImGui.IsItemDeactivated())
                    {
                        if (!this.miniDragging) this.miniMode = false;   // 单击 = 展开
                        this.miniDragging = false;
                    }

                    var dl = ImGui.GetWindowDrawList();
                    var r0 = p + new Vector2(3f, 3f);
                    var r1 = p + avail - new Vector2(3f, 3f);
                    dl.AddRectFilled(r0, r1, ImGui.GetColorU32(V1Card), 11f);
                    if (this.MiniIcon()?.GetWrapOrDefault() is { } wrap)
                        dl.AddImage(wrap.Handle, r0, r1);
                    else
                    {
                        var tw = this.TW("水", 34f);
                        dl.AddText(this.FontFor(34f), 34f,
                            new Vector2(r0.X + (r1.X - r0.X - tw) / 2f, r0.Y + (r1.Y - r0.Y - 40f) / 2f),
                            ImGui.GetColorU32(V1AccentInk), "水");
                    }

                    if (hov && !this.miniDragging) ImGui.SetTooltip("点一下展开主窗\n拖动可挪位置");
                }
            }
            finally
            {
                ImGui.End();
            }
            ImGui.PopStyleVar(2);
        }
        finally
        {
            PopAutoStyle();
        }
    }

    private const float HeadH = 62f;

    /// <summary>窗体装饰：圆角底板、S 徽章标题、DC 芯片、ET/LT 时钟、关闭键、右下缩放手柄。使用 Dalamud 默认字体（自定义 GameFontHandle 会崩 CLR，勿回加）。</summary>
    private void DrawWindowChrome()
    {
        var wmin = ImGui.GetWindowPos();
        var wmax = wmin + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var dcName = this.srankData?.data?.dc ?? "—";

        // 底板：近黑 + 圆角 + 1px 描边
        dl.AddRectFilled(wmin, wmax, ImGui.GetColorU32(V1Bg), 10f);
        // 顶部柔光：从标题区往下淡出，顶栏和列表之间多一层层次（左右内缩，不越出圆角）
        var glowC = ImGui.GetColorU32(lightTheme
            ? new Vector4(0f, 0f, 0f, 0.018f)
            : new Vector4(1f, 1f, 1f, 0.045f));
        dl.AddRectFilledMultiColor(wmin + new Vector2(12, 1), new Vector2(wmax.X - 12, wmin.Y + 96),
            glowC, glowC, ImGui.GetColorU32(Transparent), ImGui.GetColorU32(Transparent));
        dl.AddRect(wmin + new Vector2(0.5f, 0.5f), wmax - new Vector2(0.5f, 0.5f), ImGui.GetColorU32(V1Border), 10f);

        // 左侧 S 徽章（酸黄圆角方块）—— 顶栏压到 62px，给列表多留一行
        var bp = wmin + new Vector2(18, 13);
        const float bsz = 36f;
        dl.AddRectFilled(bp, bp + new Vector2(bsz, bsz), ImGui.GetColorU32(V1Accent), 8f);
        var sw = this.TW("水", 22f);
        dl.AddText(this.FontFor(22f), 22f, bp + new Vector2(bsz / 2 - sw / 2, 4), ImGui.GetColorU32(V1OnAccent), "水");

        // 标题 / 副标题
        var titleX = bp.X + bsz + 12 - wmin.X;
        const float titleFs = 19f;
        dl.AddText(this.FontFor(titleFs), titleFs, wmin + new Vector2(titleX, 8), ImGui.GetColorU32(V1Text),
            this.settingsOpen ? "魔法水桶 · 高级设置" : "魔法水桶");
        dl.AddText(this.FontFor(12f), 12f, wmin + new Vector2(titleX, 33), ImGui.GetColorU32(V1Dim), "/// 熊站 · 可触发监控");

        // 顶栏底边：一条两端渐隐的细线，标题区和列表之间有干净的界线
        {
            var hy = wmin.Y + HeadH - 2f;
            var hx0 = wmin.X + 16f;
            var hx1 = wmax.X - 16f;
            var hm = (hx0 + hx1) * 0.5f;
            var hc = ImGui.GetColorU32(V1Border);
            var h0 = ImGui.GetColorU32(Transparent);
            dl.AddRectFilledMultiColor(new Vector2(hx0, hy), new Vector2(hm, hy + 1f), h0, hc, hc, h0);
            dl.AddRectFilledMultiColor(new Vector2(hm, hy), new Vector2(hx1, hy + 1f), hc, h0, h0, hc);
        }

        // 右上：关闭键
        const float csz = 26f;
        var cp = new Vector2(wmax.X - 44, wmin.Y + 16);
        var m = ImGui.GetMousePos();
        var hov = m.X >= cp.X && m.X <= cp.X + csz && m.Y >= cp.Y && m.Y <= cp.Y + csz;
        if (hov)
        {
            // 悬停：淡红底 + 红叉，比灰底更符合“关闭”的直觉
            dl.AddRectFilled(cp, cp + new Vector2(csz, csz),
                ImGui.GetColorU32(new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, 0.16f)), 7f);
            dl.AddRect(cp + new Vector2(0.5f, 0.5f), cp + new Vector2(csz - 0.5f, csz - 0.5f),
                ImGui.GetColorU32(new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, 0.45f)), 7f);
        }
        var xCol = ImGui.GetColorU32(hov ? V1Pink : V1Dim);
        dl.AddLine(cp + new Vector2(9, 9), cp + new Vector2(17, 17), xCol, 1.7f);
        dl.AddLine(cp + new Vector2(17, 9), cp + new Vector2(9, 17), xCol, 1.7f);
        if (hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            this.SavePrefs();      // 关窗顺手把筛选落盘，别等 1.2 秒的延迟写
            // v1.1.74：从「完整版」点 X 是回到原生主窗，不是彻底关闭。
            // v1.1.98：从齿轮进来的「高级设置」点 X 只收起设置，不关原生主窗
            if (this.settingsOpen) this.settingsOpen = false;
            else if (this.legacyMainOpen) this.legacyMainOpen = false;
            else this.mainWindowOpen = false;
            this.miniMode = false;
        }

        // 停止键：常驻在关闭键左边，任何自动流程（巡绿点 / 上坐骑 / 到点动作 / 进退副本）一键取消
        const float stopW = 58f, stopH = 26f;
        var sp = new Vector2(cp.X - 10f - stopW, wmin.Y + 16);
        var sHov = m.X >= sp.X && m.X <= sp.X + stopW && m.Y >= sp.Y && m.Y <= sp.Y + stopH;
        dl.AddRectFilled(sp, sp + new Vector2(stopW, stopH),
            ImGui.GetColorU32(sHov
                ? new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, 0.18f)
                : new Vector4(1f, 1f, 1f, 0.035f)), 7f);
        dl.AddRect(sp + new Vector2(0.5f, 0.5f), sp + new Vector2(stopW - 0.5f, stopH - 0.5f),
            ImGui.GetColorU32(new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, sHov ? 0.55f : 0.26f)), 7f);
        {
            const float sfs = 13f;
            const string stxt = "停止";
            const float sq = 7f;
            var stw = this.TW(stxt, sfs);
            var sInner = sq + 5f + stw;
            var sx0 = sp.X + (stopW - sInner) / 2f;
            var syc = sp.Y + stopH / 2f;
            dl.AddRectFilled(new Vector2(sx0, syc - sq / 2f), new Vector2(sx0 + sq, syc + sq / 2f),
                ImGui.GetColorU32(sHov ? V1Pink : new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, 0.72f)), 1.5f);
            dl.AddText(this.FontFor(sfs), sfs, new Vector2(sx0 + sq + 5f, syc - sfs * 0.7f),
                ImGui.GetColorU32(sHov ? V1Pink : V1Dim), stxt);
        }
        if (sHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) this.StopAll();
        if (sHov) ImGui.SetTooltip("停止全部：取消巡绿点 / 上坐骑 / 到点动作 / 进退副本（也可 /水桶 stop）");

        // 最小化键：收起成一个方形小窗（图标铺满），点小窗展开回来
        const float msz = 26f;
        var mp = new Vector2(sp.X - 10f - msz, wmin.Y + 16);
        var mHov = m.X >= mp.X && m.X <= mp.X + msz && m.Y >= mp.Y && m.Y <= mp.Y + msz;
        if (mHov)
        {
            dl.AddRectFilled(mp, mp + new Vector2(msz, msz),
                ImGui.GetColorU32(new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.14f)), 7f);
            dl.AddRect(mp + new Vector2(0.5f, 0.5f), mp + new Vector2(msz - 0.5f, msz - 0.5f),
                ImGui.GetColorU32(new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.42f)), 7f);
        }
        var mCol = ImGui.GetColorU32(mHov ? V1AccentInk : V1Dim);
        dl.AddLine(mp + new Vector2(9f, 17f), mp + new Vector2(17f, 17f), mCol, 1.7f);   // 一条横杠 = 最小化
        if (mHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) this.miniMode = true;
        if (mHov) ImGui.SetTooltip("最小化成小方块（点小方块展开）");

        // 顶栏右侧还有富余宽度，就把「搜索 + 刷新 + 展开全部」直接并进标题行（省掉一整行高度）；
        // 放不下时仍回到列表上方单独一行，由 DrawHuntList 画。
        var titleEndX = wmin.X + titleX + this.TW("魔法水桶", titleFs);
        var headLeft = titleEndX + 26f;
        var headRight = cp.X - 14f - 66f;   // 给停止键留位，搜索框不压上去
        this.searchInHeader = ImGui.GetWindowSize().X >= 820f && headRight - headLeft >= 330f;

        // 时钟（LT 右、ET 左）—— 窗口拉窄后右侧空间不够就只留标题，避免撞成一团；搜索行占用顶栏时挪到底栏
        var narrowChrome = ImGui.GetWindowSize().X < 660f;
        var tinierChrome = ImGui.GetWindowSize().X < 500f;
        var et = DateTimeOffset.FromUnixTimeSeconds((long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1440.0 / 70.0)).UtcDateTime;
        var etTxt = $"ET {et:HH:mm}";
        var ltTxt = $"LT {DateTime.Now:HH:mm}";
        var etW = this.TW(etTxt, 14f);
        var ltW = this.TW(ltTxt, 14f);
        var clockX = cp.X - 20 - 66 - etW;
        if (!tinierChrome && !this.searchInHeader)
        {
            dl.AddText(this.FontFor(14f), 14f, new Vector2(clockX, wmin.Y + 21), ImGui.GetColorU32(V1Text), etTxt);
            dl.AddText(this.FontFor(14f), 14f, new Vector2(clockX - ltW - 16, wmin.Y + 21), ImGui.GetColorU32(V1Dim), ltTxt);
        }

        // DC 芯片（窄窗隐藏，DC 名在标题副行里也能看）
        const float chipW = 108f, chipH = 28f;
        if (!narrowChrome && !this.searchInHeader)
        {
            var chp = new Vector2(clockX - ltW - 28 - chipW, wmin.Y + 15);
            dl.AddRectFilled(chp, chp + new Vector2(chipW, chipH), ImGui.GetColorU32(V1Bg2), 6f);
            dl.AddRect(chp + new Vector2(0.5f, 0.5f), chp + new Vector2(chipW - 0.5f, chipH - 0.5f), ImGui.GetColorU32(V1BorderHi), 6f);
            dl.AddText(this.FontFor(11f), 11f, chp + new Vector2(11, 8), ImGui.GetColorU32(V1Dim), "DC");
            dl.AddText(this.FontFor(14f), 14f, chp + new Vector2(33, 6), ImGui.GetColorU32(V1Text), dcName);
        }

        // 右下角缩放手柄：自绘拖拽（不依赖 ImGui 边缘缩放是否可用）
        var g1 = wmax - new Vector2(20, 20);
        var overGrip = m.X >= g1.X && m.X <= wmax.X && m.Y >= g1.Y && m.Y <= wmax.Y;
        if (overGrip && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            this.winResizing = true;
            this.winResizeStart = m;
            this.winResizeBase = ImGui.GetWindowSize();
        }
        if (this.winResizing)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var d = m - this.winResizeStart;
                this.winPendingSize = new Vector2(
                    Math.Clamp(this.winResizeBase.X + d.X, 520f, 3000f),
                    Math.Clamp(this.winResizeBase.Y + d.Y, 480f, 2400f));
            }
            else
            {
                this.winResizing = false;
                this.winPendingSize = null;
            }
        }
        var gripCol = overGrip || this.winResizing
            ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.95f)
            : new Vector4(V1Faint.X, V1Faint.Y, V1Faint.Z, V1Faint.W);
        for (var gi = 0; gi < 3; gi++)
        {
            var o = new Vector2(wmax.X - 8 - gi * 5, wmax.Y - 8);
            dl.AddLine(o, o + new Vector2(0, -(4 + gi * 5)), ImGui.GetColorU32(gripCol), 1.4f);
        }
        if (overGrip && !this.winResizing) ImGui.SetTooltip("拖动调整窗口大小");

        // 搜索行并进标题行：直接画在自绘 chrome 之上（控件走 ImGui，位置由屏幕坐标给定）
        if (this.searchInHeader)
        {
            ImGui.SetCursorScreenPos(new Vector2(headLeft, wmin.Y + 12f));
            this.DrawSearchBar(headRight - headLeft);
        }
    }

    /* ───── 熊站快照列表（与网页窗口同逻辑：CD 84h + 窗口 48h） ───── */

    private const double CdHours = 84, WinHours = 48;

    // V1 科技风：近黑 / 亮色两套配色，全部集中在这里，ApplyTheme 一改全局立即生效
    private static bool lightTheme;
    private static Vector4 V1Bg       = new(0.043f, 0.051f, 0.051f, 0.99f);  // 窗体底
    private static Vector4 V1Bg2      = new(0.059f, 0.071f, 0.067f, 1f);     // 次级底
    private static Vector4 V1Card     = new(0.075f, 0.086f, 0.082f, 1f);     // 卡片底
    private static Vector4 V1Input    = new(0.055f, 0.063f, 0.059f, 1f);     // 输入框底
    private static Vector4 V1Border   = new(0.118f, 0.137f, 0.129f, 1f);     // 普通描边
    private static Vector4 V1BorderHi = new(0.173f, 0.196f, 0.180f, 1f);     // 强描边
    private static Vector4 V1BorderSoft = new(0.173f, 0.196f, 0.180f, 0.55f);
    private static Vector4 V1Accent   = new(0.839f, 0.949f, 0.294f, 1f);     // 酸黄（实心块 / 描边）
    private static Vector4 V1AccentInk = new(0.839f, 0.949f, 0.294f, 1f);    // 酸黄当文字/细线用（亮色下压深）
    private static Vector4 V1Text     = new(0.949f, 0.961f, 0.937f, 1f);     // 正文
    private static Vector4 V1Dim      = new(0.455f, 0.502f, 0.475f, 1f);     // 次要文字
    private static Vector4 V1Faint    = new(0.455f, 0.502f, 0.475f, 0.45f);  // 最弱文字（无记录 / 空态）
    private static Vector4 V1FaintSoft = new(0.455f, 0.502f, 0.475f, 0.75f); // 弱文字（英文注 / 手柄）
    private static Vector4 V1Lime     = new(0.839f, 0.949f, 0.294f, 1f);     // 可触发
    private static Vector4 V1Mint     = new(0.231f, 0.890f, 0.541f, 1f);     // 冷却中
    private static Vector4 V1Pink     = new(1f, 0.302f, 0.553f, 1f);         // 已过窗
    private static Vector4 V1Panel    = new(0.059f, 0.071f, 0.067f, 1f);     // 内嵌面板底
    private static Vector4 V1PanelBorder = new(0.118f, 0.137f, 0.129f, 1f);
    private static Vector4 V1Hover     = new(1f, 1f, 1f, 0.045f);            // 悬停 / 选中
    private static Vector4 V1HoverMid  = new(1f, 1f, 1f, 0.07f);
    private static Vector4 V1HoverStrong = new(1f, 1f, 1f, 0.13f);
    private static Vector4 V1Sel       = new(1f, 1f, 1f, 0.03f);             // 展开行 / 选中行底色
    private static Vector4 V1Scroll    = new(1f, 1f, 1f, 0.08f);
    private static Vector4 V1ScrollHi  = new(1f, 1f, 1f, 0.14f);
    private static Vector4 V1FrameHi   = new(0.098f, 0.110f, 0.106f, 1f);    // 输入框悬停
    private static Vector4 V1BtnHi     = new(0.110f, 0.125f, 0.117f, 1f);    // 按钮悬停
    private static Vector4 V1BtnAct    = new(0.075f, 0.086f, 0.082f, 1f);    // 按钮按下
    private static Vector4 V1PopBg     = new(0.043f, 0.051f, 0.051f, 0.98f); // tooltip 底
    private static readonly Vector4 Transparent = new(0f, 0f, 0f, 0f);
    private static Vector4 V1Track    = new(0.086f, 0.098f, 0.094f, 1f);     // 进度条槽
    private static readonly Vector4 V1OnAccent = new(0.043f, 0.051f, 0.051f, 1f); // 酸黄块上的深色字

    /// <summary>切换配色。false = 近黑科技风（默认），true = 亮色。</summary>
    private static void ApplyTheme(bool light)
    {
        lightTheme = light;
        if (light)
        {
            // 亮色：追「极强对比」——底子越白、层次越分明，字越近纯黑；不发灰、不半透明。
            V1Bg = new Vector4(0.980f, 0.984f, 0.973f, 1f);      // #FAFBF8 窗底，不透明才干净
            V1Bg2 = new Vector4(0.898f, 0.910f, 0.878f, 1f);     // #E5E8E0 次级底，和窗底差一档
            V1Card = new Vector4(0.855f, 0.871f, 0.831f, 1f);    // #DADED4 卡片
            V1Input = new Vector4(1f, 1f, 1f, 1f);               // 纯白输入框，靠深描边界定
            V1Border = new Vector4(0.612f, 0.647f, 0.584f, 1f);  // #9CA595 画得出来的描边
            V1BorderHi = new Vector4(0.408f, 0.447f, 0.384f, 1f); // #687262 强描边
            V1BorderSoft = new Vector4(0.408f, 0.447f, 0.384f, 0.65f);
            V1Accent = new Vector4(0.792f, 0.902f, 0.235f, 1f);  // 亮酸黄（实心块 + 深字）
            V1AccentInk = new Vector4(0.259f, 0.333f, 0.000f, 1f); // #425500 白底上的酸黄字
            V1Text = new Vector4(0.035f, 0.047f, 0.027f, 1f);    // #090C07 近纯黑
            V1Dim = new Vector4(0.157f, 0.184f, 0.145f, 1f);     // #282F25 深灰绿（不再是浅灰）
            V1Faint = new Vector4(0.157f, 0.184f, 0.145f, 0.62f);
            V1FaintSoft = new Vector4(0.157f, 0.184f, 0.145f, 0.82f);
            V1Lime = new Vector4(0.208f, 0.353f, 0.000f, 1f);    // 可触发：深橄榄
            V1Mint = new Vector4(0.000f, 0.353f, 0.196f, 1f);    // 冷却：深绿
            V1Pink = new Vector4(0.635f, 0.043f, 0.196f, 1f);    // 过窗：深洋红
            V1Panel = new Vector4(0.929f, 0.937f, 0.914f, 1f);
            V1PanelBorder = new Vector4(0.612f, 0.647f, 0.584f, 1f);
            V1Hover = new Vector4(0f, 0f, 0f, 0.06f);
            V1HoverMid = new Vector4(0f, 0f, 0f, 0.10f);
            V1HoverStrong = new Vector4(0f, 0f, 0f, 0.16f);
            V1Sel = new Vector4(0f, 0f, 0f, 0.07f);
            V1Scroll = new Vector4(0f, 0f, 0f, 0.28f);
            V1ScrollHi = new Vector4(0f, 0f, 0f, 0.42f);
            V1FrameHi = new Vector4(0.976f, 0.980f, 0.969f, 1f);
            V1BtnHi = new Vector4(0.804f, 0.824f, 0.780f, 1f);
            V1BtnAct = new Vector4(0.757f, 0.780f, 0.729f, 1f);
            V1PopBg = new Vector4(1f, 1f, 1f, 0.99f);            // tooltip 纯白
            V1Track = new Vector4(0.780f, 0.800f, 0.745f, 1f);   // 进度条槽，明显能看见
        }
        else
        {
            V1Bg = new Vector4(0.043f, 0.051f, 0.051f, 0.99f);
            V1Bg2 = new Vector4(0.059f, 0.071f, 0.067f, 1f);
            V1Card = new Vector4(0.075f, 0.086f, 0.082f, 1f);
            V1Input = new Vector4(0.055f, 0.063f, 0.059f, 1f);
            V1Border = new Vector4(0.118f, 0.137f, 0.129f, 1f);
            V1BorderHi = new Vector4(0.173f, 0.196f, 0.180f, 1f);
            V1BorderSoft = new Vector4(0.173f, 0.196f, 0.180f, 0.55f);
            V1Accent = new Vector4(0.839f, 0.949f, 0.294f, 1f);
            V1AccentInk = new Vector4(0.839f, 0.949f, 0.294f, 1f);
            V1Text = new Vector4(0.949f, 0.961f, 0.937f, 1f);
            V1Dim = new Vector4(0.455f, 0.502f, 0.475f, 1f);
            V1Faint = new Vector4(0.455f, 0.502f, 0.475f, 0.45f);
            V1FaintSoft = new Vector4(0.455f, 0.502f, 0.475f, 0.75f);
            V1Lime = new Vector4(0.839f, 0.949f, 0.294f, 1f);
            V1Mint = new Vector4(0.231f, 0.890f, 0.541f, 1f);
            V1Pink = new Vector4(1f, 0.302f, 0.553f, 1f);
            V1Panel = new Vector4(0.059f, 0.071f, 0.067f, 1f);
            V1PanelBorder = new Vector4(0.118f, 0.137f, 0.129f, 1f);
            V1Hover = new Vector4(1f, 1f, 1f, 0.045f);
            V1HoverMid = new Vector4(1f, 1f, 1f, 0.07f);
            V1HoverStrong = new Vector4(1f, 1f, 1f, 0.13f);
            V1Sel = new Vector4(1f, 1f, 1f, 0.03f);
            V1Scroll = new Vector4(1f, 1f, 1f, 0.08f);
            V1ScrollHi = new Vector4(1f, 1f, 1f, 0.14f);
            V1FrameHi = new Vector4(0.098f, 0.110f, 0.106f, 1f);
            V1BtnHi = new Vector4(0.110f, 0.125f, 0.117f, 1f);
            V1BtnAct = new Vector4(0.075f, 0.086f, 0.082f, 1f);
            V1PopBg = new Vector4(0.043f, 0.051f, 0.051f, 0.98f);
            V1Track = new Vector4(0.086f, 0.098f, 0.094f, 1f);
        }
    }

    /// <summary>主题开关落到插件目录，下次进游戏沿用。</summary>
    private string ThemeFile => Path.Combine(this.pluginDirectory, "theme.txt");

    private void LoadTheme()
    {
        try
        {
            this.lightMode = File.Exists(this.ThemeFile) &&
                             File.ReadAllText(this.ThemeFile).Trim().Equals("light", StringComparison.OrdinalIgnoreCase);
        }
        catch { this.lightMode = false; }
        ApplyTheme(this.lightMode);
    }

    private void SetLightTheme(bool light)
    {
        this.lightMode = light;
        ApplyTheme(light);
        try { File.WriteAllText(this.ThemeFile, light ? "light" : "dark"); }
        catch { /* 写不进去只影响下次启动，不影响当前显示 */ }
    }

    /// <summary>12 色色板：饱和度/明度统一，同一大区内按服序号依次取，
    /// 8 服的区也不会撞色。豆豆柴现有 5 色正好是色板前 5 位（水晶塔橙 / 银泪湖绿 /
    /// 太阳海岸黄绿 / 伊修加德天蓝 / 红茶川粉），所以原配色一点没变。</summary>
    private static readonly Vector4[] WorldPalette =
    {
        new(0.96f, 0.655f, 0.26f, 1f),   // 1  橙
        new(0.08f, 0.76f, 0.22f, 1f),    // 2  绿
        new(0.69f, 0.76f, 0.08f, 1f),    // 3  黄绿
        new(0.44f, 0.72f, 1f, 1f),       // 4  天蓝
        new(1f, 0.41f, 0.50f, 1f),       // 5  粉
        new(0.76f, 0.55f, 1f, 1f),       // 6  紫
        new(0.15f, 0.80f, 0.78f, 1f),    // 7  青
        new(1f, 0.58f, 0.20f, 1f),       // 8  深橙
        new(0.88f, 0.78f, 0.25f, 1f),    // 9  金
        new(0.36f, 0.62f, 1f, 1f),       // 10 蓝
        new(1f, 0.45f, 0.72f, 1f),       // 11 玫红
        new(0.55f, 0.85f, 0.28f, 1f),    // 12 草绿
    };

    private Vector4 WorldColor(string w)
    {
        // 按「大区序号 × 5 + 服序号」取色：各大区起点错开，同区内连续不重复
        var idx = WorldPalette.Length - 1;   // 找不到的服给个中性草绿，不再一灰到底
        if (this.srankData?.dcs is { Count: > 0 } dcs)
        {
            for (var di = 0; di < dcs.Count; di++)
            {
                var wi = Array.IndexOf(dcs[di].worlds, w);
                if (wi < 0) continue;
                idx = (di * 5 + wi) % WorldPalette.Length;
                break;
            }
        }
        var c = WorldPalette[idx];
        // 亮色底上这些高饱和色偏亮，压深一档当文字色才读得清
        return lightTheme ? new Vector4(c.X * 0.72f, c.Y * 0.72f, c.Z * 0.72f, c.W) : c;
    }

    /// <summary>v1.2.7：把缺 mt 的怪补成站点口径（0.6×rt，两只例外查表）。
    /// 站点公式：pair = row[2] ? (hunt.mt || hunt.rt) : hunt.rt；T 主导时用 mt 覆盖 cd/win。
    /// 老用户插件目录的 srank_data.json 是用户状态文件，部署时绝不能覆盖，只能在运行时补。</summary>
    private void EnsureMaintPairs()
    {
        var d = this.srankData;
        if (d is null) return;
        void Fill(HuntData[]? hunts)
        {
            if (hunts is null) return;
            foreach (var h in hunts)
            {
                if (h is null || h.mt is { Length: >= 2 }) continue;
                h.mt = MaintPairOverride.TryGetValue(h.en, out var pair)
                    ? (double[])pair.Clone()
                    : new[] { h.cd * 0.6, h.win * 0.6 };
            }
        }
        Fill(d.data?.hunts);
        foreach (var dc in d.dcs) Fill(dc?.hunts);
    }

    private void LoadSrankData()
    {
        try
        {
            // Dalamud 从字节流加载插件，Assembly.Location 为空；要用 AssemblyLocation（DLL 真实路径）
            var p = Path.Combine(this.pluginDirectory, "srank_data.json");
            if (!File.Exists(p))
            {
                this.dataError = "找不到数据文件：" + p;
                this.log.Warning("[SrankFlag] 找不到数据文件 {0}", p);
                return;
            }
            this.srankData = JsonSerializer.Deserialize<DataFile>(File.ReadAllText(p));
            this.EnsureMaintPairs();     // v1.2.7：旧数据文件没有 mt，运行时补齐，部署不必覆盖用户状态文件
            this.dataError = this.srankData?.data is null && this.srankData?.dcs.Count is null or 0
                ? "数据文件解析为空：" + p : "";
            // 多大大区文件：按「上次选的大区 → 游戏所在大区 → 第一个」挑一份挂到 data 上
            if (this.srankData?.dcs is { Count: > 0 } list)
            {
                var want = this.LoadDcPref();
                if (want.Length == 0)
                {
                    var cur = this.CurrentWorldName();
                    want = list.FirstOrDefault(x => x.worlds.Contains(cur, StringComparer.OrdinalIgnoreCase))?.dc ?? "";
                }
                var pick = list.FirstOrDefault(x => x.dc == want) ?? list[0];
                this.srankData.data = pick;
                this.currentDc = pick.dc;
            }
            if (this.srankData?.data is { } d)
            {
                // 老格式没有 snapshot；多大大区文件用写入时间兜底，底栏不会显示 1970
                if (d.snapshot <= 0)
                    d.snapshot = new DateTimeOffset(File.GetLastWriteTimeUtc(p)).ToUnixTimeSeconds();
                this.ApplyFilter(this.currentDc, d);
            }
            this.MergeBearCache();
            this.ApplySonarKills();   // v1.2.12：本地实时击杀时间最后回放，保证比 HTTP/缓存更晚的值不被回滚
            // v1.2.12「已被触发」：先读黑名单，再用磁盘里的死亡时间清掉「离线期间已被打死」的条目。
            // 这是唯一的批量解锁路径，没有任何超时逻辑。
            this.LoadTriggered();
            this.SweepTriggeredByDeath();
        }
        catch (Exception ex)
        {
            this.dataError = "读取 srank_data.json 失败：" + ex.Message;
            this.log.Error(ex, "[SrankFlag] 读取 srank_data.json 失败");
        }
    }

    /// <summary>上次选的大区（prefs.json 优先，兼容老的 dc.txt），没有就返回空串。</summary>
    private string LoadDcPref()
    {
        this.LoadPrefs();
        if (this.prefs.dc.Length > 0) return this.prefs.dc;
        try
        {
            var p = Path.Combine(this.pluginDirectory, "dc.txt");
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        catch { return ""; }
    }

    private void LoadPrefs()
    {
        if (this.prefsLoaded) return;
        this.prefsLoaded = true;
        try
        {
            var p = Path.Combine(this.pluginDirectory, "prefs.json");
            if (!File.Exists(p)) return;
            var x = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(p));
            if (x is not null) this.prefs = x;
            this.prefs.byDc ??= new Dictionary<string, DcFilter>(StringComparer.Ordinal);
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag] prefs.json 读取失败：" + ex.Message); }
    }

    private void SavePrefs()
    {
        try
        {
            this.CaptureFilter();
            this.filterDirtyAt = DateTime.MinValue;
            File.WriteAllText(Path.Combine(this.pluginDirectory, "prefs.json"),
                JsonSerializer.Serialize(this.prefs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag] prefs.json 写入失败：" + ex.Message); }
        // v1.1.98：设置保存后跳过原生主窗 200ms 节流，下一帧立即反映最新配置
        try { this.nativeHud?.ForceMainRefresh(); } catch { }
    }

    /// <summary>把当前界面上的筛选收进内存（不落盘）。</summary>
    private void CaptureFilter()
    {
        if (this.currentDc.Length == 0) return;
        this.prefs.dc = this.currentDc;
        this.prefs.byDc[this.currentDc] = new DcFilter
        {
            worlds = this.worldFilter.ToList(),
            worldsSet = true,
            patches = this.patchSel.ToList(),
            onlyOpen = this.onlyOpen,
            search = this.searchText,
        };
    }

    /// <summary>按 prefs 恢复某个大区的筛选；没记录就全选服务器、其余保持默认。</summary>
    private void ApplyFilter(string dc, SnapshotData d)
    {
        this.LoadPrefs();
        this.worldFilter.Clear();
        this.patchSel.Clear();
        this.onlyOpen = false;   // v1.2.3：默认显示全部 S，非豆豆柴大区未刷新时也不至于空白
        this.searchText = "";
        if (this.prefs.byDc.TryGetValue(dc, out var f) && f.worldsSet)
        {
            foreach (var w in f.worlds)
                if (d.worlds.Contains(w, StringComparer.OrdinalIgnoreCase)) this.worldFilter.Add(w);
            foreach (var x in f.patches) this.patchSel.Add(x);
            this.onlyOpen = f.onlyOpen;
            this.searchText = f.search ?? "";
        }
        else
        {
            foreach (var w in d.worlds) this.worldFilter.Add(w);
        }
        this.lastFilterSig = this.FilterSig();
        this.filterDirtyAt = DateTime.MinValue;
    }

    private string FilterSig() =>
        this.currentDc + "|" + string.Join(",", this.worldFilter.OrderBy(x => x, StringComparer.Ordinal))
        + "|" + string.Join(",", this.patchSel.OrderBy(x => x, StringComparer.Ordinal))
        + "|" + this.onlyOpen + "|" + this.searchText;

    /// <summary>每帧扫一次筛选指纹：变了先记内存，手停 1.2 秒再写盘
    /// （搜索框逐字输入时不会一路 IO；插件被关掉也不会丢）。</summary>
    private void TrackFilterChanges()
    {
        var sig = this.FilterSig();
        if (sig == this.lastFilterSig)
        {
            if (this.filterDirtyAt != DateTime.MinValue &&
                (DateTime.UtcNow - this.filterDirtyAt).TotalSeconds > 1.2)
                this.SavePrefs();
            return;
        }
        this.lastFilterSig = sig;
        this.CaptureFilter();
        this.filterDirtyAt = DateTime.UtcNow;
    }

    /// <summary>切到另一个大区：换数据，筛选（服 / 版本 / 只看开闸 / 搜索）按该大区上次的状态恢复，
    /// 没去过的大区才回到全选。切回原来那个区时，你挑好的条件还在。</summary>
    private void SelectDc(string dc)
    {
        if (this.srankData?.dcs is not { Count: > 0 } list) return;
        var pick = list.FirstOrDefault(x => x.dc == dc) ?? list[0];
        if (this.srankData.data == pick) return;
        this.CaptureFilter();                 // 先存下当前大区的筛选
        this.srankData.data = pick;
        this.currentDc = pick.dc;
        this.ApplyFilter(pick.dc, pick);      // 再恢复目标大区上次的筛选
        this.bearStatus = "尚未拉取";
        this.lastBearFetch = DateTime.MinValue;
        try { File.WriteAllText(Path.Combine(this.pluginDirectory, "dc.txt"), pick.dc); } catch { }
        this.SavePrefs();
        if (this.currentDc == SelfHostedDc)
            this.MaybeStartBearFetch(true);   // 豆豆柴：切换即拉（站点优先）
        else
        {
            // 非豆豆柴：切换不自动打公开熊站，等用户点刷新；避免后台静默请求第三方
            this.bearStatus = "当前大区数据需手动刷新";
        }
        this.Print(this.currentDc == SelfHostedDc
            ? $"已切换到 {pick.dc}（{pick.worlds.Length} 个服 · {pick.hunts.Length} 只 S）"
            : $"已切换到 {pick.dc}（{pick.worlds.Length} 个服 · {pick.hunts.Length} 只 S）；该大区数据来自公开熊站，请点右上角「刷新」手动拉取");
    }

    /* ───── 熊站自动拉取（POST tracker.beartoolkit.com/api/getHuntInfo） ───── */

    private const string BearApi = "https://tracker.beartoolkit.com/api/getHuntInfo";
    /// <summary>站点导出的本大区 live 快照（后台每 5 分钟从熊站批量同步一次）——一次请求拿全，比逐条问快得多。</summary>
    private const string LiveApi = "http://119.29.119.179/api/live?dc=";
    private const string SelfHostedDc = "豆豆柴";   // ★ 只有豆豆柴走自建站自动+手动拉取；其余大区改用公开熊站且只能手动刷新
    private static readonly TimeSpan BearInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BearForceFloor = TimeSpan.FromSeconds(10);  // 强制刷新也至少隔 10s，防狂点连发
    private const int BearFallbackCooldownMin = 60;   // 兜底回退公开熊站失败/空结果的熔断窗口（分钟）
    private const int BearFallbackMaxPairs = 60;      // 兜底一次最多问 60 条配对，杜绝上千条爆发
    private static readonly Lazy<HttpClient> BearHttp = new(() =>
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://tracker.beartoolkit.com/srank");
        return h;
    });

    private DateTime lastBearFetch = DateTime.MinValue;
    private DateTime lastBearFallback = DateTime.MinValue;  // 兜底回退公开熊站的熔断时间戳
    private DateTime lastBearOk = DateTime.MinValue;
    private bool bearFetching;
    private string bearStatus = "尚未拉取";

    // ───── 全局重启时间 T（站点 🔧 维护模式「维护结束时间」最大值，全国服共享）─────
    private const string RestartApi = "http://119.29.119.179/api/restart";
    private double restartAt = 0; // 全局重启时间 T（epoch 秒），0 = 无维护重启
    private DateTime lastRestartFetch = DateTime.MinValue;

    private static async Task<(string hunt, string world, double ts)> BearQueryAsync(string huntEn, string world)
    {
        var body = JsonSerializer.Serialize(new { HuntName = huntEn, WorldName = world });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await BearHttp.Value.PostAsync(BearApi, content).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(txt);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return (huntEn, world, 0);
        double best = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (e.TryGetProperty("Canceled", out var c) && c.ValueKind == JsonValueKind.True) continue;
            if (e.TryGetProperty("Time", out var t) && t.ValueKind == JsonValueKind.Number)
                best = Math.Max(best, t.GetDouble());
        }
        return (huntEn, world, best);
    }

    /// <summary>每帧调度的 T 刷新（60s 节流）。force=true 用于「手动刷新」按钮，立即拉一次，
    /// 让插件的概率数字当场和主站对齐（不等待节流窗口）。</summary>
    private void MaybeRefreshRestart(bool force)
    {
        if (!force && DateTime.UtcNow - this.lastRestartFetch <= TimeSpan.FromSeconds(60)) return;
        this.lastRestartFetch = DateTime.UtcNow;
        _ = Task.Run(() => this.RefreshRestartAsync());
    }

    /// <summary>后台刷新全局重启时间 T（站点维护结束时间最大值，全国服共享）。失败沿用上次值。0 额外熊站请求。</summary>
    private async Task RefreshRestartAsync()
    {
        try
        {
            using var resp = await BearHttp.Value.GetAsync(RestartApi).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("restartAt", out var r) && r.ValueKind == JsonValueKind.Number)
            {
                var t = r.GetDouble();
                this.restartAt = t > 0 ? t : 0;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning("[SrankFlag] 重启时间拉取失败，沿用上次值：" + ex.Message);
        }
    }

    /// <summary>击杀时间拉取。豆豆柴：自建站自动+手动（站点优先，熊站党底）。
    /// 其他大区：改用公开熊站，且严格限手动刷新（force=true），后台定时（force=false）一律禁止。</summary>
    private void MaybeStartBearFetch(bool force)
    {
        if (this.bearFetching) return;
        if (this.srankData?.data is not { } d || d.hunts.Length == 0 || d.worlds.Length == 0) return;

        bool selfHosted = this.currentDc == SelfHostedDc;

        // ── 非豆豆柴：公开熊站只能手动刷新，后台自动拉取绝不允许 ──
        if (!selfHosted)
        {
            if (!force) return;                                  // 后台 5 分钟定时 → 直接 return，绝不静默打公开 API
            if (DateTime.UtcNow - this.lastBearFetch < BearForceFloor) return;  // 手动也受 10s 冷却，防狂点连发
            this.bearFetching = true;
            this.lastBearFetch = DateTime.UtcNow;
            this.bearStatus = "从公开熊站拉取中…";
            var hunts = d.hunts.Select(h => h.en).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var worlds = d.worlds.ToList();
            var pairs = hunts.SelectMany(h => worlds.Select(w => (h, w))).ToList();
            _ = Task.Run(async () =>
            {
                var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);
                try
                {
                    // 公开熊站逐条问（限并发，别打爆第三方）；手动刷新不做熔断/限量截断，保证拿到全量
                    await Parallel.ForEachAsync(pairs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (p, _) =>
                    {
                        try
                        {
                            var (h, w, ts) = await BearQueryAsync(p.h, p.w).ConfigureAwait(false);
                            if (ts > 0) bag[h + "|" + w] = ts;
                        }
                        catch { /* 单条失败忽略，保留旧值 */ }
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.log.Warning("[SrankFlag] 公开熊站拉取失败：" + ex.Message);
                }
                this.framework.RunOnTick(() =>
                {
                    try { this.ApplyBearResult(bag); }
                    catch (Exception ex) { this.log.Warning("[SrankFlag] 熊站数据写入失败：" + ex.Message); }
                    this.bearFetching = false;
                });
            });
            return;
        }

        // ── 豆豆柴：保持原有逻辑（站点优先 + 熊站党底，自动 + 手动）──
        if (!force && DateTime.UtcNow - this.lastBearFetch < BearInterval) return;
        if (force && DateTime.UtcNow - this.lastBearFetch < BearForceFloor) return;  // 连点刷新不再紧接连发

        this.bearFetching = true;
        this.lastBearFetch = DateTime.UtcNow;
        this.bearStatus = "拉取中…";

        var hunts2 = d.hunts.Select(h => h.en).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var worlds2 = d.worlds.ToList();
        var pairs2 = hunts2.SelectMany(h => worlds2.Select(w => (h, w))).ToList();
        var dc = this.currentDc;

        _ = Task.Run(async () =>
        {
            var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            // v1.2.12：主站「这只怪现在活着/被触发」的可选信号（接口给了才有），与击杀时间分开收集
            var trigBag = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            // 负信号（接口明确说「没在打」）→ 立刻解除拉黑
            var negBag = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);

            // 首选：站点 /api/live?dc= 一次拿回本大区全服 × 全怪的击杀时间（后台 5 分钟刷一次，比逐条快两个数量级）
            if (dc.Length > 0)
            {
                try
                {
                    using var resp = await BearHttp.Value
                        .GetAsync(LiveApi + Uri.EscapeDataString(dc)).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(txt);
                        // v1.2.12：「已被触发」主站源。线上接口目前只有击杀时间，这里解析的是
                        // triggered / engaged / spawned / relay 这类可选容器；没有就静默跳过。
                        CollectTriggeredJson(doc.RootElement, trigBag, negBag);
                        if (doc.RootElement.TryGetProperty("live", out var lv) && lv.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var w in lv.EnumerateObject())
                            {
                                if (w.Value.ValueKind != JsonValueKind.Object) continue;
                                foreach (var hh in w.Value.EnumerateObject())
                                {
                                    double best = 0;
                                    if (hh.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var row in hh.Value.EnumerateArray())
                                        {
                                            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                                            if (row.GetArrayLength() >= 3 && row[2].ValueKind == JsonValueKind.True) continue; // 维护/取消的记录不算
                                            if (row[1].ValueKind != JsonValueKind.Number) continue;
                                            var t = row[1].GetDouble();
                                            if (t > best) best = t;
                                        }
                                    }
                                    if (best > 0) bag[hh.Name + "|" + w.Name] = best;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.log.Warning("[SrankFlag] 站点 live 拉取失败，退回熊站逐条：" + ex.Message);
                }
            }

            // 党底：站点没数据（新大区 / 接口挂了）才回退公开熊站；限量 + 降并发 + 熔断，避免爆发式打第三方
            if (bag.IsEmpty && DateTime.UtcNow - this.lastBearFallback > TimeSpan.FromMinutes(BearFallbackCooldownMin))
            {
                var capped = pairs2.Count > BearFallbackMaxPairs ? pairs2.GetRange(0, BearFallbackMaxPairs) : pairs2;
                try
                {
                    await Parallel.ForEachAsync(capped, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (p, _) =>
                    {
                        try
                        {
                            var (h, w, ts) = await BearQueryAsync(p.h, p.w).ConfigureAwait(false);
                            if (ts > 0) bag[h + "|" + w] = ts;
                        }
                        catch { /* 单条失败忽略，保留旧值 */ }
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.log.Warning("[SrankFlag] 熊站拉取失败：" + ex.Message);
                }
                this.lastBearFallback = DateTime.UtcNow;   // 不论成败都记时间 → 熔断窗口内不再重试
            }

            this.framework.RunOnTick(() =>
            {
                try { this.ApplyBearResult(bag, trigBag, negBag); }
                catch (Exception ex) { this.log.Warning("[SrankFlag] 熊站数据写入失败：" + ex.Message); }
                this.bearFetching = false;
            });
        });
    }
    /* ───── 实时排点（站点 /api/tracker-points/snapshot，60 秒一次） ─────
       站点后台会持续轮询熊站，把「每服每线每个点位有没有被踩过」存成快照；
       绿点 = 这一轮还没被踩过的点（B 怪点永远不算）。
       本地 srank_data.json 里的点位颜色只是生成那一刻的，早就过期了，这里用实时快照盖掉。 */

    private const string PointsSnapshotApi = "http://119.29.119.179/api/tracker-points/snapshot";
    private static readonly TimeSpan PointsInterval = TimeSpan.FromSeconds(60);

    private sealed class LiveMap
    {
        public int[] ids { get; set; } = [];
        public float[][] pts { get; set; } = [];
        public int[] bmob { get; set; } = [];
    }

    private readonly Dictionary<string, LiveMap> liveMaps = new(StringComparer.Ordinal);                 // 怪 en → 地图几何
    private readonly Dictionary<string, Dictionary<int, char>> liveStates = new(StringComparer.Ordinal); // 服|怪|线 → 已踩点位色标
    private DateTime lastPointsFetch = DateTime.MinValue;
    private bool pointsFetching;
    private double pointsUpdatedAt;
    private string pointsStatus = "排点未同步";
    private bool pointsProbeLogged;

    private void MaybeStartPointsFetch(bool force)
    {
        if (this.pointsFetching) return;
        if (!force && DateTime.UtcNow - this.lastPointsFetch < PointsInterval) return;

        this.pointsFetching = true;
        this.lastPointsFetch = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            string? err = null;
            var maps = new Dictionary<string, LiveMap>(StringComparer.Ordinal);
            var states = new Dictionary<string, Dictionary<int, char>>(StringComparer.Ordinal);
            double updated = 0;
            try
            {
                using var resp = await BearHttp.Value.GetAsync(PointsSnapshotApi).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;

                if (root.TryGetProperty("maps", out var ms) && ms.ValueKind == JsonValueKind.Object)
                {
                    foreach (var m in ms.EnumerateObject())
                    {
                        var lm = new LiveMap();
                        if (m.Value.TryGetProperty("pointIds", out var ids)) lm.ids = ReadInts(ids);
                        if (m.Value.TryGetProperty("pts", out var pp)) lm.pts = ReadPts(pp);
                        if (m.Value.TryGetProperty("bmob", out var bm)) lm.bmob = ReadInts(bm);
                        if (lm.ids.Length > 0) maps[m.Name] = lm;
                    }
                }

                if (root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rows.EnumerateArray())
                    {
                        if (r.ValueKind != JsonValueKind.Object) continue;
                        var world = r.TryGetProperty("world", out var w1) ? w1.GetString() ?? "" : "";
                        var hunt = r.TryGetProperty("hunt", out var h1) ? h1.GetString() ?? "" : "";
                        var line = r.TryGetProperty("line", out var l1) && l1.ValueKind == JsonValueKind.Number ? l1.GetInt32() : 1;
                        if (world.Length == 0 || hunt.Length == 0) continue;

                        var dict = new Dictionary<int, char>();
                        if (r.TryGetProperty("points", out var ps) && ps.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in ps.EnumerateObject())
                            {
                                if (!int.TryParse(p.Name, out var id) || p.Value.ValueKind != JsonValueKind.Object) continue;
                                // 只有 state 为真才进表（＝已踩）：红=已验证 / 黄=未验证 / 灰=管理员标注
                                if (!(p.Value.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.True)) continue;
                                var verified = p.Value.TryGetProperty("verified", out var vf) && vf.ValueKind == JsonValueKind.True;
                                var admin = p.Value.TryGetProperty("admin", out var ad) && ad.ValueKind == JsonValueKind.True;
                                dict[id] = admin ? 'a' : verified ? 'r' : 'y';
                            }
                        }

                        states[world + "|" + hunt + "|" + line] = dict;
                    }
                }

                if (root.TryGetProperty("updatedAt", out var up) && up.ValueKind == JsonValueKind.Number)
                    updated = up.GetDouble();
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            this.framework.RunOnTick(() =>
            {
                this.pointsFetching = false;
                if (err is not null)
                {
                    // 失败就留着上一次的好数据，别把群里的绿点一夜清空
                    this.pointsStatus = "排点同步失败";
                    return;
                }

                this.liveMaps.Clear();
                foreach (var kv in maps) this.liveMaps[kv.Key] = kv.Value;
                this.liveStates.Clear();
                foreach (var kv in states) this.liveStates[kv.Key] = kv.Value;
                this.pointsUpdatedAt = updated > 0 ? updated : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                this.pointsStatus = "排点 " + DateTimeOffset.FromUnixTimeSeconds((long)this.pointsUpdatedAt).ToLocalTime().ToString("HH:mm");
                if (!this.pointsProbeLogged)
                {
                    this.pointsProbeLogged = true;
                    this.log.Info($"[SrankFlag] 实时排点已接入：{this.liveMaps.Count} 张图 / {this.liveStates.Count} 条服·线");
                }
            });
        });
    }

    private static int[] ReadInts(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<int>();
        foreach (var x in e.EnumerateArray())
            if (x.ValueKind == JsonValueKind.Number) list.Add(x.GetInt32());
        return list.ToArray();
    }

    private static float[][] ReadPts(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<float[]>();
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Array) continue;
            var it = x.EnumerateArray();
            if (!it.MoveNext()) continue;
            var ax = it.Current.GetSingle();
            if (!it.MoveNext()) continue;
            list.Add([ax, it.Current.GetSingle()]);
        }
        return list.ToArray();
    }

    /// <summary>实时绿点：站点排点里这一轮还没被踩过、且不是 B 怪点的点位。
    /// 没有实时数据时返回 null，调用方回退到本地快照（离线/刚启动也能看）。</summary>
    private List<Pt>? LiveGreens(string huntEn, string world)
    {
        if (this.liveMaps.Count == 0) return null;
        if (!this.liveMaps.TryGetValue(huntEn, out var map) || map.ids.Length == 0) return null;

        // 人在这个服就按所在这条线排点，否则按 1 线（绝大多数 S 只有一条线）
        var want = this.BestPointLine(world);
        Dictionary<int, char>? states = null;
        if (this.liveStates.TryGetValue(world + "|" + huntEn + "|" + want, out var s1)) states = s1;
        else if (want != 1 && this.liveStates.TryGetValue(world + "|" + huntEn + "|1", out var s2)) states = s2;
        if (states is null) return null; // 这个服这一轮还没排点，别假装全是绿点

        var bm = new HashSet<int>(map.bmob);
        var list = new List<Pt>();
        for (var i = 0; i < map.ids.Length; i++)
        {
            var id = map.ids[i];
            if (bm.Contains(id)) continue;        // B 怪点永远不是绿点
            if (states.ContainsKey(id)) continue; // 已经踩过
            var p = i < map.pts.Length ? map.pts[i] : new[] { 0f, 0f };
            list.Add(new Pt(id, p[0], p[1], 'g'));
        }
        return list;
    }

    private int BestPointLine(string world)
    {
        try
        {
            if (!string.Equals(this.CurrentWorldName(), world, StringComparison.OrdinalIgnoreCase)) return 1;
            var cur = (int)this.GetCurrentInstance();
            return cur > 0 ? cur : 1;
        }
        catch { return 1; }
    }

    private string BearCachePath => Path.Combine(this.pluginDirectory, "srank_live.json");

    /// <summary>把「en|world → 击杀时间」缓存到本地，插件重载/离线时也能保留最新值。</summary>
    private void SaveBearCache()
    {
        try
        {
            if (this.srankData?.data is not { } d) return;
            var kills = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var h in d.hunts)
                foreach (var w in h.worlds)
                    if (w.lastDeath > 0) kills[h.en + "|" + w.world] = w.lastDeath;
            var doc = new { snapshot = d.snapshot, kills };
            File.WriteAllText(this.BearCachePath, JsonSerializer.Serialize(doc));
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag] 写缓存失败：" + ex.Message); }
    }

    /// <summary>载入本地快照后，把缓存里更新的击杀时间合并进来。</summary>
    private void MergeBearCache()
    {
        try
        {
            if (this.srankData?.data is not { } d) return;
            if (!File.Exists(this.BearCachePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(this.BearCachePath));
            if (!doc.RootElement.TryGetProperty("kills", out var kills)) return;
            var map = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in kills.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) map[p.Name] = p.Value.GetDouble();
            if (doc.RootElement.TryGetProperty("snapshot", out var sn) && sn.ValueKind == JsonValueKind.Number)
                d.snapshot = Math.Max(d.snapshot, sn.GetDouble());

            foreach (var h in d.hunts)
                foreach (var w in h.worlds)
                    if (map.TryGetValue(h.en + "|" + w.world, out var ts) && ts > w.lastDeath)
                        w.lastDeath = ts;
            foreach (var h in d.hunts)
                if (h.worlds.Length > 0)
                {
                    var mx = h.worlds.Max(x => x.lastDeath);
                    if (mx > h.lastDeath) h.lastDeath = mx;
                }
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag] 读缓存失败：" + ex.Message); }
    }

    private void ApplyBearResult(System.Collections.Concurrent.ConcurrentDictionary<string, double> map,
                                 System.Collections.Concurrent.ConcurrentDictionary<string, double>? triggered = null,
                                 System.Collections.Concurrent.ConcurrentDictionary<string, double>? negative = null)
    {
        if (this.srankData?.data is not { } d) return;
        var hit = 0;
        var changed = 0;
        // 本轮刚被主站确认「死了」的组合：接下来落主站拉黑信号时必须跳过，
        // 否则「刚死解锁 → 同一轮又被 live 锁上」，永远等不到冷却结束。
        var justDied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in d.hunts)
        {
            if (h.worlds.Length == 0) continue;
            var touched = false;
            foreach (var wd in h.worlds)
            {
                if (!map.TryGetValue(h.en + "|" + wd.world, out var ts) || ts <= 0) continue;
                hit++;
                if (ts > wd.lastDeath)
                {
                    wd.lastDeath = ts;
                    touched = true;
                    // v1.2.12 历史：主站报了更晚的击杀时间 → 记一条 Kill/MainSite。
                    // EventTime 用站点给的 ts（真实击杀时刻），不是本地 now；
                    // 与 Sonar 那条不去重（Source 不同），正好用来核对站点延迟与概率。
                    this.history?.Add(h.cn, wd.world, h.map,
                        HistoryEventType.Kill, HistorySource.MainSite, ts);
                    // v1.2.12：主站确认这只已经死了 → 解除「已被触发」黑名单（并把这次击杀记为已消化）
                    this.ClearTriggered(h.en, wd.world, "主站击杀时间更新", ts);
                    justDied.Add(TrigKey(h.en, wd.world));
                }
            }
            if (touched)
            {
                var mx = h.worlds.Max(x => x.lastDeath);
                if (mx > h.lastDeath) h.lastDeath = mx;
                changed++;
            }
        }
        d.snapshot = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        this.lastBearOk = DateTime.UtcNow;
        this.SaveBearCache();
        this.bearStatus = hit == 0
            ? $"拉取失败 · {DateTime.Now:HH:mm}"
            : $"已同步 {changed} 只 · {DateTime.Now:HH:mm}";

        // v1.2.12 历史：主站数据更新后再检测一次「新进闸」→ Spawn/MainSite。
        this.NoteMainSiteOpenStates(d);

        // v1.2.12「已被触发」：三段状态机。
        // 顺序很重要：先按最新的 lastDeath 解锁 → 再落主站接口给的明确信号/否认 → 最后按窗口分段
        // 统一处理（冷却锁 / 闸内放行 / 闸后未死）。窗口判断放最后，才不会和刚写入的 lastDeath 打架。
        this.SweepTriggeredByDeath();
        this.ApplyMainSiteTriggered(triggered, negative, justDied);
        this.SweepTriggeredWindow();

        this.nativeHud?.ForceHistoryRefresh();
    }

    /// <summary>
    /// v1.2.12：主站「新的 isOpen 状态」→ 一条出生 / 进闸历史（Source=MainSite）。
    /// 判定口径与 BuildMainSnapshot 逐字一致（维护 T 主导时用 mt，否则用 rt），
    /// 这样历史窗口里的「出生」和主界面显示的「开闸」永远对得上。
    /// 只遍历当前大区 d.hunts（与自动前往一致，不跨大区）；首次见到的键只记基线不写历史，
    /// 否则插件一启动就会把当前所有开闸怪刷成几百条。
    /// MergeBearCache / ApplySonarKills 属于本地缓存回放，不推历史，避免每次重载重复记录。
    /// </summary>
    private void NoteMainSiteOpenStates(SnapshotData d)
    {
        if (this.history is null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            foreach (var h in d.hunts)
            {
                foreach (var w in h.worlds)
                {
                    if (w.lastDeath <= 0) continue;
                    var eff = Math.Max(w.lastDeath, this.restartAt);
                    var maint = this.restartAt > 0 && this.restartAt > w.lastDeath;
                    var useCd = maint && h.mt.Length >= 1 ? h.mt[0] : h.cd;
                    var useWin = maint && h.mt.Length >= 2 ? h.mt[1] : h.win;
                    var st = Calc(eff, now, useCd, useWin).State;
                    var openAt = eff + (useCd > 0 ? useCd : CdHours) * 3600;
                    this.history.NoteOpenState(this.currentDc + "|" + h.en + "|" + w.world,
                        st == "open", h.cn, w.world, h.map, openAt);
                }
            }
        }
        catch (Exception ex)
        {
            try { this.log.Warning("[SrankFlag.History] 主站进闸记录失败：" + ex.Message); } catch { }
        }
    }

    /// <summary>按怪自己的冷却算状态：进闸 openAt = lastDeath + cd*3600，强制 forceAt = openAt + win*3600。
    /// v1.2.7：与主站 site.html 的 SRKBoardStyle / r(t,o) 逐字对齐 ——
    ///   cold(now&lt;openAt) → 0%；forced(now&gt;=forceAt) → 100%（clamp 到 100，不再返回 &gt;100 的裸值）；
    ///   open → clamp((now-openAt)/(win*3600)*100, 0, 100)。
    /// 站点 /api/live 只回基础数据（lastDeath + isMaint），概率本来就是前端算的，单位一律 0..100（不是 0..1）。
    /// now 用 UTC Unix 秒（DateTimeOffset.UtcNow.ToUnixTimeSeconds），与站点 Date.now()/1000 同一基准，
    /// 站点里 +8*3600 只用于「格式化成本地时间显示」，不参与概率计算，所以没有 8 小时错位。
    /// cd/win 由调用方按 isMaint 选 rt 或 mt 传入；缺省（老数据）才退回全局 84h/48h。</summary>
    private static (string State, double Eta, double Pct) Calc(double lastDeath, double now, double cdH = 0, double winH = 0)
    {
        if (lastDeath <= 0) return ("unknown", 0, 0);
        var cd = cdH > 0 ? cdH : CdHours;
        var win = winH > 0 ? winH : WinHours;
        var start = lastDeath + cd * 3600;
        var end = start + win * 3600;
        if (now < start) return ("cd", start - now, 0);                 // 站点 cold → 0%
        if (now >= end) return ("over", now - end, 100);                // 站点 forced → clamped 100%
        // 唯一可能产生 0..100 数值的分支；显式 clamp，杜绝 215% / 825% 这类溢出数值流进 UI
        var pct = Math.Clamp((now - start) / (win * 3600) * 100.0, 0.0, 100.0);
        return ("open", end - now, pct);
    }

    private static string Dur(double sec)
    {
        var s = Math.Max(0, (long)sec);
        var d = s / 86400; var h = s % 86400 / 3600; var m = s % 3600 / 60;
        return d > 0 ? $"{d}d{h:00}h" : h > 0 ? $"{h}h{m:00}m" : $"{m}m";
    }

    private static Vector4 StateColor(string st) => st switch
    {
        "open" => V1Lime,
        "cd" => V1Mint,
        "over" => V1Pink,
        _ => V1Dim,
    };

    /// <summary>#RRGGBB → Vector4。</summary>
    private static Vector4 Hex(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
    }

    /// <summary>版本主色：每个资料片一个干净的单色（不用渐变，深浅底都能压得住）。</summary>
    private static Vector4 PatchColor(string key) => key switch
    {
        "HW" => Hex("#3E86D6"),   // 3.0 苍穹之禁城 · 天蓝
        "SB" => Hex("#C05B45"),   // 4.0 红莲之狂潮 · 砖红
        "ShB" => Hex("#8B5BD6"),  // 5.0 暗影之逆焰 · 紫
        "EW" => Hex("#2FA3B5"),   // 6.0 晓月之终途 · 青
        "DT" => Hex("#C6A046"),   // 7.0 金曦之遗辉 · 金
        _ => Hex("#7D8894"),      // 2.0 重生之境 · 石板灰蓝
    };

    /// <summary>两色线性混合（t=0 全 a，t=1 全 b）。前景叠底一律用它，别靠 alpha 硬叠。</summary>
    private static Vector4 Mix(Vector4 a, Vector4 b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, 1f);

    /// <summary>点位瓦片（左键插旗 / 右键插旗+飞行寻路）。perRow=0 时紧跟当前行、按行宽自动折行；否则按固定列数排网格。
    /// 一排放不下时先只铺一排 + 「+N」，点开才换行铺满全部（可再点「-」收回）。</summary>
    private void PointTiles(string map, string huntEn, string world, string huntCn, List<Pt> greens, float gap, int perRow = 0, float tileH = 28f)
    {
        var key = huntEn + "|" + world;
        var expanded = this.tilesExpanded.Contains(key);
        var lineRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;

        // 一行能放几个：按「当前行尾到窗口右边」剩下的宽度算（每个瓦片 42 + 间距 4）
        var perLine = perRow > 0
            ? perRow
            : Math.Max(1, (int)Math.Floor((lineRight - (ImGui.GetItemRectMax().X + gap) + 4f) / 46f));

        var overflow = greens.Count > perLine;
        var shown = overflow && !expanded ? Math.Max(1, perLine - 1) : greens.Count;   // 留一格给「+N」

        for (var i = 0; i < shown; i++)
        {
            var pt = greens[i];
            ImGui.PushID($"{huntEn}|{world}|{pt.Id}");

            if (perRow > 0)
            {
                if (i % perRow != 0) ImGui.SameLine(0f, 4f);
            }
            else if (i == 0)
            {
                ImGui.SameLine(0f, gap);
            }
            else if (ImGui.GetItemRectMax().X + 4f + 42f <= lineRight + 0.5f)
            {
                ImGui.SameLine(0f, 4f);
            }

            var c = PtColor(pt.S);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(c.X, c.Y, c.Z, 0.16f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(c.X, c.Y, c.Z, 0.35f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(c.X, c.Y, c.Z, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(c.X, c.Y, c.Z, 0.4f));
            ImGui.PushStyleColor(ImGuiCol.Text, c);
            if (ImGui.Button(pt.Id.ToString(), new Vector2(42f, tileH)))
            {
                this.Flag(map, pt.X, pt.Y);
            }
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                this.NavTo(map, pt.X, pt.Y, "", huntCn);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{pt.Id} 号点 · {PtLabel(pt.S)} · X {pt.X:0.0} Y {pt.Y:0.0}\n左键=插旗 · 右键=插旗+飞行寻路");
            }

            ImGui.PopID();
        }

        if (!overflow) return;

        // 「+N / -」按钮：放在同一行末尾放得下就同一行，放不下就换行
        var atLineEnd = perRow > 0
            ? shown % perRow == 0
            : ImGui.GetItemRectMax().X + 4f + 40f > lineRight + 0.5f;
        if (shown > 0 && !atLineEnd) ImGui.SameLine(0f, 4f);

        ImGui.PushID(key + "|more");
        ImGui.PushStyleColor(ImGuiCol.Button, V1Bg2);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V1Card);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, V1Card);
        ImGui.PushStyleColor(ImGuiCol.Text, V1Dim);
        if (ImGui.Button(expanded ? "-" : $"+{greens.Count - shown}", new Vector2(40f, tileH)))
        {
            if (expanded) this.tilesExpanded.Remove(key);
            else this.tilesExpanded.Add(key);
        }
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(expanded ? "收起全部绿点" : $"还有 {greens.Count - shown} 个绿点，点开全部显示");
        }
        ImGui.PopID();
    }

    private static Vector4 PtColor(char s)
    {
        var c = s switch
        {
            'g' => new Vector4(0.20f, 0.85f, 0.35f, 1f),
            'y' => new Vector4(0.90f, 0.80f, 0.25f, 1f),
            'r' => new Vector4(0.85f, 0.30f, 0.30f, 1f),
            'a' => new Vector4(0.55f, 0.55f, 0.55f, 1f),
            _ => new Vector4(0.35f, 0.35f, 0.35f, 1f), // b = B怪点
        };
        // 亮色底上提亮色会发白，压深一档才看得清
        return lightTheme ? new Vector4(c.X * 0.62f, c.Y * 0.62f, c.Z * 0.62f, c.W) : c;
    }

    private static string PtLabel(char s) => s switch
    {
        'g' => "可踩",
        'y' => "玩家排除",
        'r' => "熊站排除",
        'a' => "管理员排除",
        _ => "B怪点",
    };

    /// <summary>同网页版 huntPoints：按服的排除清单把编号点分类。</summary>
    private static List<Pt> HuntPoints(PointSet? ps, string world)
    {
        var list = new List<Pt>();
        if (ps is null) return list;
        var wf = ps.w.TryGetValue(world, out var f) ? f : null;
        for (var i = 0; i < ps.ids.Length; i++)
        {
            var id = ps.ids[i];
            var p = i < ps.pts.Length ? ps.pts[i] : [0f, 0f];
            var s = ps.bm.Contains(id) ? 'b'
                : (wf?.a?.Contains(id) ?? false) ? 'a'
                : (wf?.r?.Contains(id) ?? false) ? 'r'
                : (wf?.y?.Contains(id) ?? false) ? 'y'
                : 'g';
            list.Add(new Pt(id, p[0], p[1], s));
        }
        return list;
    }

    private static string DecodeBuf(byte[] buf)
    {
        var n = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    /// <summary>V1 细进度条：暗槽 + 实心填充 + 轻微刻度（画在指定屏幕坐标，不动光标）。</summary>
    private static void BarAt(ImDrawListPtr dl, Vector2 p, float w, float hgt, Vector4 c, float pct)
    {
        var r = Math.Max(2f, hgt * 0.5f);
        dl.AddRectFilled(p, p + new Vector2(w, hgt), ImGui.GetColorU32(V1Track), r);
        var fw = w * Math.Clamp(pct / 100f, 0f, 1f);
        if (fw > 1f)
        {
            dl.AddRectFilled(p, p + new Vector2(fw, hgt), ImGui.GetColorU32(c), r);
            // 填充段顶部一道高光，暗底上更像“有厚度”的条
            if (fw > 5f)
                dl.AddRectFilledMultiColor(new Vector2(p.X + 1.5f, p.Y + 1f), new Vector2(p.X + fw - 1.5f, p.Y + hgt * 0.55f),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f)), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f)),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f)), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f)));
            // 每 10% 一道暗刻线，做出分段推进的感觉
            for (var i = 1; i < 10; i++)
            {
                var x = p.X + w * i / 10f;
                if (x < p.X + fw - 1f)
                    dl.AddLine(new Vector2(x, p.Y + 1), new Vector2(x, p.Y + hgt - 1),
                        ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.22f)));
            }
        }
    }

    /// <summary>行内迷你进度条（无描边，高度 4px）。</summary>
    private static void MiniBar(ImDrawListPtr dl, Vector2 p, float w, float hgt, Vector4 c, float pct)
    {
        var r = hgt * 0.5f;   // 圆头小条
        dl.AddRectFilled(p, p + new Vector2(w, hgt), ImGui.GetColorU32(V1Track), r);
        var fw = w * Math.Clamp(pct / 100f, 0f, 1f);
        if (fw <= 0.6f) return;
        dl.AddRectFilled(p, p + new Vector2(fw, hgt), ImGui.GetColorU32(c), r);
        if (fw > 4f)   // 顶部高光
            dl.AddRectFilledMultiColor(new Vector2(p.X + 1f, p.Y + 0.8f), new Vector2(p.X + fw - 1f, p.Y + hgt * 0.55f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f)), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f)));
    }

    /// <summary>搜索框 + 「刷新数据 / 展开全部」。窗口够宽时直接并进标题行（省一整行），不够宽就留在列表上方。
    /// width = 这一行可用的宽度；返回行高。</summary>
    private float DrawSearchBar(float width)
    {
        const float btnH = 32f;
        const float padL = 9f, padR = 8f;
        var anyOpen = this.expandedHunts.Count > 0;
        // 按钮只占「文字宽 + 内边距」，不再按剩余宽度等分拉伸（文字短就窄，不留一大片空白）
        float BW(string t) => ImGui.CalcTextSize(t).X + 24f;
        var bwRefresh = Math.Max(BW(this.bearFetching ? "…  拉取中" : "刷新数据"), BW("…  拉取中"));
        var bwExpand = Math.Max(BW(anyOpen ? "收起全部" : "展开全部"), BW("展开全部"));
        var bwAuto = Math.Max(BW("自动前往"), BW("自动"));
        var searchW = width - bwRefresh - bwExpand - bwAuto - padL - padR * 2;
        // 窗口窄到搜索框放不下时才退成短文案（刷新 / 展开），否则一律用完整文字
        var shortBtns = searchW < 140f;
        if (shortBtns)
        {
            bwRefresh = Math.Max(BW(this.bearFetching ? "…" : "刷新"), BW("刷新"));
            bwExpand = Math.Max(BW(anyOpen ? "收起" : "展开"), BW("展开"));
            searchW = width - bwRefresh - bwExpand - bwAuto - padL - padR * 2;
        }
        var sb = Encoding.UTF8.GetBytes(this.searchText);
        Array.Clear(this.searchBuf);
        Array.Copy(sb, this.searchBuf, Math.Min(sb.Length, this.searchBuf.Length - 1));
        ImGui.SetNextItemWidth(Math.Max(96f, searchW));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(11, 8));
        ImGui.InputTextWithHint("##search", "搜索怪名 / 地图", this.searchBuf, ImGuiInputTextFlags.None);
        ImGui.PopStyleVar();
        this.searchText = DecodeBuf(this.searchBuf);
        ImGui.SameLine(0, padL);
        if (ImGui.Button((shortBtns ? (this.bearFetching ? "…" : "刷新") : (this.bearFetching ? "…  拉取中" : "刷新数据")) + "##refresh",
                new Vector2(bwRefresh, btnH)))
        {
            if (this.srankData?.data is null) this.LoadSrankData();
            this.MaybeStartBearFetch(true);
            this.MaybeStartPointsFetch(true);
            this.Print("正在从熊站拉取最新击杀时间和实时排点…");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"每 15 分钟自动拉取熊站最新。\n状态：{this.bearStatus}");
        ImGui.SameLine(0, padR);
        if (ImGui.Button((!anyOpen ? (shortBtns ? "展开" : "展开全部") : (shortBtns ? "收起" : "收起全部")) + "##expandall",
                new Vector2(bwExpand, btnH)))
        {
            if (anyOpen) this.expandedHunts.Clear();
            else foreach (var en in this.lastListed) this.expandedHunts.Add(en);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("展开 / 收起所有怪的触发点明细");
        ImGui.SameLine(0, padR);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.Text, V1Accent);
        if (ImGui.Button((shortBtns ? "自动" : "自动前往") + "##openauto", new Vector2(bwAuto, btnH)))
            this.autoRunOpen = !this.autoRunOpen;
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("打开自动前往悬浮窗：勾选服务器和 S，一路跑完\n（也可以 /水桶 auto）");
        return btnH;
    }

    private void DrawHuntList()
    {
        if (this.srankData?.data is not { } data)
        {
            ImGui.TextDisabled("数据未加载：" + (this.dataError.Length > 0 ? this.dataError : "未知原因"));
            if (ImGui.SmallButton("重试加载")) this.LoadSrankData();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 全局重启时间 T 后台刷新（60s 节流，失败沿用上次值）
        if (DateTime.UtcNow - this.lastRestartFetch > TimeSpan.FromSeconds(60))
        {
            this.lastRestartFetch = DateTime.UtcNow;
            _ = Task.Run(() => this.RefreshRestartAsync());
        }
        var dl = ImGui.GetWindowDrawList();

        // 聚合状态（同网页 visible()+agg()）
        var pri = new Dictionary<string, int> { ["open"] = 0, ["cd"] = 1, ["over"] = 2, ["unknown"] = 3 };
        var all = data.hunts.ToList();
        List<(HuntData h, List<(WorldDeath w, string st, double eta, double pct, double fill)> ws, string st, double eta, double pct, string bestWorld)> rows = new();
        foreach (var h in all)
        {
            if (this.patchSel.Count > 0 && !this.patchSel.Contains(h.patch)) continue;
            if (this.searchText.Length > 0 &&
                !(h.cn + " " + h.en + " " + h.map).Contains(this.searchText, StringComparison.OrdinalIgnoreCase))
                continue;
            var ws = h.worlds
                .Where(w => w.lastDeath > 0 && this.worldFilter.Contains(w.world))
                .Select(w =>
                {
                    // 全局重启时间 T：站点 🔧 维护模式的维护结束时间最大值，全国服共享。
                    // T 主导时（该服上次击杀早于 T，即停机期间 CD 被打断）用维护后再生 mt；否则用正常 rt。
                    var eff = Math.Max(w.lastDeath, this.restartAt);
                    var maint = this.restartAt > 0 && this.restartAt > w.lastDeath;
                    var useCd = maint && h.mt.Length >= 1 ? h.mt[0] : h.cd;
                    var useWin = maint && h.mt.Length >= 2 ? h.mt[1] : h.win;
                    var c = Calc(eff, now, useCd, useWin);
                    return (w, c.State, c.Eta, c.Pct, Math.Min(100.0, c.Pct));
                })
                .OrderBy(x => pri.GetValueOrDefault(x.State, 3)).ThenBy(x => x.Eta)
                .ToList();
            // HTML visible()：没有任何选中服有记录的怪直接不显示
            if (ws.Count == 0) continue;
            rows.Add((h, ws, ws[0].State, ws[0].Eta, ws[0].Pct, ws[0].w.world));
        }
        rows.Sort((a, b) =>
        {
            var c = pri.GetValueOrDefault(a.st, 3) - pri.GetValueOrDefault(b.st, 3);
            return c != 0 ? c : a.eta.CompareTo(b.eta);
        });

        // 默认只列「已进闸」的怪（有服务器开着触发窗口）；切到显示全部就列全量
        var listed = this.onlyOpen ? rows.Where(r => r.st == "open").ToList() : rows;
        this.lastListed = listed.Select(r => r.h.en).ToList();   // 顶栏「展开全部」在标题行里画，用上一帧的列表

        var nOpen = rows.Count(r => r.st == "open");
        // ── 搜索框 + 两个方形按钮（按钮只占文字宽度） ──
        if (!this.searchInHeader) this.DrawSearchBar(ImGui.GetContentRegionAvail().X);

        // ── 服务器筛选 chips（圆点 + 世界色，全部 = 酸黄） ──
        void Chip(string label, Vector4 c, bool on, System.Action toggle, bool first,
            string? soloWorld = null, bool current = false, string? tip = null)
        {
            var cw = this.TW(label, 13.5f) + 36;
            if (!first)
            {
                // 放不下就折行（窗口拉窄时 chips 排成多行，不溢出窗口）
                var cx = ImGui.GetCursorPosX();
                if (cx + 6 + cw <= ImGui.GetContentRegionMax().X + 0.5f) ImGui.SameLine(0, 6);
            }
            const float chh = 27f;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
            ImGui.PushStyleColor(ImGuiCol.Button,
                current ? new Vector4(c.X, c.Y, c.Z, 0.28f) : on ? new Vector4(c.X, c.Y, c.Z, 0.14f) : V1Card);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? new Vector4(c.X, c.Y, c.Z, 0.20f) : V1HoverMid);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(c.X, c.Y, c.Z, 0.26f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Border, current ? new Vector4(c.X, c.Y, c.Z, 1f)
                : on ? new Vector4(c.X, c.Y, c.Z, 0.75f) : V1Border);
            var clicked = ImGui.Button($"##chip{label}", new Vector2(cw, chh));
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar();
            var rMin = ImGui.GetItemRectMin();
            var rMax = ImGui.GetItemRectMax();
            dl.AddCircleFilled(new Vector2(rMin.X + 13, (rMin.Y + rMax.Y) / 2), 3.6f,
                ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, on || current ? 1f : 0.5f)), 12);
            if (current)
            {
                // 自己所在服：亮一圈 + 更实的底，一眼能认出来
                dl.AddCircle(new Vector2(rMin.X + 13, (rMin.Y + rMax.Y) / 2), 6.2f,
                    ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, 0.95f)), 16, 1.5f);
                dl.AddRect(rMin - new Vector2(1, 1), rMax + new Vector2(1, 1),
                    ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, 0.9f)), 7f, 0, 1.3f);
            }
            dl.AddText(this.FontFor(13.5f), 13.5f, new Vector2(rMin.X + 22, rMin.Y + (chh - 13.5f * 1.28f) / 2f),
                ImGui.GetColorU32(current ? V1Text : on ? c : V1Dim), label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tip ?? ((soloWorld is null
                    ? (on ? $"{label}：已启用，点击关闭" : $"{label}：点击启用")
                    : $"{label}：左键切换 · 右键只看这个服") + (current ? "\n你现在在这个服务器" : "")));
            if (clicked) toggle();
            if (soloWorld is not null && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                this.worldFilter.Clear();
                this.worldFilter.Add(soloWorld);
            }
        }

        // 大区切换：点一下按 豆豆柴 → 莫古力 → 猫小胖 → 陆行鸟 循环
        var dcList = this.srankData?.dcs ?? new List<SnapshotData>();
        if (dcList.Count > 0)
        {
            var di = dcList.FindIndex(x => x.dc == this.currentDc);
            if (di < 0) di = 0;
            var nextDc = dcList[(di + 1) % dcList.Count].dc;
            var curH = dcList[di].hunts.Length;
            Chip(this.currentDc, V1AccentInk, true, () => this.SelectDc(nextDc), true, null, false,
                $"当前大区：{this.currentDc} · {dcList[di].worlds.Length} 服 · {curH} 只 S\n点击切换到 {nextDc}");
        }
        Chip("全部", V1AccentInk, this.worldFilter.Count == data.worlds.Length, () =>
        {
            if (this.worldFilter.Count == data.worlds.Length)
            {
                this.worldFilter.Clear();
            }
            else
            {
                this.worldFilter.Clear();
                foreach (var x in data.worlds) this.worldFilter.Add(x);
            }
        }, dcList.Count == 0);
        foreach (var w in data.worlds)
        {
            var on = this.worldFilter.Contains(w);
            Chip(w, WorldColor(w), on, () =>
            {
                if (on) this.worldFilter.Remove(w); else this.worldFilter.Add(w);
            }, false, w, string.Equals(this.CurrentWorldName(), w, StringComparison.OrdinalIgnoreCase));
        }

        // ── 资料片徽章（对齐站点 srk-version-badge：渐变色块 + 亮描边 + 白字） ──
        ImGui.Dummy(new Vector2(0, 2));
        var pc = all.GroupBy(h => h.patch).ToDictionary(g => g.Key, g => g.Count());
        var tabFirst = true;
        foreach (var (key, label) in new[]
                 {
                     ("ALL", "全部"),
                     ("ARR", "版本 2.0"), ("HW", "版本 3.0"), ("SB", "版本 4.0"),
                     ("ShB", "版本 5.0"), ("EW", "版本 6.0"), ("DT", "版本 7.0"),
                 })
        {
            var isAll = key == "ALL";
            var cur = isAll ? this.patchSel.Count == 0 : this.patchSel.Contains(key);
            var cnt = key == "ALL" ? all.Count : pc.GetValueOrDefault(key);
            var txt = isAll ? "全部" : label.Replace("版本 ", string.Empty);   // 版本号只留「5.0」，不重复写六遍「版本」
            const float badgeH = 26f, fs = 15.5f, badgeR = 7f, dotR = 3.2f, dotGap = 5f, padX = 10f;
            var textW = this.TW(txt, fs);
            var contentW = dotR * 2f + dotGap + textW;   // 圆点 + 间距 + 文字，当作一整块
            var tw2 = contentW + padX * 2f;              // 药丸宽 = 内容 + 左右等宽内边距
            if (!tabFirst)
            {
                var cx = ImGui.GetCursorPosX();
                if (cx + 7 + tw2 <= ImGui.GetContentRegionMax().X + 0.5f) ImGui.SameLine(0, 7);
            }
            tabFirst = false;
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V1Hover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, V1HoverMid);
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 0f));
            var hit = ImGui.Button($"##tab{key}", new Vector2(tw2, badgeH));
            ImGui.PopStyleColor(5);
            var tMin = ImGui.GetItemRectMin();
            var tMax = ImGui.GetItemRectMax();
            // 整块（圆点+文字）在药丸里水平居中
            var blockX = tMin.X + (tw2 - contentW) / 2f;
            var dotCX = blockX + dotR;
            var textLeft = blockX + dotR * 2f + dotGap;

            // 扁平药丸：底色＝卡片色混一点版本主色（不透明、不用渐变），
            // 圆点是版本色，文字用主题正文色 —— 深浅两套底上都清楚，也不会糊成一排色块。
            var hue = isAll ? V1Accent : PatchColor(key);
            var tint = cur ? 0.34f : cnt == 0 ? 0.10f : 0.17f;
            var bg = Mix(V1Card, hue, tint);
            // 亮色下亮色系（酸黄、青）压在白底上看不见，描边/圆点先跟正文色混一下再画
            var mark = lightTheme ? Mix(hue, V1Text, 0.32f) : hue;
            dl.AddRectFilled(tMin, tMax, ImGui.GetColorU32(bg), badgeR);
            dl.AddRect(tMin + new Vector2(0.5f, 0.5f), tMax - new Vector2(0.5f, 0.5f),
                ImGui.GetColorU32(cur ? mark : V1Border), badgeR, 0, cur ? 1.6f : 1f);
            var dotC = cur ? mark : Mix(V1Bg, hue, cnt == 0 ? 0.42f : 0.72f);
            var dotCY = (tMin.Y + tMax.Y) / 2f;
            dl.AddCircleFilled(new Vector2(dotCX, dotCY), dotR, ImGui.GetColorU32(dotC), 14);
            if (cur)   // 选中：圆点外面再套一道同色细环，形状更明确
                dl.AddCircle(new Vector2(dotCX, dotCY), 6.5f,
                    ImGui.GetColorU32(new Vector4(mark.X, mark.Y, mark.Z, 0.55f)), 18, 1.2f);
            dl.AddText(this.FontFor(fs), fs,
                new Vector2(textLeft, tMin.Y + (badgeH - fs * 1.3f) / 2f),
                ImGui.GetColorU32(cur ? V1Text : Mix(V1Text, V1Bg, 0.25f)), txt);
            if (hit)
            {
                if (isAll) this.patchSel.Clear();                          // 「全部」= 清空选择
                else if (!this.patchSel.Remove(key)) this.patchSel.Add(key); // 其余多选：点一次加、再点去掉
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isAll
                    ? $"全部版本 · {cnt} 只\n（点一下清空版本筛选）"
                    : $"{label} · {cnt} 只\n左键多选：可同时看多个版本");
        }

        // 右侧开关：只看开闸 + 亮色/暗色；窄窗放不下就另起一行
        var foldW = this.TW("只看开闸", 14f) + 48;
        var themeW = this.TW("亮色", 14f) + 48;
        var rightW = foldW + themeW + 7f;
        var rightAvail = ImGui.GetContentRegionMax().X - ImGui.GetCursorPosX();
        if (rightAvail >= rightW + 16f)
            ImGui.SameLine(ImGui.GetContentRegionMax().X - rightW);
        Chip("只看开闸", this.onlyOpen ? V1AccentInk : V1Dim, this.onlyOpen, () => this.onlyOpen = !this.onlyOpen, true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.onlyOpen
                ? $"只显示已进闸（有触发窗口开着）的 S：{nOpen} 只\n已折叠未进闸的 {rows.Count - nOpen} 只，点击显示全部"
                : "显示全部 S（含冷却中 / 已过窗的未进闸怪），点击只留已进闸的");
        Chip(this.lightMode ? "暗色" : "亮色", this.lightMode ? V1AccentInk : V1Dim, this.lightMode,
            () => this.SetLightTheme(!this.lightMode), false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.lightMode ? "切回近黑科技风" : "切换亮色主题（白天/深色游戏画面下更清楚）");

        // ───── 列表 ─────
        if (listed.Count == 0)
        {
            if (rows.Count > 0)
            {
                // 有记录但一只都没进闸：说清楚为什么是空的，并给一键展开
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.TextDisabled("NO READY");
                ImGui.TextWrapped($"当前没有开闸的 S，已折叠 {rows.Count} 只未进闸的怪（冷却中 / 已过窗）。");
                ImGui.Dummy(new Vector2(0, 4));
                if (ImGui.Button("显示全部 S", new Vector2(132, 34))) this.onlyOpen = false;
            }
            else
            {
                ImGui.TextDisabled("NO MATCH");
                ImGui.TextWrapped("没有匹配的 S 怪——试试清空搜索，或把服务器 chips 点回来。");
            }

            return;
        }

        // 列宽全部按「全部怪名/全部地图名」的最大宽度算好，每行同一 X，不随筛选跳动；
        // 进度条吃掉剩下的宽度 —— 窗口拉宽就变长，自动自适应。
        const float nameX = 16f;
        float maxNameW = 0, maxMapW = 0;
        foreach (var r0 in all)
        {
            maxNameW = Math.Max(maxNameW, this.TW(r0.cn, 19f));
            maxMapW = Math.Max(maxMapW, this.TW(r0.map, 13f));
        }
        var fullW = ImGui.GetContentRegionAvail().X;
        var mapColX = Math.Min(nameX + maxNameW + 18f, fullW * 0.40f);
        var mapW = Math.Min(maxMapW + 12f, fullW * 0.18f);
        var pctReserve = this.TW("100%", 18f) + 26f;
        // 窄窗：地图挪到名称下方，进度条从左半边起，一行塞得下也不互相压
        var compactRow = fullW < 560f;

        ImGui.BeginChild("huntlist", new Vector2(0, Math.Max(120f, ImGui.GetContentRegionAvail().Y - 40f)), false);

        for (var i = 0; i < listed.Count; i++)
        {
            var (h, ws, st, _, pct, bestWorld) = listed[i];
            var open = this.expandedHunts.Contains(h.en);
            var col = StateColor(st);
            var pctTxt = st == "unknown" ? "—" : $"{Math.Round(pct)}%";

            // 每服迷你进度条：只画「当前筛选选中的服」，一律单列竖排（分两列时列优先的读法会让人看错顺序）
            var selWorlds = data.worlds.Where(w => this.worldFilter.Contains(w)).ToList();
            var showBars = selWorlds.Count > 0;
            var showPct = selWorlds.Count < data.worlds.Length; // 全选时不显示右侧百分比
            var mbSolo = selWorlds.Count == 1;
            var mbH = mbSolo ? 12f : 7f;
            const float mbGap = 3.5f;
            // 行高跟着条数长：8 服单列要堆 8 条，硬塞 52px 会压到下一行
            var rowH = showBars
                ? Math.Max(52f, selWorlds.Count * mbH + (selWorlds.Count - 1) * mbGap + 16f)
                : 52f;

            // 收起行：左状态竖条 | 名称 | 地图 | 每服迷你进度条（单列） | 百分比
            var dlr = ImGui.GetWindowDrawList();
            var listW = ImGui.GetContentRegionAvail().X;
            ImGui.PushID(h.en);
            var rowMin = ImGui.GetCursorScreenPos();
            // 圆角行底：展开 > 悬停（ImGui 自带的那层直角高亮已置透明）
            var rowHover = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(rowMin, rowMin + new Vector2(listW, rowH));
            if (open || rowHover)
            {
                var bg = open
                    ? new Vector4(V1Sel.X, V1Sel.Y, V1Sel.Z, 0.07f)
                    : V1Hover;
                dlr.AddRectFilled(rowMin + new Vector2(2, 2), rowMin + new Vector2(listW - 6, rowH - 2),
                    ImGui.GetColorU32(bg), 8f);
            }
            var toggled = ImGui.Selectable("##row", open, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(listW, rowH));
            if (toggled)
            {
                if (open) this.expandedHunts.Remove(h.en); else this.expandedHunts.Add(h.en);
            }
            var rowRight = rowMin.X + listW - 14;
            float Tw(string t, float size) => this.TW(t, size);

            // 左侧状态竖条：圆头 + 上实下淡的渐变（比纯色条更有体积感）
            var sTop = rowMin + new Vector2(3f, 6f);
            var sBot = rowMin + new Vector2(5.5f, rowH - 6f);
            dlr.AddRectFilled(sTop, sBot, ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, 0.72f)), 1.25f);
            dlr.AddRectFilled(sTop, new Vector2(sBot.X, sTop.Y + 12f), ImGui.GetColorU32(col), 1.25f);

            // 行内所有文字统一按「行高中线」自动垂直居中，不再各自写死 y 偏移
            // （雅黑 1.3 倍行距；单行直接用字号求顶边，双行把两块拼起来整体居中）
            var rowMidY = rowMin.Y + rowH * 0.5f;
            const float nameFs = 19f;
            var mapFs = compactRow ? 12.5f : 13f;
            float RowTextTop(float fs) => rowMidY - fs * 0.65f;
            var nameTop = compactRow
                ? rowMidY - (nameFs * 1.3f + 3f + mapFs * 1.3f) * 0.5f   // 窄窗：名称+地图两行整体居中
                : RowTextTop(nameFs);
            var mapTop = compactRow ? nameTop + nameFs * 1.3f + 3f : RowTextTop(mapFs);

            // 名称
            dlr.AddText(this.FontFor(nameFs), nameFs, new Vector2(rowMin.X + nameX, nameTop), ImGui.GetColorU32(V1Text), h.cn);
            // 地图列紧贴名称右侧的固定列位（每行同一 X，紧凑且对齐）；窄窗时放到名称下面
            var mapX = compactRow ? nameX : mapColX;

            // 地图列
            dlr.AddText(this.FontFor(mapFs), mapFs, new Vector2(rowMin.X + mapX, mapTop), ImGui.GetColorU32(V1Dim), h.map);

            // 每服迷你进度条：只画「当前筛选选中的服」，顺序固定；全选时不画（看聚合值即可）
            var wsMap = new Dictionary<string, (WorldDeath w, string st, double eta, double pct, double fill)>();
            foreach (var it in ws) wsMap[it.w.world] = it;
            var barsX = compactRow
                ? Math.Max(nameX + Math.Min(maxMapW, fullW * 0.34f) + 14f,   // 窄窗：给名字下方的地图让位
                           nameX + Math.Min(maxNameW, fullW * 0.42f) + 12f)   // 也别压到名字
                : mapColX + mapW;                                           // 宽窗：紧跟地图列
            barsX = Math.Min(barsX, Math.Max(compactRow ? nameX : mapColX, listW - pctReserve - 80f));
            var barsW = Math.Max(60f, listW - pctReserve - barsX);
            // 单服：条加粗，并在条右边补上服名——只有一根彩条没有任何参照，看不出是哪服
            var mbLabelW = mbSolo ? Tw(selWorlds[0], 12f) + 8f : 0f;
            var mbAreaW = Math.Max(36f, barsW - mbLabelW);
            var mbBlockH = selWorlds.Count * mbH + Math.Max(0, selWorlds.Count - 1) * mbGap;
            var mbTop = rowMin.Y + Math.Max(2f, (rowH - mbBlockH) * 0.5f);
            if (showBars)
            {
                for (var si = 0; si < selWorlds.Count; si++)
                {
                    var wn = selWorlds[si];
                    var by = mbTop + si * (mbH + mbGap);      // 单列：从上往下一服一行
                    var wBarX = rowMin.X + barsX;
                    var wBarW = mbAreaW;
                    if (wsMap.TryGetValue(wn, out var sv))
                        MiniBar(dlr, new Vector2(wBarX, by), wBarW, mbH, this.WorldColor(wn), (float)sv.fill);
                    else
                        dlr.AddRectFilled(new Vector2(wBarX, by), new Vector2(wBarX + wBarW, by + mbH),
                            ImGui.GetColorU32(new Vector4(V1Track.X, V1Track.Y, V1Track.Z, 0.55f)));
                }
                if (mbSolo)
                {
                    const float lfs = 12f;
                    dlr.AddText(this.FontFor(lfs), lfs,
                        new Vector2(rowMin.X + barsX + mbAreaW + 8f, rowMidY - lfs * 0.65f),
                        ImGui.GetColorU32(V1Dim), selWorlds[0]);
                }
                // 悬停进度条区域时给出选中服的明细（含「无记录」）
                var mpos = ImGui.GetMousePos();
                if (ImGui.IsItemHovered() && mpos.X >= rowMin.X + barsX && mpos.X <= rowMin.X + barsX + barsW)
                {
                    var tip = string.Join("\n", selWorlds.Select(wn =>
                    {
                        if (!wsMap.TryGetValue(wn, out var sv)) return $"{wn}：无记录";
                        return $"{wn}：{(sv.st == "open" ? "可踩" : sv.st == "cd" ? "冷却" : "过窗")} · {Math.Round(sv.pct)}%" +
                            (sv.st == "cd" ? $" · {Dur(sv.eta)}后" : "");
                    }));
                    ImGui.SetTooltip(tip);
                }
            }

            // 行尾只留百分比（按状态着色）：角签「可踩/冷却/过窗」和展开箭头都去掉，
            // 状态看左侧竖条 + 百分比颜色即可
            if (showPct)
            {
                var pctX = rowRight - 10 - Tw(pctTxt, 17f);
                dlr.AddText(this.FontFor(17f), 17f, new Vector2(pctX, RowTextTop(17f)), ImGui.GetColorU32(col), pctTxt);
            }

            // 行尾分割线：两端渐隐，比一根实线轻很多（不会把列表切成一格一格）
            var sepY = rowMin.Y + rowH + 1f;
            var sepL = rowMin.X + nameX - 4f;
            var sepR = rowMin.X + Math.Max(60f, listW - 12f);
            var sepMid = (sepL + sepR) * 0.5f;
            var sepC = ImGui.GetColorU32(V1Border);
            var sepC0 = ImGui.GetColorU32(new Vector4(V1Border.X, V1Border.Y, V1Border.Z, 0f));
            dlr.AddRectFilledMultiColor(new Vector2(sepL, sepY), new Vector2(sepMid, sepY + 1f), sepC0, sepC, sepC, sepC0);
            dlr.AddRectFilledMultiColor(new Vector2(sepMid, sepY), new Vector2(sepR, sepY + 1f), sepC, sepC0, sepC0, sepC);

            // 展开明细（纯手排四列：世界 | 进度条 | 百分比 | 状态·窗口 —— 不用 BeginTable，TableSetupColumn 断言与崩溃源）
            if (open)
            {
                var singleWorld = selWorlds.Count == 1;
                // 只有白名单里的怪才踩点；其余怪传大水晶等刷新就够，点位相关 UI 一律不出现
                var wantPoints = PointHunts.Contains(h.cn);
                var noPoints = NoPointHunts.Contains(h.cn);
                var pw = singleWorld
                    ? selWorlds[0]
                    : (this.huntWorld.TryGetValue(h.en, out var hw) ? hw : (bestWorld.Length > 0 ? bestWorld : data.worlds[0]));
                // 只保留可踩绿点：优先用站点实时排点（每分钟同步），拿不到才回退本地快照
                List<Pt> greens = new();
                if (wantPoints && !noPoints)
                    greens = this.LiveGreens(h.en, pw)
                        ?? HuntPoints(this.srankData.points.GetValueOrDefault(h.en), pw)
                            .Where(q => q.S == 'g')
                            .ToList();

                // 单服：不列服清单（都是「无记录」没意义）；不踩点 / 没有绿点的 S：整块省略
                var showWorldList = !noPoints && !singleWorld;
                var showPoints = greens.Count > 0;

                {
                    const float rowWorldH = 25f;
                    const float actH = 26f;
                    // 明细不再用固定高度的 child（高度算不准就会留空白 / 裁内容），
                    // 改成 drawlist 通道：先画内容、拿到真实高度，再在内容下面铺底色，永远贴合。
                    ImGui.Indent(8);
                    var dlBg = ImGui.GetWindowDrawList();
                    var detTop = ImGui.GetCursorScreenPos();
                    var detInnerW = ImGui.GetContentRegionAvail().X;
                    dlBg.ChannelsSplit(2);
                    dlBg.ChannelsSetCurrent(1);

                    // 默认动作：白名单里要走到触发点的 S 才「前往绿点」（插旗 + 跨服 + 传大水晶 + 坐骑寻路）；
                    // 其余 S（触发与点位无关、或用户明确说只需到大水晶的）一律「前往大水晶」
                    var aeDiscard = DiscardMaps.ContainsKey(h.map);
                    var walk = showPoints;
                    var topBtnW = aeDiscard && !walk ? 176f : 132f;
                    var isGounitu = h.cn == "顾尼图";
                    if (ImGui.Button((isGounitu ? "黑风海触发" : (walk ? "前往绿点" : aeDiscard ? "前往大水晶+丢弃" : "前往大水晶")) + $"##ae{h.en}",
                            new Vector2(topBtnW, actH)))
                    {
                        if (isGounitu)
                        {
                            this.GounituTrigger(pw);
                            this.Print($"[{h.cn}] 开始自动触发（切战士 → 传黑风海 → 飞触发点 → 水蛭飞斧 → 挑衅冰海天使 → 等巨大化/最大化/口锥）");
                        }
                        else if (walk)
                        {
                            var g0 = greens[0];
                            var wr = this.Goto(h.map, g0.X, g0.Y, pw, this.HuntLineOverride(h.en, pw), h.cn, true, greens);
                            this.Print($"[{h.cn}] {wr.Message}");
                        }
                        else
                        {
                            var ar = this.GotoAetheryte(h.map, pw, h.cn);
                            this.Print($"[{h.cn}] {ar.Message}");
                            if (ar.Ok && aeDiscard)
                            {
                                // 落地再等 2.5s 丢，卡过图期间丢会失败
                                this.WaitUntil(() => this.InMap(h.map), () =>
                                    this.Sched(() => this.Print(this.DiscardTrigger().Message),
                                        TimeSpan.FromMilliseconds(2500)), 240, 500);
                            }
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        if (isGounitu)
                            ImGui.SetTooltip("切战士 → 传黑风海大水晶 → 飞到固定点位 (29.8, 3.9) 自动下坐骑 → 对三个深海水蛭丢飞斧 → 挑衅冰海天使 → 等巨大化/最大化 → 受口锥伤害完成触发");
                        else
                            ImGui.SetTooltip((walk
                                ? $"开图 + 跨服到 {pw} + 传「{h.map}」大水晶，落地先上坐骑再飞向绿点"
                                : string.IsNullOrWhiteSpace(pw)
                                    ? $"开图并传送到「{h.map}」的大水晶" + (aeDiscard ? "，落地后自动丢弃触发道具" : "")
                                    : $"跨服到 {pw}，落地后传「{h.map}」的大水晶" + (aeDiscard ? "并丢弃触发道具" : ""))
                                + PrepHint(h.cn)
                                + (walk ? $"\n可踩绿点 {greens.Count} · {this.pointsStatus}" : ""));
                    }
                    // 单服：点位瓦片直接排在按钮右边同一行；有服务器明细时，瓦片并排到明细右侧
                    if (showPoints && !showWorldList)
                    {
                        this.PointTiles(h.map, h.en, pw, h.cn, greens, 10f);
                    }
                    else
                    {
                        var subTxt = $"{h.map} · {pw}";
                        if (h.kind.Length > 0) subTxt += " · " + h.kind;
                        var prepTxt = PrepHint(h.cn);
                        var restW = ImGui.GetContentRegionAvail().X - 10f;
                        if (prepTxt.Length > 0 && Tw(subTxt + prepTxt, 13.5f) < restW)
                            subTxt += prepTxt;
                        // 窄窗放不下就另起一行，不硬挤（挤出去会被窗口右边切掉）
                        if (Tw(subTxt, 13.5f) <= restW) ImGui.SameLine(0, 10);
                        ImGui.TextDisabled(subTxt);
                    }

                    // 条件时间（天气 / ET 时段 / 月相）：只有 13 只怪有，算法与熊站 Condition 列同源
                    if (ConditionForecast.HasSpec(h.en))
                    {
                        var cwNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var cwWin = ConditionForecast.NextWindow(h.en, cwNow);
                        var cwOpen = cwWin is { } cww && cww.Start <= cwNow && cwNow < cww.End;
                        ImGui.Dummy(new Vector2(0, 3));
                        ImGui.PushTextWrapPos(0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, StateColor(cwOpen ? "open" : "cd"));
                        ImGui.TextUnformatted("条件 · " + ConditionForecast.Describe(h.en, cwNow));
                        ImGui.PopStyleColor();
                        ImGui.PopTextWrapPos();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(ConditionForecast.LabelOf(h.en)
                                + "\n按本地现实时间推算，与 eorzea-weather / 熊站的条件计时器一致");
                    }

                    if (showWorldList)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, V1Dim);
                        ImGui.TextUnformatted(wantPoints ? "服务器明细 · 点击行切换该服的触发点" : "服务器明细 · 点一行看该服");
                        ImGui.PopStyleColor();

                // 每服自己的绿点瓦片直接排在「该服那一行」的右边；无记录的服不列点位
                var rowAvailW = ImGui.GetContentRegionAvail().X;
                const float tileW = 42f, tileH = 24f, tileGap = 4f, tileVGap = 4f;
                // 不需要点位的怪，右侧没有瓦片区，这一行直接用满宽度（进度条更长更好读）
                var leftW = wantPoints
                    ? Math.Clamp(rowAvailW * 0.58f, 236f, Math.Max(236f, rowAvailW - 60f))
                    : rowAvailW;
                var tileZoneW = Math.Max(0f, rowAvailW - leftW - 8f);
                var perRow = Math.Max(1, (int)Math.Floor((tileZoneW + tileGap) / (tileW + tileGap)));
                float colW = Math.Clamp(leftW * 0.24f, 72f, 150f);
                const float colP = 40f;
                float colE = Math.Clamp(leftW * 0.26f, 84f, 150f);
                float colB = Math.Max(24f, leftW - colW - colP - colE - 10f);

                List<Pt> GreensOf(string w) =>
                    this.LiveGreens(h.en, w)
                    ?? HuntPoints(this.srankData.points.GetValueOrDefault(h.en), w).Where(q => q.S == 'g').ToList();

                foreach (var wn in data.worlds)
                {
                    var has = wsMap.TryGetValue(wn, out var sv);
                    var isSel = wn == pw;
                    var gl = has && wantPoints ? GreensOf(wn) : new List<Pt>();
                    // 折叠时只铺一排（末尾带「+N」按钮，多出来的收起来），点开才按行数撑高
                    var tFold = gl.Count > perRow;
                    var tShown = gl.Count == 0 ? 0 : (!tFold || this.tilesExpanded.Contains(h.en + "|" + wn)
                        ? gl.Count
                        : Math.Max(1, perRow - 1));
                    var tRows = tShown == 0 ? 0 : (int)Math.Ceiling((tShown + (tFold ? 1 : 0)) / (double)perRow);
                    var thisRowH = Math.Max(rowWorldH - 2f, tRows > 0 ? tRows * tileH + (tRows - 1) * tileVGap + 4f : 0f);

                    var rp = ImGui.GetCursorScreenPos();
                    var wrowHov = ImGui.IsWindowHovered() &&
                                  ImGui.IsMouseHoveringRect(rp, rp + new Vector2(leftW, thisRowH));
                    if (isSel || wrowHov)
                        ImGui.GetWindowDrawList().AddRectFilled(
                            rp + new Vector2(-2, 1), rp + new Vector2(leftW - 2, thisRowH - 1),
                            ImGui.GetColorU32(isSel ? V1Sel : V1Hover), 6f);
                    var hit = ImGui.Selectable($"##{h.en}|{wn}", isSel, ImGuiSelectableFlags.SpanAllColumns, new Vector2(leftW, thisRowH));
                    if (has && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        // 右键 = 只保留这个服（单选筛选）
                        this.worldFilter.Clear();
                        this.worldFilter.Add(wn);
                        this.huntWorld[h.en] = wn;
                    }
                    else if (has && hit)
                    {
                        this.huntWorld[h.en] = wn;
                    }
                    var dld = ImGui.GetWindowDrawList();
                    var wcol = WorldColor(wn);
                    var midY = rp.Y + thisRowH / 2f;
                    dld.AddText(this.FontFor(15f), 15f, new Vector2(rp.X + 2, midY - 10f),
                        ImGui.GetColorU32(isSel ? wcol : (has ? V1Dim : V1Faint)),
                        (isSel ? "● " : "　") + wn);
                    if (!has)
                    {
                        dld.AddText(this.FontFor(13.5f), 13.5f, new Vector2(rp.X + colW, midY - 8f),
                            ImGui.GetColorU32(V1Faint), "无记录");
                    }
                    else
                    {
                        var bx = rp.X + colW;
                        BarAt(dld, new Vector2(bx, midY - 4.5f), colB, 9, wcol, (float)sv.fill);
                        dld.AddText(this.FontFor(15f), 15f, new Vector2(bx + colB + 10, midY - 10f),
                            ImGui.GetColorU32(StateColor(sv.st)), $"{Math.Round(sv.pct)}%");
                        var stTxt = colE >= 108f
                            ? $"{(sv.st == "open" ? "可踩" : sv.st == "cd" ? "冷却" : "过窗")} · {Dur(sv.eta)}"
                            : Dur(sv.eta);
                        dld.AddText(this.FontFor(13f), 13f, new Vector2(bx + colB + 10 + colP, midY - 8f),
                            ImGui.GetColorU32(V1Dim), stTxt);
                    }

                    // 该服的绿点瓦片贴在这一行右边
                    if (gl.Count > 0)
                    {
                        var blockH = tRows * tileH + (tRows - 1) * tileVGap;
                        ImGui.SetCursorScreenPos(new Vector2(rp.X + leftW + 8f, rp.Y + (thisRowH - blockH) / 2f));
                        this.PointTiles(h.map, h.en, wn, h.cn, gl, 0f, perRow, tileH);
                    }
                    ImGui.SetCursorScreenPos(new Vector2(rp.X, rp.Y + thisRowH));
                }

                    // ── 触发要点：从攻略抽出来的实际触发方式，默认收起，展开后逐条列 ──
                    if (h.tips.Length > 0)
                    {
                        ImGui.Dummy(new Vector2(0, 2));
                        var tk = h.en + "|tips";
                        var tOpen = this.tipsOpen.Contains(tk);
                        var mark = (h.counted ? " · 计数" : "") + (h.prob ? " · 概率" : "");
                        ImGui.PushStyleColor(ImGuiCol.Button, Transparent);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V1Hover);
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, V1HoverMid);
                        ImGui.PushStyleColor(ImGuiCol.Text, V1Dim);
                        if (ImGui.SmallButton($"{(tOpen ? "-" : "+")} 触发要点 {h.tips.Length} 条{mark}##tips{h.en}"))
                        {
                            if (!this.tipsOpen.Remove(tk)) this.tipsOpen.Add(tk);
                        }
                        ImGui.PopStyleColor(4);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("整理自「S级恶名精英狩猎与触发指南」· 点击" + (tOpen ? "收起" : "展开"));

                        if (tOpen)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, V1Dim);
                            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(120f, ImGui.GetContentRegionAvail().X - 8f));
                            foreach (var line in h.tips) ImGui.TextUnformatted("· " + line);
                            ImGui.PopTextWrapPos();
                            ImGui.PopStyleColor();
                        }
                    }

                    }

                    // 内容画完才知道真正高度：底色按实测高度铺，多一像素都不留
                    var detBottom = ImGui.GetCursorScreenPos().Y;
                    dlBg.ChannelsSetCurrent(0);
                    var cardMin = new Vector2(detTop.X - 10, detTop.Y - 8);
                    var cardMax = new Vector2(detTop.X - 10 + detInnerW + 20, detBottom + 8);
                    dlBg.AddRectFilled(cardMin, cardMax, ImGui.GetColorU32(V1Panel), 8f);
                    dlBg.AddRect(cardMin + new Vector2(0.5f, 0.5f), cardMax - new Vector2(0.5f, 0.5f),
                        ImGui.GetColorU32(new Vector4(V1Border.X, V1Border.Y, V1Border.Z, 0.85f)), 8f);
                    // 左侧一道状态色细条，展开的卡片和上面那行的状态色呼应
                    dlBg.AddRectFilled(new Vector2(cardMin.X + 3f, cardMin.Y + 10f),
                        new Vector2(cardMin.X + 5.5f, cardMax.Y - 10f),
                        ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, 0.85f)), 1.25f);
                    dlBg.ChannelsMerge();
                    ImGui.Indent(-8);
                    ImGui.SetCursorScreenPos(new Vector2(detTop.X - 8, detBottom + 8));
                }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();

        // 底栏上方：一条两端渐隐的细线，把列表和状态栏轻轻分开
        {
            var dlSep = ImGui.GetWindowDrawList();
            var sy = ImGui.GetCursorScreenPos().Y + 2f;
            var sx0 = ImGui.GetWindowPos().X + 16f;
            var sx1 = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - 34f;
            var sm = (sx0 + sx1) * 0.5f;
            var sc = ImGui.GetColorU32(V1Border);
            var s0 = ImGui.GetColorU32(new Vector4(V1Border.X, V1Border.Y, V1Border.Z, 0f));
            dlSep.AddRectFilledMultiColor(new Vector2(sx0, sy), new Vector2(sm, sy + 1f), s0, sc, sc, s0);
            dlSep.AddRectFilledMultiColor(new Vector2(sm, sy), new Vector2(sx1, sy + 1f), sc, s0, s0, sc);
        }
        // 底栏：● LIVE · BEAR TRACKER · 快照时间   |   右侧 GAME LINK
        ImGui.Dummy(new Vector2(0, 2));
        var dlf = ImGui.GetWindowDrawList();
        var ftMin = ImGui.GetCursorScreenPos();
        var ftH = ImGui.GetTextLineHeight();
        const float ftFs = 13f;
        var pulse = 0.45f + 0.55f * (float)Math.Sin(ImGui.GetTime() * 2.6);
        dlf.AddCircleFilled(ftMin + new Vector2(5, ftH / 2f + 1), 3.5f,
            ImGui.GetColorU32(new Vector4(V1Mint.X, V1Mint.Y, V1Mint.Z, pulse)), 12);
        float FT(string t) => this.TW(t, ftFs);
        var curW = this.CurrentWorldName();
        var curLine = this.GetCurrentInstance();
        var glink = curW.Length > 0
            ? $"GAME LINK · {curW}" + (curLine > 0 ? $" · {curLine}线" : "")
            : "GAME LINK · 未连游戏";
        var rightX = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - 34 - FT(glink);

        dlf.AddText(this.FontFor(ftFs), ftFs, ftMin + new Vector2(16, 1), ImGui.GetColorU32(V1Mint), "LIVE");
        var bearX = 16 + FT("LIVE") + 12;
        dlf.AddText(this.FontFor(ftFs), ftFs, ftMin + new Vector2(bearX, 1), ImGui.GetColorU32(V1Dim), "BEAR TRACKER");
        // 计数与快照：左边还剩多少空间就放多少，窄窗自动省略，不与右侧 GAME LINK 撞车
        var tailX = bearX + FT("BEAR TRACKER") + 10;
        var cntTxt = $"· {rows.Count} 只 · 开闸 {nOpen}";
        if (tailX + FT(cntTxt) < rightX - 12)
        {
            dlf.AddText(this.FontFor(ftFs), ftFs, ftMin + new Vector2(tailX, 1),
                ImGui.GetColorU32(nOpen > 0 ? V1Lime : V1Dim), cntTxt);
            tailX += FT(cntTxt) + 10;
        }
        var snap = $"· 快照 {DateTimeOffset.FromUnixTimeSeconds((long)data.snapshot).ToLocalTime():MM-dd HH:mm}";
        if (tailX + FT(snap) < rightX - 12)
        {
            dlf.AddText(this.FontFor(ftFs), ftFs, ftMin + new Vector2(tailX, 1), ImGui.GetColorU32(V1Dim), snap);
            tailX += FT(snap) + 10;
        }
        // 搜索行占用了顶栏 → DC 名与 ET/LT 挪到底栏，信息不丢
        if (this.searchInHeader)
        {
            var et2 = DateTimeOffset.FromUnixTimeSeconds((long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1440.0 / 70.0)).UtcDateTime;
            var info = $"· {this.srankData?.data?.dc ?? "—"} · ET {et2:HH:mm} · LT {DateTime.Now:HH:mm}";
            if (tailX + FT(info) < rightX - 12)
                dlf.AddText(this.FontFor(ftFs), ftFs, ftMin + new Vector2(tailX, 1), ImGui.GetColorU32(V1Dim), info);
        }

        dlf.AddText(this.FontFor(ftFs), ftFs, new Vector2(rightX, ftMin.Y + 1), ImGui.GetColorU32(V1Dim), glink);
        ImGui.Dummy(new Vector2(0, 19));
    }

    private string TerritoryName(uint terr)
    {
        try
        {
            var row = this.dataManager.GetExcelSheet<TerritoryType>()?.GetRow(terr);
            return row is { } r && r.PlaceName.RowId != 0 ? r.PlaceName.Value.Name.ToString() : terr.ToString();
        }
        catch { return terr.ToString(); }
    }

    private static bool ParseOn(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        s.Trim().ToLowerInvariant() is "on" or "1" or "true" or "yes" or "开" or "开启";

    // ─────────────────────────── 辅助 ───────────────────────────

    private string CurrentWorldName()
    {
        try { return this.objectTable.LocalPlayer?.CurrentWorld.Value.Name.ToString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>当前所在分线（UIState 的 PublicInstance.InstanceId：1–6 线；0 表示无分线/主城）。</summary>
    private uint GetCurrentInstance()
    {
        try
        {
            unsafe
            {
                return FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->PublicInstance.InstanceId;
            }
        }
        catch { return 0; }
    }

    /// <summary>排一个延时任务。所有流程（巡绿点 / 上坐骑 / 到点动作 / 进退副本）都走这里，
    /// 于是「停止」只要把 runGen +1，之前排队的整条链就全部作废，不用逐个去追。</summary>
    private void Sched(System.Action act, TimeSpan delay)
    {
        var gen = this.runGen;
        this.framework.RunOnTick(() =>
        {
            if (gen != this.runGen || this.halted) return;   // 已经被「停止」作废
            try { act(); }
            catch (Exception ex) { this.log.Error(ex, "[SrankFlag] 延时任务异常"); }
        }, delay);
    }

    /// <summary>手动停止：作废所有排队中的动作 + 拦住后续命令 + 让 vnav 停下来。相当于把自动化整个取消。</summary>
    private void StopAll(bool quiet = false)
    {
        this.halted = true;      // 先立旗：此后 Say() 不再放行任何自动化命令
        this.runGen++;
        this.Say("/vnav stop", force: true);
        if (quiet) return;

        var extra = "";
        try
        {
            if (this.condition[ConditionFlag.Casting])
                extra = "；正在读条的这一下会照常放出来（读条打断不了），之后不会再有任何动作";
            else if (this.condition[ConditionFlag.BoundByDuty] || this.condition[ConditionFlag.BetweenAreas])
                extra = "；当前还在副本里，DailyRoutines 的进退副本会自己把这一轮走完，走完后就不会再动了";
            else if (this.Dead()) extra = "；你已经倒地，返回大水晶的弹窗留给你自己点";
        }
        catch { /* ignore */ }
        this.Print("已停止全部工作：路线、上坐骑、到点动作、进退副本都已取消" + extra);
    }

    /// <summary>重新开始任何一次自动化动作（前往绿点 / 前往大水晶 / 上坐骑 / 触发），先解除停止状态。</summary>
    private void Resume() => this.halted = false;

    /// <summary>执行一条斜杠命令，在主线程里跑，跨线程调用也安全。
    /// 注意：Dalamud 的 ProcessCommand 只分发「插件注册过的」命令（文档：True if the command was found and dispatched），
    /// 返回 false 说明游戏根本没收到——/vnav、/pdr 这类注册过能走它，/gaction、/mount 是游戏原生命令，必须交给游戏自己解析。
    /// 之前「点了前往绿点却一直骑不上」就是这里只调了 ProcessCommand，命令被静默丢掉。</summary>
    private void Say(string text, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            this.framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    // 已经点过停止：队列里躺着的旧命令一律作废，只放行停止本身（force）
                    if (this.halted && !force)
                    {
                        this.log.Debug("[SrankFlag] 已停止，丢弃命令：" + text);
                        return;
                    }
                    if (this.commandManager.ProcessCommand(text)) return;  // 插件注册的命令（/vnav、/pdr…）
                    this.ExecuteGameCommand(text);                          // 游戏原生命令（/gaction、/mount…）
                }
                catch (Exception ex)
                {
                    this.log.Error(ex, "[SrankFlag] 命令执行异常：" + text);
                }
            });
        }
        catch (Exception ex) { this.log.Error(ex, "[SrankFlag] Say failed"); }
    }

    /// <summary>走游戏自己的命令解析器（RaptureShellModule.ExecuteCommandInner），
    /// 效果等同于在聊天框里敲下这行并回车。</summary>
    private unsafe void ExecuteGameCommand(string command)
    {
        try
        {
            var shell = FFXIVClientStructs.FFXIV.Client.UI.Shell.RaptureShellModule.Instance();
            if (shell == null)
            {
                this.log.Warning("[SrankFlag] RaptureShellModule 不可用，命令没发出去：" + command);
                return;
            }
            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
            var cmd = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(command);
            if (cmd == null) { this.log.Warning("[SrankFlag] 构造命令字符串失败：" + command); return; }
            try { shell->ExecuteCommandInner(cmd, ui); }
            finally { cmd->Dtor(); }
            this.log.Debug("[SrankFlag] 游戏命令已执行：" + command);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[SrankFlag] 执行游戏命令失败：" + command);
        }
    }

    private List<string> SearchZones(string keyword, int limit)
    {
        var result = new List<string>();
        var key = Normalize(keyword);
        var maps = this.dataManager.GetExcelSheet<Map>();
        if (maps is null) return result;
        foreach (var row in maps)
        {
            var name = SafeMapName(row);
            if (name.Length == 0) continue;
            if (Normalize(name).Contains(key, StringComparison.Ordinal))
            {
                if (!result.Contains(name)) result.Add(name);
                if (result.Count >= limit) return result;
            }
        }
        return result;
    }

    private static string SafeMapName(Map row)
    {
        try { return row.PlaceName.RowId == 0 ? string.Empty : row.PlaceName.Value.Name.ToString(); }
        catch { return string.Empty; }
    }
    private static string SafePlaceName(TerritoryType row)
    {
        try { return row.PlaceName.RowId == 0 ? string.Empty : row.PlaceName.Value.Name.ToString(); }
        catch { return string.Empty; }
    }
    private static string SafePlaceNameFrom(Aetheryte row)
    {
        try { return row.PlaceName.RowId == 0 ? string.Empty : row.PlaceName.Value.Name.ToString(); }
        catch { return string.Empty; }
    }
    private static string Normalize(string s) => (s ?? string.Empty).Replace(" ", string.Empty).Replace("　", string.Empty).Trim();

    private void Print(string msg)
    {
        try { this.framework.RunOnFrameworkThread(() => { try { this.chatGui.Print($"[魔法水桶] {msg}"); } catch { } }); }
        catch { this.log.Information("[SrankFlag] " + msg); }
    }

    // ─────────────────────────── HTTP 小接口 ───────────────────────────

    private void StartServer()
    {
        try
        {
            this.listener = new TcpListener(IPAddress.Loopback, Port);
            try { this.listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
            this.listener.Start();
            _ = Task.Run(this.AcceptLoopAsync);
            this.log.Information($"[SrankFlag] listening on 127.0.0.1:{Port}");
            this.Print($"已就绪，接口 127.0.0.1:{Port}（窗口里点触发点即可开图插旗）");
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[SrankFlag] listen failed");
            this.Print($"接口启动失败（端口 {Port} 被占用？）：{ex.Message}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!this.cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await this.listener!.AcceptTcpClientAsync(this.cts.Token); }
            catch (OperationCanceledException) { return; }
            catch { continue; }
            _ = Task.Run(() => this.HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                var (method, path, body) = await ReadRequestAsync(stream);
                if (method is null) return;

                if (method == "OPTIONS") { await WriteAsync(stream, 204, "{}"); return; }

                // 全量数据（data + 触发点）
                if (path.StartsWith("/data", StringComparison.OrdinalIgnoreCase))
                {
                    if (this.srankData is null) { await WriteAsync(stream, 404, Json(ok: false, message: "数据未加载")); return; }
                    await WriteAsync(stream, 200, JsonSerializer.Serialize(this.srankData));
                    return;
                }

                if (path.StartsWith("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 200, JsonSerializer.Serialize(new
                    {
                        ok = true,
                        name = "SrankFlag",
                        version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "?",
                        world = CurrentWorldName(),
                        instance = GetCurrentInstance(),
                        territory = this.clientState.TerritoryType,
                        autoSummon = this.autoSummonMinion,
                        autoLeave = this.autoLeaveDuty,
                        confirmReturn = this.autoConfirmReturn,
                        port = Port,
                    }));
                    return;
                }

                if (path.StartsWith("/maps", StringComparison.OrdinalIgnoreCase))
                {
                    var q = QueryValue(path, "q") ?? string.Empty;
                    var hits = q.Length == 0 ? new List<string>() : this.SearchZones(q, 20);
                    await WriteAsync(stream, 200, JsonSerializer.Serialize(new { ok = true, q, hits }));
                    return;
                }

                if (path.StartsWith("/flag", StringComparison.OrdinalIgnoreCase))
                {
                    (string map, float x, float y) = ParseFlagArgs(method, path, body);
                    if (map.Length == 0) { await WriteAsync(stream, 400, Json(ok: false, message: "map 不能为空")); return; }
                    var r = this.Flag(map, x, y);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message, territory: r.TerritoryId, mapId: r.MapId, x, y));
                    return;
                }

                if (path.StartsWith("/goto", StringComparison.OrdinalIgnoreCase))
                {
                    (string map, float x, float y, string world, int line) = ParseGotoArgs(method, path, body);
                    if (map.Length == 0) { await WriteAsync(stream, 400, Json(ok: false, message: "map 不能为空")); return; }
                    var r = this.Goto(map, x, y, world, line);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/telepo", StringComparison.OrdinalIgnoreCase))
                {
                    (string map, float x, float y, _, _) = ParseGotoArgs(method, path, body);
                    if (map.Length == 0) { await WriteAsync(stream, 400, Json(ok: false, message: "map 不能为空")); return; }
                    var r = this.TeleportNearest(map, x, y);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/worldtravel", StringComparison.OrdinalIgnoreCase))
                {
                    var world = ParseGotoArgs(method, path, body).world;
                    var r = this.WorldTravel(world);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/insc", StringComparison.OrdinalIgnoreCase))
                {
                    var line = ParseGotoArgs(method, path, body).line;
                    var r = this.ChangeInstance(line);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/discard", StringComparison.OrdinalIgnoreCase))
                {
                    var r = this.DiscardTrigger();
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/weeEa", StringComparison.OrdinalIgnoreCase))
                {
                    var r = this.QueryWeeEa();
                    await WriteAsync(stream, 200, Json(ok: r.Ok, message: r.Message, names: r.Names));
                    return;
                }

                if (path.StartsWith("/nav", StringComparison.OrdinalIgnoreCase))
                {
                    (string map, float x, float y, string world, _) = ParseGotoArgs(method, path, body);
                    if (map.Length == 0) { await WriteAsync(stream, 400, Json(ok: false, message: "map 不能为空")); return; }
                    var r = this.NavTo(map, x, y, world);
                    await WriteAsync(stream, r.Ok ? 200 : 404, Json(ok: r.Ok, message: r.Message));
                    return;
                }

                if (path.StartsWith("/vstop", StringComparison.OrdinalIgnoreCase))
                {
                    this.StopNav();
                    await WriteAsync(stream, 200, Json(ok: true, message: "已发送 /vnav stop"));
                    return;
                }

                if (path.StartsWith("/summonauto", StringComparison.OrdinalIgnoreCase))
                {
                    var on = QueryValue(path, "on");
                    if (on != null) this.autoSummonMinion = ParseOn(on);
                    if (this.autoSummonMinion) this.RefreshUnlockedMinions();
                    this.SaveAutoSettings();
                    await WriteAsync(stream, 200, Json(ok: true, message: $"自动召唤随从：{(this.autoSummonMinion ? "已开启" : "已关闭")}"));
                    return;
                }

                if (path.StartsWith("/leaveauto", StringComparison.OrdinalIgnoreCase))
                {
                    var on = QueryValue(path, "on");
                    if (on != null) this.autoLeaveDuty = ParseOn(on);
                    this.SaveAutoSettings();
                    await WriteAsync(stream, 200, Json(ok: true, message: $"独身自动退本：{(this.autoLeaveDuty ? "已开启" : "已关闭")}"));
                    return;
                }

                if (path.StartsWith("/summon", StringComparison.OrdinalIgnoreCase))
                {
                    var r = this.SummonForCurrentTerritory();
                    await WriteAsync(stream, 200, Json(ok: r.StartsWith("已"), message: r));
                    return;
                }

                await WriteAsync(stream, 404, Json(ok: false, message: "unknown route"));
            }
            catch (Exception ex) { this.log.Warning(ex, "[SrankFlag] request failed"); }
        }
    }

    private (string map, float x, float y, string world, int line) ParseGotoArgs(string method, string path, string body)
    {
        string map = "", world = ""; float x = 0, y = 0; int line = 0;
        if (method == "GET")
        {
            map = QueryValue(path, "map") ?? "";
            x = ParseFloat(QueryValue(path, "x"));
            y = ParseFloat(QueryValue(path, "y"));
            world = QueryValue(path, "world") ?? "";
            line = (int)ParseFloat(QueryValue(path, "line"));
        }
        else
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            map = Str(root, "map"); world = Str(root, "world");
            x = Flt(root, "x"); y = Flt(root, "y"); line = (int)Flt(root, "line");
        }
        return (map, x, y, world, line);
    }

    private (string map, float x, float y) ParseFlagArgs(string method, string path, string body)
    {
        var g = ParseGotoArgs(method, path, body);
        return (g.map, g.x, g.y);
    }

    private static string Str(JsonElement root, string k) =>
        root.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    private static float Flt(JsonElement root, string k) =>
        root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

    private static string Json(bool ok, string message, uint territory = 0, uint mapId = 0, float x = 0, float y = 0, List<string>? names = null) =>
        JsonSerializer.Serialize(new { ok, message, territory, mapId, x, y, names = names ?? new List<string>() });

    private static float ParseFloat(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static async Task<(string? Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buf = new byte[8192];
        var acc = new List<byte>(8192);
        int headerEnd;
        while (true)
        {
            headerEnd = IndexOfHeaderEnd(acc);
            if (headerEnd >= 0) break;
            if (acc.Count > 1 << 20) return (null, string.Empty, string.Empty);
            var n = await stream.ReadAsync(buf, 0, buf.Length);
            if (n <= 0) return (null, string.Empty, string.Empty);
            for (var i = 0; i < n; i++) acc.Add(buf[i]);
        }
        var head = Encoding.UTF8.GetString(acc.GetRange(0, headerEnd).ToArray());
        var bodyStart = headerEnd + 4;
        var lines = head.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        var method = first.Length > 0 ? first[0].ToUpperInvariant() : null;
        var path = first.Length > 1 ? first[1] : "/";
        var contentLength = 0;
        foreach (var line in lines)
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);
        var bodyBytes = new List<byte>(Math.Max(contentLength, 0));
        if (acc.Count > bodyStart) bodyBytes.AddRange(acc.GetRange(bodyStart, acc.Count - bodyStart));
        while (bodyBytes.Count < contentLength)
        {
            var n = await stream.ReadAsync(buf, 0, buf.Length);
            if (n <= 0) break;
            for (var i = 0; i < n; i++) bodyBytes.Add(buf[i]);
        }
        var body = bodyBytes.Count == 0 ? string.Empty : Encoding.UTF8.GetString(bodyBytes.ToArray());
        return (method, path, body);
    }

    private static int IndexOfHeaderEnd(List<byte> b)
    {
        for (var i = 3; i < b.Count; i++)
            if (b[i] == (byte)'\n' && b[i - 1] == (byte)'\r' && b[i - 2] == (byte)'\n' && b[i - 3] == (byte)'\r')
                return i - 3;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 204 => "No Content", _ => "OK" };
        var head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: *\r\n" +
            "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static string? QueryValue(string path, string key)
    {
        var q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in path[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }
}
