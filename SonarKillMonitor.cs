using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SrankFlag;

/// <summary>
/// v1.2.12：Sonar 聊天栏「实时击杀时间」补充数据源。
///
/// 【为什么必须有这个模块】
/// HTTP 拉 /api/live 或熊站有延迟，非豆豆柴尤其明显，概率算不准。Sonar 会在游戏聊天栏
/// 立刻播报击杀，把它当作本地实时 lastDeath 补进来，可以顺带核对站点返回的概率是否正确。
///
/// 【Sonar 实际输出格式（已对照 SonarDistrib 源码核实）】
///   Notifiers/HuntNotifier.cs:
///     HuntFound(出生) → SendToChat(state)                       // 末尾什么都没有
///     HuntDead(击杀) → SendToChat(state) + " was just killed"   // ← 唯一区别
///   Game/PayloadExtensions.cs:
///     "Rank {Rank}: {Name} " + MapLink("地图名 ( X , Y )") +
///     (可选分线图标) + " &lt;" + (可选跨服图标 ) + "服务器名&gt;"
///   ChatGui.Print(new() { Type = HuntOutputChannel, Name = "Sonar", Message = ... })
///
///   击杀播报：Rank S: 盐和光 基拉巴尼亚湖区 ( 10.3 , 34.0 ) &lt;晨曦王座&gt; was just killed
///   出生播报：(Sonar) Rank S: 戾虫 雷克兰德 ( 25.4  , 24.4 ) &lt;旅人栈桥&gt;   ← 只记历史
///
/// 【v1.2.12 历史功能】出生 / 击杀都要收进 srank_history.json，但写入路径完全不同：
///   击杀 → 更新 lastDeath + 概率瞬间 0% + 主窗强制刷新 + 记历史(Kill/Sonar)
///   出生 → 只记历史(Spawn/Sonar)，绝不改 lastDeath / 概率 / 主窗
///   两条正则都 \z 全锚定，互斥；出生分支同样要过下面六道白名单闸才准写。
///
/// 【严格性设计（六道闸，任何一道不过就整条丢弃，绝不写数据）】
///   1. 长度 24..200 + 必须含 "Rank S:"（Ordinal 精确大小写）；
///      Rank A / Rank B / Rank SS / FATE / 玩家闲聊在这一步就被挡掉。
///      尾句 "was just killed" 决定走 KillRx 还是 SpawnRx，不再作为总体丢弃条件。
///   2. 清洗 FFXIV 私用区图标（U+E000..U+F8FF：地图链接 、跨服 、分线图标）
///      与全角/不换行空格，避免国服特殊符号把正则打断。
///   3. 正则 ^...\z 全串锚定：可选 (Sonar) 前缀 + Rank S: + 头部 + (X , Y) + &lt;服名&gt;，
///      KillRx 再要求尾句 was just killed、SpawnRx 则要求 &lt;服名&gt; 后立即结束（严格互斥）。
///      不按聊天频道过滤 —— Sonar 的 HuntOutputChannel 用户可配（默认 Echo），硬过滤会静默失效。
///   4. 「怪名 + 地图名」整体必须与 srank_data.json 里的 cn+map 或 en+map 精确相等（白名单反查），
///      且服务器名必须唯一属于某一个大区的 worlds 列表。这一步同时完成实体识别，
///      避免 "Salt and Light" 这类含空格英文名被切错，也挡住玩家在聊天栏手打伪造文案。
///   5. 坐标 0..42 + 该怪确实存在这个服的 WorldDeath 槽（不新增槽位）。
///   6. 30 秒同 (大区|怪|服) 去重 + 时间戳只增不减（detected &gt; old 才写）。
///
/// 【线程】ChatMessage 事件在框架 PrintMessage 钩子里触发，这里只读文本并解析，
/// 所有共享状态（srankData / nativeHud / 缓存文件）都经 framework.RunOnTick 回到主线程再动。
///
/// 【持久化】实时击杀单独写 sonar_kills.json；历史事件写 SrankHistory 的 srank_history.json。
/// 两者都是独立文件，绝不触碰 5 个用户状态文件，也不会被 HTTP 拉取回滚。
/// </summary>
public sealed partial class Plugin
{
    /// <summary>击杀播报正则。注意 ^ 与 \z 全锚定、CultureInvariant、不加 IgnoreCase
    /// （Sonar 固定输出 "Rank S:" / "was just killed"，放宽大小写只会增加误抓面）。</summary>
    private static readonly Regex SonarKillRx = new(
        @"^\s*" +
        @"(?:(?:\(|\[|（)\s*Sonar\s*(?:\)|\]|）)\s*|Sonar\s*:\s*)?" +   // 可选 (Sonar) / [Sonar] / （Sonar） / Sonar:
        @"Rank\s+S\s*:\s*" +                                            // 只要 Rank S，不匹配 Rank A / B / SS
        @"(?<head>[^<>()]+?)\s*" +                                        // 怪名 + 地图链接图标 + 地图名（图标已清洗）
        @"\(\s*(?<x>\d{1,2}(?:\.\d+)?)\s*,\s*(?<y>\d{1,2}(?:\.\d+)?)\s*\)\s*" +
        @"<\s*(?<world>[^<>]+?)\s*>\s*" +
        @"was\s+just\s+killed\s*\.?\s*\z",                                // ← 击杀的唯一标志，出生播报没有
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// v1.2.12 历史功能：出生播报（HuntFound）正则。Sonar 源码里出生与击杀格式完全相同，
    /// 唯一区别是击杀末尾多 " was just killed"。所以这里把 \z 直接锚在 &lt;服名&gt; 之后：
    /// 带尾句的击杀播报必然匹配失败，两条正则严格互斥，绝不会把一条消息记成两个事件。
    /// 出生播报没有那句尾锚点，安全门槛反而要更高 —— 后面用与击杀完全相同的白名单校验，
    /// 并且【只写历史】，绝不碰 lastDeath / 概率 / 主窗（把「刚出生」当「刚被杀」清零概率是灾难）。
    /// </summary>
    private static readonly Regex SonarSpawnRx = new(
        @"^\s*" +
        @"(?:(?:\(|\[|（)\s*Sonar\s*(?:\)|\]|）)\s*|Sonar\s*:\s*)?" +
        @"Rank\s+S\s*:\s*" +
        @"(?<head>[^<>()]+?)\s*" +
        @"\(\s*(?<x>\d{1,2}(?:\.\d+)?)\s*,\s*(?<y>\d{1,2}(?:\.\d+)?)\s*\)\s*" +
        @"<\s*(?<world>[^<>]+?)\s*>\s*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// v1.2.12「已触发未死」的第二种解除条件：脱战 / 重置播报。
    ///
    /// SonarDistrib 源码里只有 HuntFound / HuntDead 两种播报，**没有**脱战文案，
    /// 所以这条是给「将来 Sonar 加了脱战播报」和「插件自己的重置播报」预留的：
    /// 只认 &lt;服名&gt; 之后紧跟的明确动词（reset / despawned / evaded / disengaged / leashed /
    /// 中文 脱战·重置），不做模糊包含匹配；命中后**只解除黑名单**，不动 lastDeath / 概率。
    /// IgnoreCase 只对这一条开（文案未见实物，大小写不敏感更稳），前两条仍严格区分大小写。
    /// </summary>
    private static readonly Regex SonarResetRx = new(
        @"^\s*" +
        @"(?:(?:\(|\[|（)\s*Sonar\s*(?:\)|\]|）)\s*|Sonar\s*:\s*)?" +
        @"Rank\s+S\s*:\s*" +
        @"(?<head>[^<>()]+?)\s*" +
        @"\(\s*(?<x>\d{1,2}(?:\.\d+)?)\s*,\s*(?<y>\d{1,2}(?:\.\d+)?)\s*\)\s*" +
        @"<\s*(?<world>[^<>]+?)\s*>\s*" +
        @"(?:was\s+just\s+)?(?:reset|despawned|evaded|disengaged|leashed|脱战|重置)\s*[.!。]?\s*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string SonarCacheFile = "sonar_kills.json";
    private const double SonarDedupeSeconds = 30;

    private readonly Dictionary<string, double> sonarKills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> sonarSeen = new(StringComparer.Ordinal);
    /// <summary>出生播报独立去重表：与击杀分开，否则「先出生、10 秒后被杀」会被 30s 去重吞掉击杀。</summary>
    private readonly Dictionary<string, double> sonarSpawnSeen = new(StringComparer.Ordinal);
    /// <summary>脱战 / 重置播报的独立去重表。</summary>
    private readonly Dictionary<string, double> sonarResetSeen = new(StringComparer.Ordinal);
    private readonly List<SonarKillRecord> sonarRecent = new();
    private bool sonarKillsLoaded;
    private bool sonarProbeOk;
    private double sonarProbeAt;
    private int sonarUnmatched;
    private string sonarUnmatchedSample = "";

    private sealed class SonarKillFile
    {
        public Dictionary<string, double> kills { get; set; } = new(StringComparer.Ordinal);
        public List<SonarKillRecord> recent { get; set; } = new();
    }

    private sealed class SonarKillRecord
    {
        public double at { get; set; }        // 捕获时刻（UTC Unix 秒）
        public double kill { get; set; }      // 写入的 lastDeath（同 at）
        public double old { get; set; }       // 覆盖前 lastDeath（站点/缓存口径）
        public string dc { get; set; } = "";
        public string world { get; set; } = "";
        public string cn { get; set; } = "";
        public string en { get; set; } = "";
        public string map { get; set; } = "";
        public double oldPct { get; set; }    // 原值算出的概率（站点口径）
        public double newPct { get; set; }    // 实时值算出的概率（应为 0）
    }

    private sealed record SonarHit(string Head, string World, double X, double Y,
                                   double SeenAt, bool IsKill, bool IsReset = false);

    private string SonarCachePath => Path.Combine(this.pluginDirectory, SonarCacheFile);

    // ───────────────────── 聊天事件入口 ─────────────────────

    /// <summary>ChatGui.ChatMessage 回调。只做纯文本解析，绝不在这里碰共享数据。
    /// v1.2.12 历史功能：出生 / 击杀都收，但走两条互斥正则、两类完全不同的写入路径。</summary>
    private void OnSonarKillChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
    {
        try
        {
            var raw = msg?.Message?.TextValue ?? string.Empty;
            if (raw.Length is < 24 or > 200) return;

            // 闸 1：Rank S 硬锚点。Rank A / B / SS / FATE / 玩家闲聊在这里就被挡掉。
            if (raw.IndexOf("Rank S:", StringComparison.Ordinal) < 0) return;

            // 闸 2：清掉 FFXIV 私用区图标（地图链接  U+E0BB / 跨服  U+E05D / 分线 ①②③…）
            // 以及全角空格、不换行空格、换行；它们都是 SeString 里的展示字符，不参与语义。
            var text = CleanSonarText(raw);

            // 闸 3：尾句决定事件类型。击杀带 "was just killed"，出生没有（Sonar 源码实证）。
            // 两条正则都 ^...\z 全锚定：带尾句的消息过不了 SpawnRx，不带尾句的过不了 KillRx。
            // v1.2.12：先试「脱战/重置」正则 —— 它要求 <服名> 后跟明确动词，
            // 出生播报（&gt; 后直接结束）不会命中，所以三条正则严格互补。
            var isKill = text.IndexOf("was just killed", StringComparison.Ordinal) >= 0;
            Match m;
            var isReset = false;
            if (isKill)
            {
                m = SonarKillRx.Match(text);
            }
            else
            {
                m = SonarResetRx.Match(text);
                isReset = m.Success;
                if (!m.Success) m = SonarSpawnRx.Match(text);
            }
            if (!m.Success)
            {
                // 只对「疑似击杀但格式不对」留诊断样本；出生分支不匹配很正常，别刷日志。
                if (isKill) this.NoteSonarUnmatched(text);
                return;
            }
            if (!double.TryParse(m.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(m.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return;

            var hit = new SonarHit(
                m.Groups["head"].Value.Trim(),
                m.Groups["world"].Value.Trim(),
                x, y,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                isKill,
                isReset);

            this.framework.RunOnTick(() => this.ApplySonarHit(hit));
        }
        catch (Exception ex)
        {
            try { this.log.Warning("[SrankFlag.Sonar] 聊天解析异常：" + ex.Message); } catch { }
        }
    }

    private static string CleanSonarText(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c >= '\uE000' && c <= '\uF8FF') continue;                  // FFXIV 私用区图标
            if (c is '\u3000' or '\u00A0' or '\u202F' or '\u2007' or '\r' or '\n' or '\t')
            { sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private void NoteSonarUnmatched(string text)
    {
        // 只在「已过双锚点、但正则没过」时记，限频，方便以后补 Sonar 新格式，不刷日志。
        this.sonarUnmatched++;
        if (this.sonarUnmatchedSample.Length == 0)
            this.sonarUnmatchedSample = text.Length > 160 ? text[..160] : text;
        if (this.sonarUnmatched <= 3)
            this.log.Information("[SrankFlag.Sonar] 疑似播报但未匹配（已忽略）：" + this.sonarUnmatchedSample);
    }

    // ───────────────────── 主线程：校验 + 写入 + 刷新 ─────────────────────

    private void ApplySonarHit(SonarHit hit)
    {
        try
        {
            // 链路自检消息：怪名带 __PROBE__，只验证 ChatMessage 能收到 Chat.Print，不写任何数据。
            if (hit.Head.IndexOf("__PROBE__", StringComparison.Ordinal) >= 0)
            {
                this.sonarProbeOk = true;
                this.log.Information("[SrankFlag.Sonar] 链路自检通过：ChatGui.ChatMessage 能收到 Sonar 的聊天输出");
                return;
            }

            // 闸 5a：坐标范围（FFXIV 地图坐标 0..42）
            if (hit.X < 0 || hit.X > 42 || hit.Y < 0 || hit.Y > 42) return;
            if (hit.World.Length is 0 or > 20 || hit.Head.Length is 0 or > 80) return;

            if (this.srankData?.dcs is not { Count: > 0 } dcs) return;
            var worldKey = Normalize(hit.World);

            // 闸 4a：服务器必须唯一属于某个大区（跨大区也要认，非豆豆柴正是痛点）
            var dcMatches = dcs.Where(d => d.worlds.Any(w =>
                string.Equals(Normalize(w), worldKey, StringComparison.OrdinalIgnoreCase))).ToList();
            if (dcMatches.Count != 1) { this.log.Information("[SrankFlag.Sonar] 服名不在白名单或归属多区，忽略：" + hit.World); return; }
            var dc = dcMatches[0];

            // 闸 4b：头部 = 怪名 + 地图名，整体与 srank_data.json 的 cn/en + map 精确相等。
            // 这样既认中文名也认英文名，且顺手校验「怪与地图必须对得上」，玩家伪造文案过不了。
            var headKey = Normalize(hit.Head);
            var hunt = dc.hunts.FirstOrDefault(h =>
                string.Equals(Normalize(h.cn + h.map), headKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Normalize(h.en + h.map), headKey, StringComparison.OrdinalIgnoreCase));
            if (hunt is null)
            {
                // 格式对得上、但实体不认识：多半是游戏内译名与 srank_data.json 的 cn 有出入，
                // 记日志供补别名；绝不退而求其次做模糊匹配（那才是误抓的源头）。
                this.sonarUnmatched++;
                this.log.Information("[SrankFlag.Sonar] 怪名/地图不在白名单，忽略：" + hit.Head);
                return;
            }

            // 闸 5b：只更新已存在的 WorldDeath 槽，绝不新建脏数据
            var wd = hunt.worlds.FirstOrDefault(w =>
                string.Equals(Normalize(w.world), worldKey, StringComparison.OrdinalIgnoreCase));
            if (wd is null) return;

            var key = dc.dc + "|" + hunt.en + "|" + wd.world;

            // ── 脱战 / 重置播报：只解除黑名单，绝不动 lastDeath / 概率 ──────────
            // 这是「已触发未死」的第二种解除条件（第一种是它真的被击杀）。
            // 脱战说明这只怪回到了未交战状态，别人触发的那个状态已经不存在了，可以放行。
            if (hit.IsReset)
            {
                if (this.sonarResetSeen.TryGetValue(key, out var lastReset) &&
                    hit.SeenAt - lastReset < SonarDedupeSeconds) return;
                this.sonarResetSeen[key] = hit.SeenAt;

                var freed = this.ClearTriggered(hunt.en, wd.world, "Sonar 脱战/重置播报");
                this.nativeHud?.ForceMainRefresh();
                this.log.Information(
                    $"[SrankFlag.Sonar] 脱战/重置 {dc.dc}/{wd.world} {hunt.cn}({hunt.en}) → " +
                    (freed ? "解除「已被触发」拉黑" : "本来就没拉黑，忽略"));
                return;
            }

            // ── 出生播报：只写历史，绝不碰实时数据 ──────────────────────────
            // 这里已经过了与击杀完全相同的六道白名单闸（服名唯一归属、怪名+地图精确匹配、
            // 坐标范围、已存在 WorldDeath 槽），所以玩家在聊天栏手打伪造句也进不来。
            // ★ 绝对不能在这里改 wd.lastDeath / 清概率 / ForceMainRefresh：
            //   「刚出生」被当成「刚被杀」会把概率瞬间清零，比 HTTP 延迟糟糕得多。
            if (!hit.IsKill)
            {
                if (this.sonarSpawnSeen.TryGetValue(key, out var lastSpawn) &&
                    hit.SeenAt - lastSpawn < SonarDedupeSeconds) return;
                this.sonarSpawnSeen[key] = hit.SeenAt;

                this.history?.Add(hunt.cn, wd.world, hunt.map,
                    HistoryEventType.Spawn, HistorySource.Sonar, hit.SeenAt);
                this.nativeHud?.ForceHistoryRefresh();

                // v1.2.12：出生播报 = 它已经刷出来了 = 已被（别人）触发 → 一出现就拉黑。
                // 只标记「别再去」，绝不碰 lastDeath / 概率（那是击杀才做的事）。
                this.MarkTriggered(hunt.en, wd.world, wd.lastDeath, hit.SeenAt, "Sonar 出生播报");

                this.log.Information(
                    $"[SrankFlag.Sonar] 出生播报 {dc.dc}/{wd.world} {hunt.cn}({hunt.en}) · 记历史 + 拉黑，实时状态不变");
                return;
            }

            // 闸 6a：30 秒去重（Sonar 重播 / 同一条被多个频道看到）
            if (this.sonarSeen.TryGetValue(key, out var last) && hit.SeenAt - last < SonarDedupeSeconds) return;
            this.sonarSeen[key] = hit.SeenAt;

            // 闸 6b：时间戳只增不减 —— 抓到的时间与现有 lastDeath 比，只有更晚才覆盖，
            // 防止 HTTP 慢响应 / 乱序消息把刚写的实时值改回旧值（用户明确要求）。
            var old = wd.lastDeath;
            if (hit.SeenAt <= old)
            {
                this.log.Information($"[SrankFlag.Sonar] 忽略较旧时间：{dc.dc}/{wd.world} {hunt.cn} 本地={hit.SeenAt:0} 现有={old:0}");
                return;
            }

            // 概率核对：同一套 Calc()，对比「站点旧值」与「本地实时值」算出的概率。
            // 维护期会用 mt，这里用该怪自身的 cd/win 做展示口径；差异主要体现在 oldPct。
            var now = hit.SeenAt;
            var oldPct = old > 0 ? Calc(old, now, hunt.cd, hunt.win).Pct : 0;
            wd.lastDeath = now;
            var newPct = Calc(now, now, hunt.cd, hunt.win).Pct;   // 刚击杀 → cd 段 → 0%
            if (hunt.worlds.Length > 0)
            {
                var mx = hunt.worlds.Max(w => w.lastDeath);
                if (mx > hunt.lastDeath) hunt.lastDeath = mx;
            }

            this.sonarKills[key] = now;
            this.sonarRecent.Insert(0, new SonarKillRecord
            {
                at = now, kill = now, old = old,
                dc = dc.dc, world = wd.world, cn = hunt.cn, en = hunt.en, map = hunt.map,
                oldPct = oldPct, newPct = newPct,
            });
            if (this.sonarRecent.Count > 40) this.sonarRecent.RemoveRange(40, this.sonarRecent.Count - 40);
            this.SaveSonarKills();

            // 历史：Sonar 击杀独立一条，与 MainSite 击杀不去重（用户要拿它核对站点概率）。
            this.history?.Add(hunt.cn, wd.world, hunt.map,
                HistoryEventType.Kill, HistorySource.Sonar, now);
            this.nativeHud?.ForceHistoryRefresh();

            // v1.2.12：击杀 = 进入冷却 = 黑名单唯一解锁条件（其余情况永不解除）。
            // 同时把这次击杀记为「已消化」：主站稍后同步到同一时间戳时，不会再把它锁回黑名单。
            this.ClearTriggered(hunt.en, wd.world, "Sonar 击杀播报", now);

            // 闸 3 完成：跳过主窗 200ms 节流，下一帧立刻重算概率 + 血条（0% / 冷却）
            this.nativeHud?.ForceMainRefresh();

            this.log.Information(
                $"[SrankFlag.Sonar] 实时击杀 {dc.dc}/{wd.world} {hunt.cn}({hunt.en}) " +
                $"lastDeath {old:0} → {now:0}（+{now - old:0}s）" +
                $" 概率 {oldPct:0.0}% → {newPct:0.0}% · 已强制刷新主窗");
        }
        catch (Exception ex)
        {
            try { this.log.Warning("[SrankFlag.Sonar] 写入实时击杀失败：" + ex.Message); } catch { }
        }
    }

    // ───────────────────── 持久化（独立文件，不动 5 个用户状态文件） ─────────────────────

    private void LoadSonarKills()
    {
        if (this.sonarKillsLoaded) return;
        this.sonarKillsLoaded = true;
        try
        {
            if (!File.Exists(this.SonarCachePath)) return;
            var f = JsonSerializer.Deserialize<SonarKillFile>(File.ReadAllText(this.SonarCachePath));
            if (f is null) return;
            if (f.kills is not null)
                foreach (var kv in f.kills)
                    if (kv.Value > 0) this.sonarKills[kv.Key] = kv.Value;
            if (f.recent is not null)
            {
                this.sonarRecent.AddRange(f.recent.Take(40));
                this.sonarRecent.Sort((a, b) => b.at.CompareTo(a.at));
            }
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Sonar] 读缓存失败：" + ex.Message); }
    }

    private void SaveSonarKills()
    {
        try
        {
            var f = new SonarKillFile { kills = this.sonarKills, recent = this.sonarRecent };
            File.WriteAllText(this.SonarCachePath,
                JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Sonar] 写缓存失败：" + ex.Message); }
    }

    /// <summary>LoadSrankData() 末尾调用：重新从磁盘挂载数据、切大区后，把本地实时击杀时间补回去。
    /// 只写「比现有更晚」的值，和 MergeBearCache / ApplyBearResult 同一口径，不会被 HTTP 旧数据回滚。</summary>
    private void ApplySonarKills()
    {
        try
        {
            this.LoadSonarKills();
            if (this.sonarKills.Count == 0 || this.srankData?.dcs is not { Count: > 0 } dcs) return;
            foreach (var dc in dcs)
            {
                foreach (var h in dc.hunts)
                {
                    var touched = false;
                    foreach (var w in h.worlds)
                    {
                        var key = dc.dc + "|" + h.en + "|" + w.world;
                        if (this.sonarKills.TryGetValue(key, out var ts) && ts > w.lastDeath)
                        {
                            w.lastDeath = ts;
                            touched = true;
                        }
                    }
                    if (touched && h.worlds.Length > 0)
                    {
                        var mx = h.worlds.Max(w => w.lastDeath);
                        if (mx > h.lastDeath) h.lastDeath = mx;
                    }
                }
            }
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Sonar] 回放本地击杀缓存失败：" + ex.Message); }
    }

    // ───────────────────── /水桶 sonar：概率核对 + 链路自检 ─────────────────────

    private void SonarCommand(string sub)
    {
        this.LoadSonarKills();
        if (sub == "probe")
        {
            this.sonarProbeOk = false;
            this.sonarProbeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const string probe = "(Sonar) Rank S: __PROBE__ 测试地图 ( 1.0 , 1.0 ) <水晶塔> was just killed";
            this.framework.RunOnFrameworkThread(() => { try { this.chatGui.Print(probe); } catch { } });
            this.Print("已发出链路自检消息；2 秒后敲 /水桶 sonar 看「链路自检」。这条不会写入击杀数据。");
            return;
        }
        if (sub == "test")
        {
            this.Print("— Sonar 正则自检（只解析，不写任何数据）—");
            this.Print(this.SonarDryRun("Rank S: 盐和光 \ue0bb基拉巴尼亚湖区 ( 10.3 , 34.0 ) <晨曦王座> was just killed", "kill"));
            this.Print(this.SonarDryRun("(Sonar) Rank S: 戾虫 \ue0bb雷克兰德 ( 25.4  , 24.4 ) <\ue05d旅人栈桥>", "spawn"));
            this.Print(this.SonarDryRun("Rank S: 盐和光 \ue0bb基拉巴尼亚湖区 ( 10.3 , 34.0 ) <晨曦王座> despawned", "reset"));
            this.Print(this.SonarDryRun("Rank A: 戾虫 \ue0bb雷克兰德 ( 25.4 , 24.4 ) <旅人栈桥> was just killed", "drop"));
            this.Print(this.SonarDryRun("哈哈我刚才也看到 Rank S: 盐和光 <晨曦王座> was just killed 啦", "drop"));
            this.Print(this.SonarDryRun("Rank S: 盐和光 \ue0bb基拉巴尼亚湖区 ( 10.3 , 34.0 ) <晨曦王座> was just killed 快去", "drop"));
            return;
        }
        if (sub == "clear")
        {
            this.sonarKills.Clear();
            this.sonarRecent.Clear();
            this.SaveSonarKills();
            this.Print("已清空本地实时击杀缓存（不影响 srank_live.json / srank_data.json）");
            return;
        }

        this.Print($"— Sonar 实时击杀监听 — 缓存 {this.sonarKills.Count} 条 · 最近记录 {this.sonarRecent.Count} 条");
        var probeAt = this.sonarProbeAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)this.sonarProbeAt).ToLocalTime().ToString("MM-dd HH:mm:ss")
            : "未发过";
        this.Print("链路自检：" + (this.sonarProbeOk
            ? $"已收到 Chat.Print 输出，监听可用（{probeAt}）"
            : "未验证（敲 /水桶 sonar probe 可验证 ChatMessage 是否收得到 Sonar 消息）"));
        if (this.sonarUnmatched > 0)
            this.Print($"未匹配播报 {this.sonarUnmatched} 条，最近：{this.sonarUnmatchedSample}");
        if (this.sonarRecent.Count == 0)
        {
            this.Print("还没有捕获到击杀播报。/水桶 sonar test 可验证正则，/水桶 sonar probe 可验证链路。");
            return;
        }
        foreach (var r in this.sonarRecent.Take(8))
        {
            var t = DateTimeOffset.FromUnixTimeSeconds((long)r.at).ToLocalTime().ToString("MM-dd HH:mm:ss");
            this.Print($"{t} {r.dc}/{r.world} {r.cn}：站点 {r.old:0} → 本地 {r.kill:0}（差 {r.kill - r.old:0}s）" +
                       $" 概率 {r.oldPct:0.0}% → {r.newPct:0.0}%");
        }
    }

    /// <summary>只跑「清洗 + 两条正则」，不查白名单、不写数据，用来自检出生/击杀是否严格互斥。
    /// expect：kill / spawn / drop。</summary>
    private string SonarDryRun(string raw, string expect)
    {
        var text = CleanSonarText(raw);
        var kill = SonarKillRx.Match(text);
        var reset = SonarResetRx.Match(text);
        var spawn = SonarSpawnRx.Match(text);
        var got = kill.Success ? "kill" : reset.Success ? "reset" : spawn.Success ? "spawn" : "drop";
        var label = (got == expect) ? "[OK]" : "[NG]";
        var m = kill.Success ? kill : reset.Success ? reset : spawn;
        var detail = m.Success
            ? $"{got} head=[{m.Groups["head"].Value.Trim()}] world=[{m.Groups["world"].Value.Trim()}] " +
              $"({m.Groups["x"].Value},{m.Groups["y"].Value})"
            : "已丢弃";
        var want = expect switch { "kill" => "击杀", "spawn" => "出生", "reset" => "脱战/重置", _ => "丢弃" };
        return $"{label} 期望{want} · {detail}";
    }
}
