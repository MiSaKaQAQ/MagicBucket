using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SrankFlag;

/// <summary>
/// v1.2.13：「已被他人触发」黑名单 —— 与主站看板 https://119.29.119.179/srank/ 的
/// 「已触发（当前在场）」严格同源同口径。
///
/// 【为什么推倒 v1.2.12 的三段窗口推算】
///   v1.2.12 用 lastDeath + cd/win 反推「进闸 / 闸后」，那是**猜**。实测代价：
///   插件离线一段时间后 srank_data.json 陈旧，所有记录都落到「闸后段」⇒ 全表拉黑。
///   而看板本来就有真实的「当前在场」数据，不需要猜。
///
/// 【看板口径（index.html 的 T(n,e)，逐字对应）】
///   data = bot_relay.json
///     { generated, relay: { 服: { 怪en: [播报时间, x, y] } } }
///   一条 relay 记录「算在场」当且仅当：
///     ① relay 里有这只怪（ts &gt; 0）；
///     ② now - ts ≤ 20 小时            （看板：Date.now()/1e3 - t[0] &gt; 20*3600 → null）
///     ③ 之后没有更新的击杀             （看板：C(n,e) &gt;= t[0]-30 → null）
///   三条同时成立 ⇒ 看板显示红色「已触发」，插件同样标红、按钮置灰、自动前往跳过。
///   任一条不成立 ⇒ 看板不标红，插件也必须放行（红标消失、按钮恢复、可自动前往）。
///
///   即「主站说什么，插件就显示什么」，插件不再自己推算任何窗口。
///
/// 【实测（11-13 05:05，bot_relay.json + /api/live?dc=豆豆柴）】
///   relay 20 服 / 320 条：超 20h 过期 35 条、之后已被击杀 62 条 ⇒ 看板在场 223 条。
///   逐服在场数 2..20，不是全表；对照 v1.2.12 的「全红」，差距就在这里。
///
/// 【Sonar 出生播报仍然独立锁定（Source=sonar）】
///   它是「这一刻我刚看到它刷出来」的直接证据，比看板更快。只认击杀 / 脱战 / 超时兜底解除，
///   不会被看板的「未在场」覆盖 —— 否则刚锁上就被下一轮 relay 同步解掉，等于白锁。
///
/// 【持久化】独立文件 srank_triggered.json（v8：items/floors/killed/seen/src），
///   绝不触碰 5 个用户状态文件。键 = en|world（英文名跨语言稳定）。
/// </summary>
public sealed partial class Plugin
{
    private const string TriggeredCacheFile = "srank_triggered.json";

    /// <summary>看板「已触发（当前在场）」的数据源，静态文件，与 index.html 同源。</summary>
    private const string RelayApi = "http://119.29.119.179/srank/bot_relay.json";

    /// <summary>主站源开关。Enabled = 跟看板走；Disabled = 只认 Sonar 出生播报。</summary>
    private enum MainTriggerMode { Enabled, Disabled }

    private MainTriggerMode mainTriggerMode = MainTriggerMode.Enabled;

    /// <summary>看板判据：relay 记录超过 20 小时不再算「在场」（与 index.html 的 20*3600 一致）。</summary>
    private const double RelayMaxAgeSeconds = 20 * 3600;

    /// <summary>看板判据：击杀时间只比 relay 晚 30 秒以内，仍算「同一次击杀前的播报」（容差，同 index.html）。</summary>
    private const double RelayKillTolerance = 30;

    /// <summary>relay 拉取间隔。站点约 5 分钟刷新一次，3 分钟拉一次足够跟手（14 KB 静态文件）。</summary>
    private static readonly TimeSpan RelayInterval = TimeSpan.FromMinutes(3);

    /// <summary>relay 超过这么久没成功拉取 ⇒ 认为主站挂了，超时兜底才开始生效。</summary>
    private static readonly TimeSpan RelayStaleAfter = TimeSpan.FromMinutes(20);

