using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SrankFlag;

/// <summary>
/// v1.2.12：「已被他人触发」黑名单 —— 与主站窗口状态严格对齐的三段状态机。
///
/// 【判定口径（用户 11-13 第三次定稿：历史触发但没被击杀 = 已触发未死）】
///   把每只怪按主站 lastDeath 算出的窗口切成三段，**黑名单只在两端锁、中间放行**：
///
///     ① 冷却段  now &lt; ld+cd
///        刚从主站拿到未消化的击杀记录 ⇒ 有人刚打过，锁（反正这段也打不了）。
///        已被消化进 killedWatermark 的记录不再重复上锁（否则每次拉取都刷一遍红标）。
///
///     ② 闸内段  ld+cd ≤ now &lt; ld+cd+win
///        新周期已经打开 ⇒ 上一轮「被触发」的那只早就不在了，**解除主站来源的锁**，
///        红标消失、按钮恢复、自动前往重新可选。这一段正是唯一该去打的窗口。
///
///     ③ 闸后段  now ≥ ld+cd+win    ← 本次新增的核心判定
///        窗口整段走完，lastDeath 却没有任何更新 ⇒ 只有两种可能：
///          a) 已经被人触发过、打了一半跑了 / 团灭了 / 卡在某个状态（烂尾）；
///          b) 一直没人去。
///        用户口径：这种情况**不判定为「可以打」**，而是判定「已经被人触发了，但没有死」，
///        直接拉黑（Source=stuck），红标 [已触发]、按钮 [已被抢触]、自动前往跳过。
///        这段的锁只能被「击杀」（lastDeath 越过水位）或 Sonar 脱战/重置播报解除。
///
///   Sonar 出生播报仍然独立锁定（Source=sonar）：它证明「这一刻它已经刷出来了」，
///   不受上面三段窗口逻辑影响，只认击杀 / 脱战 / 主站陈旧超时。
///
/// 【为什么闸内必须放行，而不是继续锁】
///   闸内 if 还锁着，自动前往就永远排不出任何任务 —— 那不是防抢，是自杀。
///   闸内 = 新周期刚开 = 没有任何「已被触发」的证据（旧记录已随上一只怪一起消失）。
///
/// 【主站数据的事实（11-13 04:26 / 04:45 实测 http://119.29.119.179/api/live?dc=豆豆柴）】
///   { generated, dc, source:"tracker",
///     live: { 服: { 怪en: [[线, 击杀时间戳, 是否作废]] } }, relay: {}, trackerMeta, trackerStatus }
///   键是满的（5 服 × 47 怪 = 235），但只有 91 个有真实记录（38.7%），其余是空数组 []。
///   上游熊站 getHuntInfo / huntInfo 实测只返回 [{ID, Time, ReportTime, Reporter, Canceled}]，
///   **没有任何 engaged / HP / 进度 / 血量字段** —— 所以「进闸」只能由 lastDeath + cd 推算，
///   「被触发但没死」也只能由「闸走完却没新击杀」反推，不存在直接的存活信号。
///
///   04:45 实测这 91 条记录**全部还在冷却段**（age &lt; cd），闸内的 0 只、闸后 0 只。
///   ⇒ 本规则上线当刻不会锁任何怪（不是空转，是当前数据确实没有烂尾样本）。
///   另外 144 个槽位 lastDeath = 0（36h 内没有击杀记录，也无法确定窗口位置）：
///   这些**不参与判定** —— 把「没有记录」当成「闸早就过了」会把半张表全锁死。
///
/// 【已消化击杀水位 killedWatermark】
///   live 是约 36h 的击杀流水，同一条记录会被反复拉取十几次，故一次击杀用于解锁后
///   把它的 ts 记为已消化，之后同一（或更旧）ts 不再上锁 / 不再重复判定。
///
/// 【持久化】独立文件 srank_triggered.json（v7：items / floors / killed / seen / src），
///   绝不触碰 5 个用户状态文件。键 = en|world（英文名跨语言稳定）。
/// </summary>
public sealed partial class Plugin
{
    private const string TriggeredCacheFile = "srank_triggered.json";

    /// <summary>主站源开关。Enabled = 按主站窗口状态判定；Disabled = 只认 Sonar。</summary>
    private enum MainTriggerMode { Enabled, Disabled }

    private MainTriggerMode mainTriggerMode = MainTriggerMode.Enabled;

    /// <summary>「闸后未死」判定开关。关掉后闸后段不拉黑（只保留冷却段记录锁 + Sonar）。</summary>
    private bool stuckTriggerEnabled = true;

    /// <summary>兜底超时：主站数据陈旧时，超过这么久没被确认的条目自动解除。</summary>
    private static readonly TimeSpan TriggeredTimeout = TimeSpan.FromMinutes(10);

    /// <summary>主站「数据陈旧」判据：超过它没成功拉取，才允许超时兜底生效。</summary>
    private static readonly TimeSpan MainDataStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>窗口状态机的节流间隔（闸会随时间自然切换段位，不能只在拉取时算）。</summary>
    private static readonly TimeSpan TriggeredSweepInterval = TimeSpan.FromSeconds(30);

    private DateTime lastTriggeredSweepUtc = DateTime.MinValue;

    private const string SourceMain = "main";       // 主站明确信号（engaged / 非满血…，接口目前没有）
    private const string SourceRecord = "record";   // 主站有未消化的击杀记录
    private const string SourceStuck = "stuck";     // 闸已走完仍无击杀 = 已触发未死
    private const string SourceSonar = "sonar";     // Sonar 出生播报

    /// <summary>装的是「标记时刻」而不是 true：界面上要显示什么时候锁的。</summary>
    private readonly Dictionary<string, double> triggeredMonsters = new(StringComparer.Ordinal);

    /// <summary>
    /// 锁定时「这只怪已知的最后死亡时刻」（解锁水位）。解锁判据是 lastDeath &gt; 水位，
    /// 而不是 lastDeath ≥ 锁定时刻 —— 因为 lastDeath 在锁定的同一轮里就已经等于/晚于
    /// 锁定时刻了，用锁定时刻比会立刻把自己判成「已死」而解锁。
    /// </summary>
    private readonly Dictionary<string, double> triggeredFloor = new(StringComparer.Ordinal);

    /// <summary>锁的来源：record / stuck / main / sonar。决定闸内段能否解除它。</summary>
    private readonly Dictionary<string, string> triggeredSource = new(StringComparer.Ordinal);

    /// <summary>最后一次「主站仍在确认这条锁」的时刻；主站陈旧时的超时兜底用它。</summary>
    private readonly Dictionary<string, double> triggeredSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// 「已消化的击杀水位」：key → 我**已经处理过**的那次击杀时刻。
    /// 防止同一条 live 记录在解锁后被反复拿来上锁。
    /// </summary>
    private readonly Dictionary<string, double> killedWatermark = new(StringComparer.Ordinal);

    /// <summary>
    /// 「闸后判定已做过的 lastDeath」：key → 已经下过结论的那次死亡时刻。
    /// 闸后段不能用 killedWatermark 去重（那里 wm 恒等于 ld，会把判定永久跳过），
    /// 所以单独记一份：只有 lastDeath 又变了（说明中间死过一次、进入新周期）才重新判一次。
    /// 同时让 /水桶 trigger clear 之后不会立刻把旧怪全部锁回。
    /// </summary>
    private readonly Dictionary<string, double> stuckJudged = new(StringComparer.Ordinal);

    private bool triggeredLoaded;

    private string TriggeredCachePath => Path.Combine(this.pluginDirectory, TriggeredCacheFile);

    /// <summary>黑名单唯一键：英文怪名 + 服名。</summary>
    internal static string TrigKey(string en, string world) => en + "|" + world;