    /// <summary>主站陈旧时的兜底超时：超过这么久没被确认的条目自动解除。</summary>
    private static readonly TimeSpan TriggeredTimeout = TimeSpan.FromMinutes(10);

    /// <summary>节流：主循环每 30 秒跑一次（拉 relay + 超时兜底）。</summary>
    private static readonly TimeSpan TriggeredSweepInterval = TimeSpan.FromSeconds(30);

    private DateTime lastTriggeredSweepUtc = DateTime.MinValue;

    private const string SourceRelay = "relay";     // 看板显示在场
    private const string SourceSonar = "sonar";     // Sonar 出生播报

    /// <summary>relay 快照：服 → 怪en → [播报时间, x, y]。只在主线程读写。</summary>
    private Dictionary<string, Dictionary<string, double[]>> relayLive = new(StringComparer.Ordinal);
    private double relayGenerated;
    private DateTime lastRelayFetch = DateTime.MinValue;
    private DateTime lastRelayOk = DateTime.MinValue;
    private bool relayFetching;
    private string relayStatus = "看板未同步";

    /// <summary>装的是「标记时刻」而不是 true：界面上要显示什么时候锁的。</summary>
    private readonly Dictionary<string, double> triggeredMonsters = new(StringComparer.Ordinal);

    /// <summary>
    /// 锁定时「这只怪已知的最后死亡时刻」（解锁水位）。解锁判据是 lastDeath &gt; 水位，
    /// 而不是 lastDeath ≥ 锁定时刻 —— 因为 lastDeath 在锁定的同一轮里就已经等于/晚于
    /// 锁定时刻了，用锁定时刻比会立刻把自己判成「已死」而解锁。
    /// </summary>
    private readonly Dictionary<string, double> triggeredFloor = new(StringComparer.Ordinal);

    /// <summary>锁的来源：relay / sonar。决定看板撤下这条时能否自动解除。</summary>
    private readonly Dictionary<string, string> triggeredSource = new(StringComparer.Ordinal);

    /// <summary>最后一次「主站仍在确认这条锁」的时刻；主站陈旧时的超时兜底用它。</summary>
    private readonly Dictionary<string, double> triggeredSeen = new(StringComparer.Ordinal);

    /// <summary>某次击杀已经用来解锁过 ⇒ 不再重复处理（防止同一轮反复刷 UI/日志）。</summary>
    private readonly Dictionary<string, double> killedWatermark = new(StringComparer.Ordinal);

    private bool triggeredLoaded;

    private string TriggeredCachePath => Path.Combine(this.pluginDirectory, TriggeredCacheFile);

    /// <summary>黑名单唯一键：英文怪名 + 服名。</summary>
    internal static string TrigKey(string en, string world) => en + "|" + world;

    private sealed class TriggeredFile
    {
        public int v { get; set; } = 8;
        public string mainMode { get; set; } = "on";   // on | off
        public Dictionary<string, double> items { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> floors { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> killed { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> seen { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> src { get; set; } = new(StringComparer.Ordinal);
    }

    private static string MainModeText(MainTriggerMode m) =>
        m == MainTriggerMode.Disabled ? "off" : "on";

    /// <summary>来源的中文短名（命令输出用）。</summary>
    private static string SourceText(string? src) => src switch
    {
        SourceSonar => "Sonar出生",
        _ => "看板在场",
    };

    /// <summary>是否「主站侧」的锁（看板撤下时自动解除）。Sonar 出生锁不在此列。</summary>
    private static bool IsMainSide(string? src) =>
        !string.Equals(src, SourceSonar, StringComparison.Ordinal);

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

            if (f.items is not null)
                foreach (var kv in f.items)
                {
                    if (kv.Key.Length == 0 || kv.Value <= 0) continue;
                    this.triggeredMonsters[kv.Key] = kv.Value;
                    this.triggeredFloor[kv.Key] = f.floors is not null && f.floors.TryGetValue(kv.Key, out var fl)
                        ? fl
                        : kv.Value;
                    // 旧文件（v7 及以前）的 record / stuck / main 都是主站侧，统一当 relay 处理
                    this.triggeredSource[kv.Key] = f.src is not null && f.src.TryGetValue(kv.Key, out var s) &&
                                                   string.Equals(s, SourceSonar, StringComparison.Ordinal)
                        ? SourceSonar
                        : SourceRelay;
                    this.triggeredSeen[kv.Key] = f.seen is not null && f.seen.TryGetValue(kv.Key, out var sn)
                        ? sn
                        : kv.Value;
                }
            if (f.killed is not null)
                foreach (var kv in f.killed)
                    if (kv.Key.Length > 0 && kv.Value > 0) this.killedWatermark[kv.Key] = kv.Value;
        }
        catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 读黑名单失败：" + ex.Message); }
    }

    private void SaveTriggered()
    {
        try
        {
            var f = new TriggeredFile
            {
                v = 8,
                mainMode = MainModeText(this.mainTriggerMode),
                items = this.triggeredMonsters,
                floors = this.triggeredFloor,
                killed = this.killedWatermark,
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
    /// 拉黑。已锁的保持水位与时刻，只刷新「主站仍在确认」的时刻（让超时兜底不会误伤）。
    /// 返回是否为新拉黑。
    /// </summary>
    private bool MarkTriggered(string en, string world, double deathFloor, double at, string reason,
                               string source = SourceRelay)
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
    /// 解锁。killedAt &gt; 0 表示这次解锁由击杀造成，要写进已消化水位。
    /// onlyIfSource 非空时，只有该来源的锁才会被解除（看板撤下时不能动 Sonar 出生锁）。
    /// 返回是否真的从黑名单移除了。
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

    // ───────────────────── 看板 relay 同步 ─────────────────────

    /// <summary>看板数据是否已经陈旧（超过 20 分钟没成功拉取，或从未成功）。</summary>
    private bool RelayStale =>
        this.lastRelayOk == DateTime.MinValue || DateTime.UtcNow - this.lastRelayOk > RelayStaleAfter;

    /// <summary>按 3 分钟节流拉一次看板 relay 快照（force 用于刚启动/手动）。</summary>
    private void MaybeFetchRelay(bool force)
    {
        if (this.relayFetching) return;
        if (!force && DateTime.UtcNow - this.lastRelayFetch < RelayInterval) return;
        this.relayFetching = true;
        this.lastRelayFetch = DateTime.UtcNow;
        _ = Task.Run(() => this.FetchRelayAsync());
    }

    private async Task FetchRelayAsync()
    {
        try
        {
            using var resp = await BearHttp.Value.GetAsync(RelayApi).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                this.relayStatus = $"看板拉取失败 HTTP {(int)resp.StatusCode}";
                return;
            }
            var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("relay", out var rl) ||
                rl.ValueKind != JsonValueKind.Object)
            {
                this.relayStatus = "看板返回格式异常";
                return;
            }

            var gen = 0.0;
            if (root.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.Number)
                g.TryGetDouble(out gen);

            var parsed = new Dictionary<string, Dictionary<string, double[]>>(StringComparer.Ordinal);
            var total = 0;
            foreach (var w in rl.EnumerateObject())
            {
                if (w.Value.ValueKind != JsonValueKind.Object) continue;
                var m = new Dictionary<string, double[]>(StringComparer.Ordinal);
                foreach (var hh in w.Value.EnumerateObject())
                {
                    if (hh.Value.ValueKind != JsonValueKind.Array) continue;
                    var arr = hh.Value;
                    if (arr.GetArrayLength() < 3) continue;
                    if (arr[0].ValueKind != JsonValueKind.Number || !arr[0].TryGetDouble(out var ts) || ts <= 0)
                        continue;
                    var x = arr[1].ValueKind == JsonValueKind.Number && arr[1].TryGetDouble(out var xv) ? xv : 0;
                    var y = arr[2].ValueKind == JsonValueKind.Number && arr[2].TryGetDouble(out var yv) ? yv : 0;
                    m[hh.Name] = new[] { ts, x, y };
                    total++;
                }
                if (m.Count > 0) parsed[w.Name] = m;
            }

            _ = this.framework.RunOnTick(() =>
            {
                try
                {
                    this.relayLive = parsed;
                    this.relayGenerated = gen;
                    this.lastRelayOk = DateTime.UtcNow;
                    this.relayStatus = $"看板已同步 {parsed.Count} 服 / {total} 条 · {DateTime.Now:HH:mm}";
                    // 拉到新快照立刻重算一次，不让用户等 30 秒节拍
                    this.ApplyRelayTriggered(null);
                }
                catch (Exception ex)
                {
                    this.log.Warning("[SrankFlag.Trigger] relay 写入失败：" + ex.Message);
                }
                finally { this.relayFetching = false; }
            });
        }
        catch (Exception ex)
        {
            this.relayFetching = false;
            this.relayStatus = "看板拉取异常：" + ex.Message;
            this.log.Warning("[SrankFlag.Trigger] 看板 relay 拉取失败：" + ex.Message);
        }
    }

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
    /// 【核心】把黑名单对齐到看板「已触发（当前在场）」，与 index.html 的 T() 逐条同口径：
    ///   relay 有记录 + 未超 20 小时 + 之后没有更新的击杀  ⇒ 在场 ⇒ 拉黑；
    ///   否则 ⇒ 不在场 ⇒ 解除主站侧的锁（Sonar 出生锁不动）。
    ///
    /// 这是「显示什么」的唯一来源：插件不再自己推算窗口，因此不可能再出现「全表拉黑」。
    /// justDied 是本轮刚确认死亡的组合，直接跳过（避免同一轮刚解锁又锁回）。
    /// </summary>
    private void ApplyRelayTriggered(HashSet<string>? justDied)
    {
        this.LoadTriggered();
        if (this.mainTriggerMode == MainTriggerMode.Disabled)
        {
            // 关掉看板源：把看板侧的锁一并放掉，只留 Sonar 出生锁。
            // 否则这些红标会永远留着（relay 正常时超时兜底不会生效）。
            this.ReleaseAllMainSide("看板源已关闭");
            return;
        }
        if (this.relayLive.Count == 0) return;   // 从未成功拉取 → 不动现有状态，避免把锁全放掉

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expect = new HashSet<string>(StringComparer.Ordinal);
        var expectTs = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var worldKv in this.relayLive)
        {
            var world = worldKv.Key;
            foreach (var huntKv in worldKv.Value)
            {
                var row = huntKv.Value;
                if (row is null || row.Length < 1 || row[0] <= 0) continue;
                if (now - row[0] > RelayMaxAgeSeconds) continue;                 // 看板：超 20h 不算在场
                var key = TrigKey(huntKv.Key, world);
                if (justDied is not null && justDied.Contains(key)) continue;     // 本轮刚死
                if (this.DeathOf(huntKv.Key, world) >= row[0] - RelayKillTolerance)
                    continue;                                                    // 看板：之后有击杀 = 已死
                expect.Add(key);
                expectTs[key] = row[0];
            }
        }

        var added = 0;
        foreach (var key in expect)
        {
            var seg = key.Split('|');
            if (seg.Length != 2) continue;
            if (this.MarkTriggered(seg[0], seg[1], this.DeathOf(seg[0], seg[1]), expectTs[key],
                    "看板显示已触发（当前在场）", SourceRelay))
                added++;
            // 每一轮都算「主站仍在确认」，否则超时兜底会把还在场的怪误放
            this.triggeredSeen[key] = now;
        }

        // 反向同步：看板已经不显示它了 ⇒ 放行（Sonar 出生锁例外，只认击杀/脱战）
        List<(string En, string World)>? drop = null;
        foreach (var key in this.triggeredMonsters.Keys.ToList())
        {
            if (expect.Contains(key)) continue;
            this.triggeredSource.TryGetValue(key, out var src);
            if (!IsMainSide(src)) continue;
            var seg = key.Split('|');
            if (seg.Length != 2) continue;
            (drop ??= new List<(string, string)>()).Add((seg[0], seg[1]));
        }
        var released = 0;
        if (drop is not null)
            foreach (var (en, world) in drop)
                if (this.ClearTriggered(en, world, "看板已不再显示该怪在场"))
                    released++;

        if (added > 0 || released > 0)
            this.log.Information(
                $"[SrankFlag.Trigger] 看板同步：新增 {added} 只 / 放行 {released} 只，" +
                $"当前共 {this.triggeredMonsters.Count} 只（看板在场 {expect.Count}）");
    }

    /// <summary>释放所有「主站侧」的锁，保留 Sonar 出生锁。用于关掉看板源时收尾。</summary>
    private void ReleaseAllMainSide(string reason)
    {
        var keys = this.triggeredMonsters.Keys.ToList();
        if (keys.Count == 0) return;
        var n = 0;
        foreach (var key in keys)
        {
            this.triggeredSource.TryGetValue(key, out var src);
            if (!IsMainSide(src)) continue;
            var seg = key.Split('|');
            if (seg.Length != 2) continue;
            if (this.ClearTriggered(seg[0], seg[1], reason)) n++;
        }
        if (n > 0) this.log.Information($"[SrankFlag.Trigger] {reason}：放行 {n} 只");
    }

    /// <summary>
    /// 超时兜底：**只在看板数据陈旧时生效**（看板挂了 / 网络断了）。
    /// 看板正常时每一轮同步都会刷新 triggeredSeen，这条永远不该解除任何条目。
    /// </summary>
    private void SweepStaleByTimeout()
    {
        if (this.triggeredMonsters.Count == 0) return;
        if (!this.RelayStale) return;
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
        this.log.Warning($"[SrankFlag.Trigger] 看板数据陈旧，超时兜底解锁 {drop.Count} 只");
        this.nativeHud?.ForceMainRefresh();
    }

    /// <summary>主循环里按 30s 节流调用：拉看板 relay + 主站陈旧超时兜底。</summary>
    private void TickTriggeredSweep()
    {
        // 首次启动立刻拉一次，不等 3 分钟节流
        if (this.lastRelayOk == DateTime.MinValue && this.lastRelayFetch == DateTime.MinValue)
            this.MaybeFetchRelay(true);

        if (DateTime.UtcNow - this.lastTriggeredSweepUtc < TriggeredSweepInterval) return;
        this.lastTriggeredSweepUtc = DateTime.UtcNow;
        // relay 快照未变时这里的 Apply 只是把 seen 刷新到 now（幂等，不写盘就不会有 IO）
        try { this.ApplyRelayTriggered(null); } catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 看板同步失败：" + ex.Message); }
        try { this.SweepStaleByTimeout(); } catch (Exception ex) { this.log.Warning("[SrankFlag.Trigger] 超时兜底失败：" + ex.Message); }
        this.MaybeFetchRelay(false);
    }

    // ───────────────────── /水桶 trigger：查看 / 开关 / 对账 / 清空 ─────────────────────

    private void TriggerCommand(string[] parts)
    {
        this.LoadTriggered();
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        // /水桶 trigger main on|off —— 看板源开关（auto / force / stuck 为旧命令，等价于 on）
        if (sub == "main" || sub == "stuck")
        {
            var arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            this.mainTriggerMode = arg == "off" ? MainTriggerMode.Disabled : MainTriggerMode.Enabled;
            this.SaveTriggered();
            var desc = this.mainTriggerMode == MainTriggerMode.Disabled
                ? "关闭：主站/看板不再参与判定，只认 Sonar 出生播报"
                : "开启：跟看板「已触发（当前在场）」完全一致（默认）";
            this.Print($"看板源 = {MainModeText(this.mainTriggerMode)}（{desc}）。" +
                       "3 分钟内自动生效（/水桶 trigger scan 可立刻对账）。");
            return;
        }

        // /水桶 trigger scan —— 与看板对账，只打印不写盘
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
            this.Print($"已清空「已被触发」黑名单（{n} 条）。" +
                       "注意：看板仍显示在场的 S 会在下一轮同步（≤3 分钟）重新标红 —— 以看板为准。");
            return;
        }

        this.Print($"— 已被他人触发的 S — 共 {this.triggeredMonsters.Count} 条" +
                   $"（看板 {MainModeText(this.mainTriggerMode)} · {this.relayStatus}）");
        if (this.triggeredMonsters.Count == 0)
        {
            this.Print("当前没有拉黑任何 S。/水桶 trigger scan 可与看板对账。");
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
    /// 与看板对账：按看板口径统计「在场 / 过期 / 已击杀」，并与本地黑名单逐条比对。
    /// 只打印，不写盘 —— 这是确认「插件显示 == 看板显示」的唯一手段。
    /// </summary>
    private void PrintTriggerScan()
    {
        this.Print($"— 看板「已触发（当前在场）」对账（{DateTime.Now:HH:mm:ss}，只读）—");
        this.Print($"  {this.relayStatus}" +
                   (this.relayGenerated > 0
                       ? $"，快照生成 {Math.Round((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.relayGenerated) / 60.0)} 分钟前"
                       : ""));

        if (this.relayLive.Count == 0)
        {
            this.Print("  还没有拉到看板数据（检查网络 / 站点是否在线）。");
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int total = 0, expired = 0, died = 0, live = 0;
            foreach (var worldKv in this.relayLive)
                foreach (var huntKv in worldKv.Value)
                {
                    var row = huntKv.Value;
                    if (row is null || row.Length < 1 || row[0] <= 0) continue;
                    total++;
                    if (now - row[0] > RelayMaxAgeSeconds) { expired++; continue; }
                    if (this.DeathOf(huntKv.Key, worldKv.Key) >= row[0] - RelayKillTolerance) { died++; continue; }
                    live++;
                }
            this.Print($"  relay 共 {total} 条 → 超 20h 过期 {expired} · 之后已击杀 {died} ⇒ 看板在场 {live} 只");
        }

        var bySrc = this.triggeredMonsters.Keys
            .GroupBy(k => SourceText(this.triggeredSource.TryGetValue(k, out var s) ? s : null))
            .ToDictionary(g => g.Key, g => g.Count());
        this.Print($"  本地黑名单 {this.triggeredMonsters.Count} 只" +
                   (bySrc.Count > 0
                       ? "（" + string.Join(" · ", bySrc.Select(kv => kv.Key + " " + kv.Value)) + "）"
                       : ""));

        // 逐条对账：看板在场却没锁 / 看板不在场却锁着（且非 Sonar）
        var expect = new HashSet<string>(StringComparer.Ordinal);
        var now2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var worldKv in this.relayLive)
            foreach (var huntKv in worldKv.Value)
            {
                var row = huntKv.Value;
                if (row is null || row.Length < 1 || row[0] <= 0) continue;
                if (now2 - row[0] > RelayMaxAgeSeconds) continue;
                if (this.DeathOf(huntKv.Key, worldKv.Key) >= row[0] - RelayKillTolerance) continue;
                expect.Add(TrigKey(huntKv.Key, worldKv.Key));
            }

        var missing = expect.Where(k => !this.triggeredMonsters.ContainsKey(k)).ToList();
        var extra = this.triggeredMonsters.Keys
            .Where(k => !expect.Contains(k) &&
                        IsMainSide(this.triggeredSource.TryGetValue(k, out var s) ? s : null))
            .ToList();
        this.Print($"  对账：看板在场但未锁 {missing.Count} 只 · 看板不在场却锁着 {extra.Count} 只" +
                   (missing.Count == 0 && extra.Count == 0 ? "（完全一致）" : ""));
        foreach (var k in missing.Take(3)) this.Print("   · 待锁 " + k);
        foreach (var k in extra.Take(3)) this.Print("   · 待放 " + k);
    }
}