    private sealed class TriggeredFile
    {
        public int v { get; set; } = 7;
        public string mainMode { get; set; } = "on";   // on | off（旧文件可能是 auto / force）
        public bool stuck { get; set; } = true;        // 闸后未死判定
        public Dictionary<string, double> items { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> floors { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> killed { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> judged { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> seen { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> src { get; set; } = new(StringComparer.Ordinal);
    }

    private static string MainModeText(MainTriggerMode m) =>
        m == MainTriggerMode.Disabled ? "off" : "on";

    /// <summary>来源的中文短名（命令输出用）。</summary>
    private static string SourceText(string? src) => src switch
    {
        SourceSonar => "Sonar出生",
        SourceStuck => "闸过未死",
        SourceRecord => "主站记录",
        _ => "主站信号",
    };

    /// <summary>是否「主站侧」的锁（闸内段可以解除）。Sonar 出生锁不在此列。</summary>
    private static bool IsMainSide(string? src) => !string.Equals(src, SourceSonar, StringComparison.Ordinal);

    // ───────────────────── 读写 ─────────────────────

    private void LoadTriggered()
    {
        if (this.triggeredLoaded) return;
        this.triggeredLoaded = true;
        try
        {
            if (!File.Exists(this.TriggeredCachePath)) return;
            var f = JsonSerializer.Deserialize<TriggeredFile>(File.ReadAllText(this.TriggeredCachePath));
            if (f is null) return;
            // v3 的 auto / force 同义（都 = 有就锁），统一映射到 Enabled
            this.mainTriggerMode = (f.mainMode ?? "").Trim().ToLowerInvariant() == "off"
                ? MainTriggerMode.Disabled
                : MainTriggerMode.Enabled;
            // v7 之前没有 stuck 字段：默认开启（用户 11-13 明确要求）
            this.stuckTriggerEnabled = f.stuck;

            if (f.items is not null)
                foreach (var kv in f.items)
                {
                    if (kv.Key.Length == 0 || kv.Value <= 0) continue;
                    this.triggeredMonsters[kv.Key] = kv.Value;
                    this.triggeredFloor[kv.Key] = f.floors is not null && f.floors.TryGetValue(kv.Key, out var fl)
                        ? fl
                        : kv.Value;
                    // v5 之前没有 src：老条目一律按 record 处理（它们当年都是主站记录锁的）
                    this.triggeredSource[kv.Key] = f.src is not null && f.src.TryGetValue(kv.Key, out var s) && s.Length > 0
                        ? s
                        : SourceRecord;
                    this.triggeredSeen[kv.Key] = f.seen is not null && f.seen.TryGetValue(kv.Key, out var sn)
                        ? sn
                        : kv.Value;
                }
            if (f.killed is not null)
                foreach (var kv in f.killed)
                    if (kv.Key.Length > 0 && kv.Value > 0) this.killedWatermark[kv.Key] = kv.Value;
            if (f.judged is not null)
                foreach (var kv in f.judged)
                    if (kv.Key.Length > 0 && kv.Value > 0) this.stuckJudged[kv.Key] = kv.Value;
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 读黑名单失败：" + ex.Message); }
    }

    private void SaveTriggered()
    {
        try
        {
            var f = new TriggeredFile
            {
                v = 7,
                mainMode = MainModeText(this.mainTriggerMode),
                stuck = this.stuckTriggerEnabled,
                items = this.triggeredMonsters,
                floors = this.triggeredFloor,
                killed = this.killedWatermark,
                judged = this.stuckJudged,
                seen = this.triggeredSeen,
                src = this.triggeredSource,
            };
            File.WriteAllText(this.TriggeredCachePath,
                JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 写黑名单失败：" + ex.Message); }
    }

    internal bool IsTriggered(string en, string world)
    {
        if (en.Length == 0 || world.Length == 0) return false;
        this.LoadTriggered();
        return this.triggeredMonsters.ContainsKey(TrigKey(en, world));
    }

    // ───────────────────── 写入 / 解锁 ─────────────────────

    /// <summary>
    /// 拉黑。已锁的保持水位与时刻，但会刷新「主站仍在确认」的时刻（让超时兜底不会误伤）。
    /// 返回是否为新拉黑。
    /// </summary>
    private bool MarkTriggered(string en, string world, double deathFloor, double at, string reason,
                               string source = SourceMain)
    {
        if (en.Length == 0 || world.Length == 0) return false;
        this.LoadTriggered();
        var key = TrigKey(en, world);
        var stamp = at > 0 ? at : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (this.triggeredMonsters.ContainsKey(key))
        {
            this.triggeredSeen[key] = Math.Max(this.triggeredSeen.TryGetValue(key, out var s0) ? s0 : 0, stamp);
            return false;   // 已锁：绝不刷新水位（否则锁会越推越死）
        }

        this.triggeredMonsters[key] = stamp;
        this.triggeredFloor[key] = deathFloor > 0 ? deathFloor : 0;
        this.triggeredSource[key] = source;
        this.triggeredSeen[key] = stamp;
        this.SaveTriggered();
        this.log.Information($"[SrankFlag.Trigger] 拉黑 {world} · {en}（{reason}，{source}，解锁水位 {deathFloor:F0}）");
        this.nativeHud?.ForceMainRefresh();
        return true;
    }

    /// <summary>
    /// 解锁。killedAt &gt; 0 表示这次解锁由击杀造成，要写进已消化水位
    /// （否则主站下一轮会把同一条击杀记录再锁回来）。返回是否真的从黑名单移除了。
    /// </summary>
    private bool ClearTriggered(string en, string world, string reason, double killedAt = 0, string? onlyIfSource = null)
    {
        if (en.Length == 0 || world.Length == 0) return false;
        this.LoadTriggered();
        var key = TrigKey(en, world);

        if (onlyIfSource is not null &&
            this.triggeredSource.TryGetValue(key, out var src) &&
            !string.Equals(src, onlyIfSource, StringComparison.Ordinal))
            return false;

        var removed = this.triggeredMonsters.Remove(key);
        var hadFloor = this.triggeredFloor.Remove(key);
        this.triggeredSource.Remove(key);
        this.triggeredSeen.Remove(key);

        var wmChanged = false;
        if (killedAt > 0)
        {
            this.killedWatermark.TryGetValue(key, out var prev);
            if (killedAt > prev)
            {
                this.killedWatermark[key] = killedAt;
                wmChanged = true;
            }
        }

        if (!removed && !hadFloor && !wmChanged) return false;
        this.SaveTriggered();
        if (removed)
        {
            this.log.Information($"[SrankFlag.Trigger] 解锁 {world} · {en}（{reason}）");
            this.nativeHud?.ForceMainRefresh();
        }
        return removed;
    }

    /// <summary>把当前所有已知的 lastDeath 记为「已消化」——用于 trigger clear 后不让旧记录立刻锁回。</summary>
    private void SeedKilledWatermarkFromKnownDeaths()
    {
        if (this.srankData?.dcs is not { Count: > 0 } dcs) return;
        foreach (var dc in dcs)
            foreach (var h in dc.hunts)
                foreach (var w in h.worlds)
                {
                    if (w.lastDeath <= 0) continue;
                    var key = TrigKey(h.en, w.world);
                    this.killedWatermark.TryGetValue(key, out var prev);
                    if (w.lastDeath > prev) this.killedWatermark[key] = w.lastDeath;
                    // 闸后判定也要设基线：否则 clear 之后一轮 sweep 就把所有「窗口已过」的怪全锁回来
                    this.stuckJudged.TryGetValue(key, out var jprev);
                    if (w.lastDeath > jprev) this.stuckJudged[key] = w.lastDeath;
                }
    }

    /// <summary>
    /// 用「击杀时间越过解锁水位」批量解锁（主站补报了击杀，或插件没开时被打死）。
    /// 不是超时 —— 只要 lastDeath 没越过水位就保持拉黑。这条对所有来源都成立：
    /// 死了就是死了，Sonar 出生锁同样该解除（进入冷却）。
    /// </summary>
    private void SweepTriggeredByDeath()
    {
        if (this.triggeredMonsters.Count == 0) return;
        if (this.srankData?.dcs is not { Count: > 0 } dcs) return;
        List<(string Key, double Death)>? drop = null;
        foreach (var dc in dcs)
            foreach (var h in dc.hunts)
                foreach (var w in h.worlds)
                {
                    var key = TrigKey(h.en, w.world);
                    if (!this.triggeredMonsters.ContainsKey(key)) continue;
                    this.triggeredFloor.TryGetValue(key, out var floor);
                    if (w.lastDeath <= 0) continue;
                    if (w.lastDeath <= floor) continue;   // 还是锁之前那次死亡
                    (drop ??= new List<(string, double)>()).Add((key, w.lastDeath));
                }
        if (drop is null) return;
        foreach (var (k, death) in drop)
        {
            this.triggeredMonsters.Remove(k);
            this.triggeredFloor.Remove(k);
            this.triggeredSource.Remove(k);
            this.triggeredSeen.Remove(k);
            this.killedWatermark.TryGetValue(k, out var prev);
            if (death > prev) this.killedWatermark[k] = death;
        }
        this.SaveTriggered();
        this.log.Information($"[SrankFlag.Trigger] 击杀时间越过水位，解锁 {drop.Count} 只");
        this.nativeHud?.ForceMainRefresh();
    }

    /// <summary>用维护后再生（mt）覆盖 cd/win —— 与 Calc / 主窗显示同一口径。</summary>
    private (double Cd, double Win) WindowOf(HuntData h, double lastDeath)
    {
        var maint = this.restartAt > 0 && lastDeath > 0 && this.restartAt > lastDeath;
        var cd = maint && h.mt is { Length: >= 1 } ? h.mt[0] : h.cd;
        var win = maint && h.mt is { Length: >= 2 } ? h.mt[1] : h.win;
        return (cd, win);
    }

    /// <summary>
    /// 【核心】按窗口三段同步黑名单（每 30s + 每次主站拉取后跑一次）。
    ///
    ///   cd 段（now &lt; ld+cd）  ：有未消化的击杀记录 ⇒ 锁（record）。已消化的不再重复上锁。
    ///   open 段（闸内）        ：解除主站侧的锁 —— 新周期开始，上一只「被触发」的怪已不在。
    ///   over 段（闸已走完）     ：仍无新击杀 ⇒ 已触发未死 ⇒ 锁（stuck），直到它真的被击杀。
    ///
    /// 段位判定直接复用 Calc()，与主窗「开闸 / 冷却 / 已过窗」显示永远一致。
    /// lastDeath &lt;= 0 的槽位直接跳过：没有死亡时间就没有窗口，无法区分「还没开闸」与「刷出很久没人打」。
    /// </summary>
    private void SweepTriggeredWindow()
    {
        if (this.srankData?.dcs is not { Count: > 0 } dcs) return;
        var mainOn = this.mainTriggerMode == MainTriggerMode.Enabled;
        if (!mainOn && !this.stuckTriggerEnabled) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var added = 0;
        var released = 0;
        var changed = false;

        foreach (var dc in dcs)
            foreach (var h in dc.hunts)
                foreach (var w in h.worlds)
                {
                    var ld = w.lastDeath;
                    if (ld <= 0) continue;                       // 无记录 = 无法定位窗口，不判定
                    var (cd, win) = this.WindowOf(h, ld);
                    if (cd <= 0) continue;

                    var state = Calc(ld, now, cd, win).State;
                    var key = TrigKey(h.en, w.world);
                    var locked = this.triggeredMonsters.ContainsKey(key);
                    this.triggeredSource.TryGetValue(key, out var src);

                    if (state == "cd")
                    {
                        if (!mainOn || locked) continue;
                        // 已消化的击杀不再上锁：否则每次拉取都给同一只怪刷一遍红标
                        this.killedWatermark.TryGetValue(key, out var wm);
                        if (ld <= wm) continue;
                        if (this.MarkTriggered(h.en, w.world, ld, now, "主站击杀记录（冷却中）", SourceRecord))
                        { added++; changed = true; }
                    }
                    else if (state == "open")
                    {
                        // 闸内 = 新周期 = 上一只被触发的实例已经不存在了 ⇒ 放行
                        if (!locked || !IsMainSide(src)) continue;
                        if (this.ClearTriggered(h.en, w.world, "闸已开（新周期放行）", ld)) released++;
                        this.killedWatermark.TryGetValue(key, out var wm2);
                        if (ld > wm2) { this.killedWatermark[key] = ld; changed = true; }
                    }
                    else // "over"：窗口整段走完，lastDeath 却没有更新
                    {
                        if (!mainOn || !this.stuckTriggerEnabled || locked) continue;
                        // 同一份 lastDeath 只判一次：判过就记进 stuckJudged，只有它又死过一次
                        // （ld 变大、进入新周期）才允许重新判定。这样「主站否认 → 解锁」不会被
                        // 下一轮 sweep 立刻锁回来，/水桶 trigger clear 也不会清完就回弹。
                        this.stuckJudged.TryGetValue(key, out var judged);
                        if (ld <= judged) continue;
                        this.stuckJudged[key] = ld;
                        changed = true;
                        if (this.MarkTriggered(h.en, w.world, ld, now,
                                "闸已走完仍未击杀（判定为已触发未死）", SourceStuck))
                            added++;
                    }
                }

        if (changed) this.SaveTriggered();
        if (added > 0 || released > 0)
            this.log.Information(
                $"[SrankFlag.Trigger] 窗口同步：新增锁 {added} 只 / 闸开放行 {released} 只，" +
                $"当前共 {this.triggeredMonsters.Count} 只（主站 {MainModeText(this.mainTriggerMode)}，闸后判定 {(this.stuckTriggerEnabled ? "开" : "关")}）");
    }

    /// <summary>主站数据是否已经陈旧（超过 10 分钟没成功拉取，或从未成功）。</summary>
    private bool MainDataStale =>
        this.lastBearOk == default || DateTime.UtcNow - this.lastBearOk > MainDataStaleAfter;

    /// <summary>
    /// 超时兜底：**只在主站数据陈旧时生效**。主站正常时每一轮确认都会刷新 triggeredSeen，
    /// 这条永远不该解除任何条目 —— 否则刚锁上的怪会被无脑放掉，防抢就白做了。
    /// </summary>
    private void SweepStaleByTimeout()
    {
        if (this.triggeredMonsters.Count == 0) return;
        if (!this.MainDataStale) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var drop = this.triggeredMonsters.Keys
            .Where(k => now - (this.triggeredSeen.TryGetValue(k, out var s) && s > 0 ? s : 0) > TriggeredTimeout.TotalSeconds)
            .ToList();
        if (drop.Count == 0) return;
        foreach (var k in drop)
        {
            this.triggeredMonsters.Remove(k);
            this.triggeredFloor.Remove(k);
            this.triggeredSource.Remove(k);
            this.triggeredSeen.Remove(k);
        }
        this.SaveTriggered();
        this.log.Warning($"[SrankFlag.Trigger] 主站数据陈旧，超时兜底解锁 {drop.Count} 只");
        this.nativeHud?.ForceMainRefresh();
    }

    /// <summary>主循环里按 30s 节流调用：窗口三段同步 + 主站陈旧超时。</summary>
    private void TickTriggeredSweep()
    {
        if (DateTime.UtcNow - this.lastTriggeredSweepUtc < TriggeredSweepInterval) return;
        this.lastTriggeredSweepUtc = DateTime.UtcNow;
        try { this.SweepTriggeredWindow(); } catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 窗口同步失败：" + ex.Message); }
        try { this.SweepStaleByTimeout(); } catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 超时兜底失败：" + ex.Message); }
    }

    // ───────────────────── 主站源：明确状态字段解析 ─────────────────────

    /// <summary>
    /// 从 /api/live 的 JSON 里挑出「这只怪当前被触发 / 明确未被触发」的信号。
    ///
    /// 正信号容器（存在且为真）：triggered / engaged / isEngaged / spawned / engagedBy / relay
    /// 负信号：同一容器存在但值为 false / 0 / 空数组 / 空对象 ⇒ 主站明确说「没在打」。
    /// 血量类（若站点将来给出对象结构）：hp / currentHp / hpPercent / currentHpPercent / progress
    ///   · 0 &lt; v &lt; maxHp（或 &lt; 100）→ 正在被打 → 正信号
    ///   · v &lt;= 0 或 v &gt;= maxHp / 100 → 满血或归零 → 负信号
    ///
    /// 线上接口目前只有击杀时间，这些容器全都不存在，函数会静默返回。
    /// 只往传入的并发字典里塞字符串，绝不碰共享状态（在 HTTP 线程上跑）。
    /// </summary>
    internal static void CollectTriggeredJson(
        JsonElement root,
        ConcurrentDictionary<string, double> positive,
        ConcurrentDictionary<string, double>? negative = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var field in new[] { "triggered", "engaged", "isEngaged", "spawned", "engagedBy", "relay" })
        {
            if (!root.TryGetProperty(field, out var block)) continue;
            if (block.ValueKind != JsonValueKind.Object) continue;
            foreach (var world in block.EnumerateObject())
            {
                if (world.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var hunt in world.Value.EnumerateObject())
                {
                    var key = TrigKey(hunt.Name, world.Name);
                    if (TriggeredTruthy(hunt.Value)) positive[key] = stamp;
                    else negative?[key] = stamp;
                }
            }
        }

        foreach (var field in new[] { "hp", "currentHp", "hpPercent", "currentHpPercent", "progress" })
        {
            if (!root.TryGetProperty(field, out var block)) continue;
            if (block.ValueKind != JsonValueKind.Object) continue;
            foreach (var world in block.EnumerateObject())
            {
                if (world.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var hunt in world.Value.EnumerateObject())
                {
                    if (hunt.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!hunt.Value.TryGetDouble(out var v)) continue;
                    double max = 0;
                    if (world.Value.TryGetProperty("maxHp", out var mx) && mx.ValueKind == JsonValueKind.Number)
                        mx.TryGetDouble(out max);
                    var ceiling = max > 0 ? max : 100;
                    var key = TrigKey(hunt.Name, world.Name);
                    if (v > 0 && v < ceiling) positive[key] = stamp;
                    else negative?[key] = stamp;
                }
            }
        }
    }

    private static bool TriggeredTruthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => e.TryGetDouble(out var d) && d > 0,
        JsonValueKind.String => e.GetString() is { Length: > 0 } s &&
                                !s.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                !s.Equals("dead", StringComparison.OrdinalIgnoreCase),
        // 数组按 /api/live 的行格式理解：[线, 时间戳, 是否作废]。整批都被作废的不算。
        JsonValueKind.Array => e.EnumerateArray().Any(row =>
            !(row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 3 &&
              row[2].ValueKind == JsonValueKind.True)),
        JsonValueKind.Object => e.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>当前大区里这只怪在该服的 lastDeath；查不到返回 0（= 从没死过）。</summary>
    private double DeathOf(string en, string world)
    {
        if (this.srankData?.data is { } d)
            foreach (var h in d.hunts)
            {
                if (!string.Equals(h.en, en, StringComparison.Ordinal)) continue;
                foreach (var w in h.worlds)
                    if (string.Equals(w.world, world, StringComparison.Ordinal)) return w.lastDeath;
            }
        return 0;
    }

    /// <summary>
    /// 主站源的精确定时信号（接口目前没有，站点将来补 engaged / relay / 非满血 才会走到这里）。
    ///
    /// 只处理两类：
    ///   ① 明确正信号 → 锁（Source=main），锁定水位取当前 lastDeath；
    ///   ② 明确负信号（false / 满血 / 空数组）→ 立刻解锁，红标消失、按钮恢复可点。
    ///
    /// 窗口三段（冷却记录 / 闸内放行 / 闸后未死）全部在 SweepTriggeredWindow 里统一算，
    /// 不在这里重复，避免两处口径打架。
    /// </summary>
    private void ApplyMainSiteTriggered(
        ConcurrentDictionary<string, double>? explicitSignals,
        ConcurrentDictionary<string, double>? negativeSignals,
        HashSet<string>? justDied)
    {
        this.LoadTriggered();
        if (this.mainTriggerMode == MainTriggerMode.Disabled) return;

        var added = 0;
        var cleared = 0;

        // ── ① 主站明确否认：立刻解除（红标消失、按钮恢复可点）
        if (negativeSignals is { IsEmpty: false })
        {
            foreach (var kv in negativeSignals)
            {
                if (justDied is not null && justDied.Contains(kv.Key)) continue;
                if (explicitSignals is not null && explicitSignals.ContainsKey(kv.Key)) continue;   // 自相矛盾时以正信号为准
                var seg = kv.Key.Split('|');
                if (seg.Length != 2) continue;
                if (this.ClearTriggered(seg[0], seg[1], "主站明确未触发")) cleared++;
            }
        }

        // ── ② 主站明确信号：engaged / isEngaged / spawned / 非满血 等
        if (explicitSignals is { IsEmpty: false })
        {
            foreach (var kv in explicitSignals)
            {
                if (justDied is not null && justDied.Contains(kv.Key)) continue;
                var seg = kv.Key.Split('|');
                if (seg.Length != 2) continue;
                if (this.MarkTriggered(seg[0], seg[1], this.DeathOf(seg[0], seg[1]), kv.Value,
                                       "主站明确信号（正在被触发）", SourceMain))
                    added++;
            }
        }

        if (added > 0 || cleared > 0)
            this.log.Information(
                $"[SrankFlag.Trigger] 主站信号：新增 {added} 只 / 解除 {cleared} 只，" +
                $"当前共 {this.triggeredMonsters.Count} 只");
    }

    // ───────────────────── /水桶 trigger：查看 / 开关 / 演练 / 清空 ─────────────────────

    private void TriggerCommand(string[] parts)
    {
        this.LoadTriggered();
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        // /水桶 trigger main on|off —— 主站源开关（auto / force 为旧命令，等价于 on）
        if (sub == "main")
        {
            var arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            this.mainTriggerMode = arg == "off" ? MainTriggerMode.Disabled : MainTriggerMode.Enabled;
            this.SaveTriggered();
            var desc = this.mainTriggerMode == MainTriggerMode.Disabled
                ? "关闭：主站不再参与判定，只认 Sonar 出生播报"
                : "开启：冷却段锁记录 / 闸内放行 / 闸后未死上锁（默认）";
            this.Print($"主站源 = {MainModeText(this.mainTriggerMode)}（{desc}）。" +
                       "30 秒内自动生效（/水桶 trigger scan 可立刻看判定结果）。");
            return;
        }

        // /水桶 trigger stuck on|off —— 「闸已走完仍未击杀 = 已触发未死」判定
        if (sub == "stuck")
        {
            var arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            this.stuckTriggerEnabled = arg != "off";
            this.SaveTriggered();
            this.Print($"闸后未死判定 = {(this.stuckTriggerEnabled ? "on" : "off")}（" +
                       (this.stuckTriggerEnabled
                           ? "窗口走完仍未击杀 → 判定已触发未死 → 拉黑，只有击杀/脱战才解除"
                           : "窗口走完不拉黑，仍可自动前往") + "）。");
            if (arg.Length == 0) this.Print("用法：/水桶 trigger stuck on|off");
            return;
        }

        // /水桶 trigger scan —— 按当前数据演练一遍，只打印不写盘
        if (sub == "scan")
        {
            this.PrintTriggerScan();
            return;
        }

        if (sub == "clear")
        {
            var n = this.triggeredMonsters.Count;
            this.triggeredMonsters.Clear();
            this.triggeredFloor.Clear();
            this.triggeredSource.Clear();
            this.triggeredSeen.Clear();
            this.SeedKilledWatermarkFromKnownDeaths();
            this.SaveTriggered();
            this.nativeHud?.ForceMainRefresh();
            this.Print($"已清空「已被触发」黑名单（{n} 条）。清空后这些 S 会重新进入自动前往队列；" +
                       "现有击杀记录视为已消化，不会再被锁回。");
            return;
        }

        this.Print($"— 已被他人触发的 S — 共 {this.triggeredMonsters.Count} 条" +
                   $"（主站 {MainModeText(this.mainTriggerMode)} · 闸后判定 {(this.stuckTriggerEnabled ? "on" : "off")}）");
        if (this.triggeredMonsters.Count == 0)
        {
            this.Print("当前没有拉黑任何 S。/水桶 trigger scan 可看各段判定结果。");
            return;
        }
        foreach (var kv in this.triggeredMonsters.OrderByDescending(x => x.Value).Take(12))
        {
            var t = DateTimeOffset.FromUnixTimeSeconds((long)kv.Value).ToLocalTime().ToString("MM-dd HH:mm:ss");
            var seg = kv.Key.Split('|');
            this.triggeredSource.TryGetValue(kv.Key, out var src);
            this.Print($"  {t}  {(seg.Length > 1 ? seg[1] : "")} · {(seg.Length > 0 ? seg[0] : kv.Key)}" +
                       $"（{SourceText(src)}）");
        }
        if (this.triggeredMonsters.Count > 12)
            this.Print($"  …另有 {this.triggeredMonsters.Count - 12} 条");
    }

    /// <summary>
    /// 演练：只统计「按当前 lastDeath 落在哪一段」，不写任何数据。
    /// 用来确认规则没误伤 —— 尤其是闸内段必须是全部放行。
    /// </summary>
    private void PrintTriggerScan()
    {
        if (this.srankData?.dcs is not { Count: > 0 } dcs)
        {
            this.Print("还没有加载 S 数据，待主站同步后再试。");
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int cdLock = 0, openPass = 0, stuckLock = 0, unknown = 0, sonarLocked = 0;
        var samples = new List<string>();

        foreach (var dc in dcs)
            foreach (var h in dc.hunts)
                foreach (var w in h.worlds)
                {
                    var key = TrigKey(h.en, w.world);
                    var locked = this.triggeredMonsters.ContainsKey(key);
                    this.triggeredSource.TryGetValue(key, out var src);
                    if (locked && string.Equals(src, SourceSonar, StringComparison.Ordinal)) { sonarLocked++; continue; }

                    var ld = w.lastDeath;
                    if (ld <= 0) { unknown++; continue; }
                    var (cd, win) = this.WindowOf(h, ld);
                    if (cd <= 0) { unknown++; continue; }
                    switch (Calc(ld, now, cd, win).State)
                    {
                        case "cd": cdLock++; break;
                        case "open":
                            openPass++;
                            if (samples.Count < 5)
                                samples.Add($"{w.world}·{h.cn} 闸内（放行）");
                            break;
                        default:
                            stuckLock++;
                            samples.Add($"{w.world}·{h.cn} 闸已过 {(now - (ld + cd * 3600 + win * 3600)) / 3600.0:F1}h 仍无击杀 → 锁");
                            break;
                    }
                }

        this.Print($"— 黑名单演练（{DateTime.Now:HH:mm:ss}，只统计不写盘）—");
        this.Print($"  冷却段（有未消化记录→锁）：{cdLock} 只");
        this.Print($"  闸内段（→放行，可以去）：{openPass} 只");
        this.Print($"  闸后段（→判定已触发未死，锁）：{stuckLock} 只");
        this.Print($"  无击杀记录（无法定位窗口，跳过）：{unknown} 只");
        this.Print($"  Sonar 出生锁（只认击杀/脱战）：{sonarLocked} 只");
        foreach (var s in samples.Take(6)) this.Print("   · " + s);
    }
}
