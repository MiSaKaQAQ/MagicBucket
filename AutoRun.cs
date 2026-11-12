using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;

namespace SrankFlag;

/// <summary>
/// 「自动前往」悬浮窗 + 排队跑点。
/// 勾选服务器与 S 怪 → 按「服 × 怪」排队，逐个跨服 / 传大水晶 / 巡绿点 / 触发；
/// 聊天框出现「感受到了强大的恶名精英的气息……」时立刻全部停下并提示查看。
/// </summary>
public sealed partial class Plugin
{
    // ───────────── 悬浮窗状态 ─────────────
    private bool autoRunOpen;
    /// <summary>「正在触发什么」小悬浮窗。开始自动前往时自动弹出，可以自己关掉。</summary>
    private bool autoHudOpen;
    /// <summary>队列暂停中：不跑监控链、不动作，但队列和进度都留着，随时可以点「继续队列」。</summary>
    private bool autoPaused;
    private string autoPauseReason = "";
    private int autoStyle = 1;   // 1=分组　2=卡片
    private bool autoOnlyOpen = true;   // 默认只显示已进闸（可触发窗口内）的 S
    private bool autoAllWorlds = true;  // 默认用全部服务器，不用逐个勾；列表里标出进闸的服
    private readonly HashSet<string> autoWorlds = new(StringComparer.Ordinal);
    private readonly HashSet<string> autoHunts = new(StringComparer.Ordinal);
    private readonly HashSet<string> autoGroupsClosed = new(StringComparer.Ordinal);
    /// <summary>v1.1.72 原生「自动前往」窗口的 S 怪列表页码（ImGui 版不用）。</summary>
    private int autoNativePage;

    /// <summary>自动前往只跑这些地图，其余地图的 S 一律不列出、也不派任务（用户指定）。</summary>
    private static readonly string[] AutoMapNames =
    {
        "克扎玛乌卡湿地", "亚克特尔树海", "遗产之地", "活着的记忆", "厄尔庇斯",
        "加雷马", "迷津", "安穆·艾兰", "雷克兰德", "珂露西亚岛", "黑风海",
    };

    private static readonly HashSet<string> AutoMaps = new(AutoMapNames, StringComparer.Ordinal);

    /// <summary>这只怪的地图是否在自动前往范围内。</summary>
    private static bool AutoMapOk(HuntData x) => AutoMaps.Contains(x.map);

    // ───────────── 维护感知状态（与 UI 进闸判定完全对齐） ─────────────

    /// <summary>与主界面 BuildMainSnapshot 完全一致的「维护感知」状态计算：
    /// 开闸 = max(lastDeath, restartAt) + cd；当全局重启时间 T 主导（restartAt &gt; lastDeath）时，
    /// 改用维护再生 mt[0]/mt[1] 覆盖 cd/win。这样主界面判为「进闸」的，自动前往也一定能纳入队列。</summary>
    private (string State, double Eta, double Pct) CalcState(HuntData h, double lastDeath, long now)
    {
        if (lastDeath <= 0) return ("unknown", 0, 0);
        var eff = Math.Max(lastDeath, this.restartAt);
        var maint = this.restartAt > 0 && this.restartAt > lastDeath;
        var useCd = maint && h.mt.Length >= 1 ? h.mt[0] : h.cd;
        var useWin = maint && h.mt.Length >= 2 ? h.mt[1] : h.win;
        return Calc(eff, now, useCd, useWin);
    }

    // ───────────── 队列状态 ─────────────
    private sealed record AutoJob(string World, string HuntCn, string HuntEn, string Map);

    private readonly List<AutoJob> autoQueue = new();
    private int autoIdx;
    private bool autoRunning;
    private int autoGen;
    private int autoToken;              // 单个任务的令牌：换任务/停止后旧监控链自动失效
    private DateTime autoJobStart;      // 本次任务开始时间（按墙上时间算 5 分钟超时）
    private string autoStatus = "未开始：勾选服务器和 S 后点「开始自动前往」";
    private string autoCurrent = "";
    private string autoHit = "";
    private float autoDist = -1f;
    private int autoTotal;
    private DateTime autoGatherDoneAt;   // 炫学采满上限 / 收工的时刻，用来判断「采完了」
    private DateTime autoWaitBeganAt;    // 本任务「到点开始等触发」的时刻（顾尼图用来判断流程有没有起来）

    // ── 触发动作进度：走完绿点 / 丢完道具 / 采集完 → 只再等 5 秒提示 ──
    private DateTime autoRouteDoneAt;    // 走完所有绿点的时刻（StartGreenRoute 结束时写）
    private bool autoHasRoute;           // 本任务有没有绿点要巡（有就必须走完才算动作做完）
    private DateTime autoDiscardDoneAt;  // 丢完触发道具的时刻
    private DateTime autoLastLogAt;      // 进度日志节流（30 秒一条，不再刷屏）
    private DateTime autoRouteStartedAt; // StartGreenRoute 真正被调起来的时刻（用来判断动作链有没有断）
    private bool autoRetriedRoute;       // 本任务已经补排过一次点位动作
    private List<Pt> autoJobGreens = new();  // 本任务的点位（补排动作链用）
    private bool autoPrepFailed;        // 切职业两次都没成功 → 判这只触发失败，去下一只

    // ── 跨服 / 传送：卡住就重发，超时就跳过这只，不再干等 5 分钟 ──
    private DateTime autoTravelAt;       // 最近一次发跨服 / 发传送的时刻
    private int autoTravelRetry;         // 这次任务里重发过几次
    private const int AutoTravelRetries = 2;
    private const double AutoCrossFailSeconds = 90;    // 跨服最多等 90 秒（DR 跨界传送实测约 50 秒，45 秒会误判）
    private const double AutoTeleFailSeconds = 90;     // 传送最多等 90 秒
    /// <summary>触发动作（走完绿点 / 丢完道具 / 采集完 / 落地丢完）做完之后，只再等这么多秒的刷新提示。</summary>
    private const int TriggerGraceSeconds = 5;

    /// <summary>正在跑的这个任务（命中刷新提示时用来记「哪只 / 哪个服成功」）。</summary>
    private AutoJob? autoJob;
    private DateTime autoHitHandledAt;   // 同一条提示只处理一次（聊天钩子和调度之间有时间差）

    /// <summary>本次运行里已经触发成功的组合，收工后一起报给用户。</summary>
    private readonly List<(string world, string cn)> autoHits = new();

    /// <summary>本次运行里被跳过的组合（怪名 + 原因分类 + 补充说明），收工后汇总报给用户。</summary>
    private readonly List<(string cn, string kind, string detail)> autoSkipped = new();

    /// <summary>聊天框里出现 S 怪刷新提示时命中（在主线程外触发，只写字段，停队回主线程做）。</summary>
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
    {
        try
        {
            var text = msg.Message.TextValue;
            if (string.IsNullOrEmpty(text)) return;
            if (text.IndexOf("感受到了", StringComparison.Ordinal) < 0) return;
            if (text.IndexOf("气息", StringComparison.Ordinal) < 0 &&
                text.IndexOf("恶名", StringComparison.Ordinal) < 0) return;

            this.autoHit = text.Trim();
            if (!this.autoRunning) return;
            var gen = this.autoGen;
            this.Sched(() =>
            {
                if (gen != this.autoGen || !this.autoRunning) return;
                this.AutoHandleHit();
            }, TimeSpan.Zero);
        }
        catch { /* 聊天解析永不影响游戏 */ }
    }

    /// <summary>聊天里出现刷新提示：记下这只成功，然后「暂停队列」——不是把整队停掉。
    /// 顺手把同一只怪在别的服上的剩余任务清掉；想接着跑，在悬浮窗里点「继续队列」。</summary>
    private void AutoHandleHit()
    {
        if (this.autoHit.Length == 0) return;
        if ((DateTime.UtcNow - this.autoHitHandledAt).TotalSeconds < 3) return;
        this.autoHitHandledAt = DateTime.UtcNow;

        var job = this.autoJob;
        var label = job is null ? this.autoCurrent : $"{job.World} · {job.HuntCn}";
        if (job is not null && !this.autoHits.Contains((job.World, job.HuntCn)))
            this.autoHits.Add((job.World, job.HuntCn));

        this.Print($"✅ [自动前往] 检测到「{this.autoHit}」");
        this.Print($"✅ {label} 触发成功！");

        // 只删「还没跑」的部分（索引 >= autoIdx）。autoIdx 是队列游标，
        // 用 RemoveAll 会把已经跑过的同名项也删掉，游标就错位了。
        if (job is not null)
        {
            var rest = 0;
            for (var i = this.autoQueue.Count - 1; i >= this.autoIdx; i--)
            {
                if (this.autoQueue[i].HuntCn != job.HuntCn) continue;
                this.autoQueue.RemoveAt(i);
                rest++;
            }
            if (rest > 0) this.Print($"[自动前往] 剩下 {rest} 个「{job.HuntCn}」的其它服任务跳过（已经触发到了）");
        }

        this.autoHit = "";   // 清掉命中文本，顶部改成「已触发 N 只」的进度条
        this.AutoPause($"✅ {label} 触发成功");
    }

    // ───────────── 队列执行 ─────────────

    private void AutoStart()
    {
        var wasRunning = this.autoRunning;
        this.autoRunning = false;   // 重复点「开始」时先把上一轮的监控链作废，避免两条链同时往队列里走
        this.autoToken++;
        this.autoPaused = false;
        this.autoPauseReason = "";
        this.autoHudOpen = true;    // 开跑就把「正在触发什么」的小窗亮出来
        if (wasRunning) { try { this.StopAll(true); } catch { } }   // 顺手把上一轮的寻路/传送也取消掉
        if (this.srankData?.data is not { } data)
        {
            this.Print("[自动前往] 数据还没加载好，稍后再试");
            return;
        }

        var hunts = data.hunts.Where(h => this.autoHunts.Contains(h.cn) && AutoMapOk(h)).ToList();
        var allWorlds = data.worlds.Where(w => w.Length > 0).ToList();
        var worlds = this.autoAllWorlds
            ? allWorlds
            : allWorlds.Where(w => this.autoWorlds.Contains(w)).ToList();
        if (hunts.Count == 0 || worlds.Count == 0)
        {
            this.Print("[自动前往] 请至少勾选一个服务器和一只 S");
            return;
        }

        // 启用前提示应当开好的插件（到了现场才缺东西就晚了）
        this.Print("[自动前往] 启用前请确认已开启以下插件：");
        this.Print("  · vnavmesh-cn（寻路 / flyflag / movetarget）— " +
                   (this.VnavReady() ? "已检测到 ✓" : "未检测到 ✗ 请先启用"));
        this.Print("  · DailyRoutines · 跨界传送模块（跨服依赖 /pdr worldtravel）");
        this.Print("  · GatherBuddyReborn（采集类 S 需要：采集清单里配好矮人棉、数量 999）");

        this.autoQueue.Clear();
        this.autoSkipped.Clear();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 先按条件窗口 / 开闸把怪筛一遍，再排服
        var candidates = new List<HuntData>();
        var openAt = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var h in hunts)
        {
            // 条件窗口（天气连续 / ET 时段 / 月相）：没开就整只跳过——到了现场也触发不出来
            var cw = ConditionForecast.HasSpec(h.en) ? ConditionForecast.NextWindow(h.en, now) : null;
            var condOpen = cw is not { } cw0 || (cw0.Start <= now && now < cw0.End);
            if (!condOpen)
            {
                this.autoSkipped.Add((h.cn, "条件窗口没到",
                    ConditionForecast.LabelOf(h.en) + "｜" + ConditionForecast.Describe(h.en, now)));
                continue;
            }

            if (this.autoOnlyOpen)
            {
                // 只看开闸：整只怪只要有一个服进闸就去，一个都没有才记一次「没跑成」——
                // 全部服务器模式下 28 个服逐个记一遍会把汇总刷成几十条
                var open = new HashSet<string>(worlds.Where(w => this.IsOpenAt(h, w, now)), StringComparer.Ordinal);
                if (open.Count == 0)
                {
                    this.autoSkipped.Add((h.cn, "没进闸", ""));
                    continue;
                }
                openAt[h.cn] = open;
            }
            candidates.Add(h);
        }

        // 服在外层、且当前所在服排最前：先把本服能触的全撞一遍，再依次换服，少来回跨服
        var curWorld = this.CurrentWorldName();
        var order = worlds
            .OrderBy(w => string.Equals(w, curWorld, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        foreach (var w in order)
        {
            foreach (var h in candidates)
            {
                if (this.autoOnlyOpen && !openAt[h.cn].Contains(w)) continue;
                this.autoQueue.Add(new AutoJob(w, h.cn, h.en, h.map));
            }
        }

        if (this.autoQueue.Count == 0)
        {
            this.Print("[自动前往] 勾选的 S 一个都去不了：");
            this.PrintSkipReport(true);
            this.autoStatus = "没有可去的 S（原因见聊天框）";
            return;
        }

        this.Print($"[自动前往] 出发：{this.autoQueue.Count} 个任务" +
                   (this.autoSkipped.Count > 0 ? $"，另有 {this.autoSkipped.Count} 只跳过（收工汇总）" : ""));

        // 先把本服撞完再换服，顺序打进日志方便对照
        var perWorld = order
            .Select(w => (w, n: this.autoQueue.Count(j => j.World == w)))
            .Where(x => x.n > 0)
            .ToList();
        if (perWorld.Count > 0)
        {
            var show = perWorld.Take(6).Select(x => $"{x.w}({x.n})");
            this.Print("[自动前往] 顺序：" + string.Join(" → ", show) +
                       (perWorld.Count > 6 ? $" → …共 {perWorld.Count} 个服" : ""));
        }

        this.autoIdx = 0;
        this.autoTotal = this.autoQueue.Count;
        this.autoRunning = true;
        this.autoGen++;
        this.autoHit = "";
        this.autoJob = null;
        this.autoHits.Clear();
        this.autoHitHandledAt = default;
        this.autoStatus = "准备出发…";
        this.AutoStep();
    }

    /// <summary>跳过汇总。detail=false 只打一行分类计数，true 打出每类下面具体是哪些 S。</summary>
    private void PrintSkipReport(bool detail)
    {
        if (this.autoSkipped.Count == 0) return;
        var groups = this.autoSkipped.GroupBy(s => s.kind).ToList();

        if (!detail)
        {
            var parts = groups.Select(g => $"{g.Key} {g.Select(x => x.cn).Distinct().Count()}");
            this.Print($"[自动前往] 没跑成 {this.DistinctSkipped()} 只：{string.Join(" · ", parts)}");
            return;
        }

        this.Print($"[自动前往] 本次没跑成的 {this.DistinctSkipped()} 只：");
        foreach (var g in groups)
        {
            var names = g.GroupBy(s => s.cn).Select(n =>
            {
                var d = n.Select(x => x.detail).FirstOrDefault(x => x.Length > 0);
                return d is { Length: > 0 } ? $"{n.Key}（{d}）" : n.Key;
            });
            this.Print($"  · {g.Key}：{string.Join("、", names)}");
        }
    }

    /// <summary>跳过清单里去重后的怪数（同一只怪可能在几个服各失败一次）。</summary>
    private int DistinctSkipped() => this.autoSkipped.Select(s => s.cn).Distinct().Count();

    /// <summary>某只怪在某个服上是否正处于开闸窗口。</summary>
    private bool IsOpenAt(HuntData h, string world, long now)
    {
        foreach (var w in h.worlds)
        {
            if (!string.Equals(w.world, world, StringComparison.Ordinal) || w.lastDeath <= 0) continue;
            return CalcState(h, w.lastDeath, now).State == "open";
        }
        return false;
    }

    /// <summary>这只怪当前进闸的服（全部服务器模式下用来告诉用户该去哪个服）。</summary>
    private List<string> OpenWorldsOf(HuntData h, long now)
    {
        var list = new List<string>();
        foreach (var w in h.worlds)
        {
            if (w.lastDeath <= 0) continue;
            if (CalcState(h, w.lastDeath, now).State == "open") list.Add(w.world);
        }
        return list;
    }

    /// <summary>把进闸的服压成一小段：一两个全写，多了写「首服 +N」，全量在悬浮提示里。</summary>
    private static string WorldsHint(List<string> ws)
    {
        if (ws.Count == 0) return "";
        if (ws.Count <= 2) return string.Join("、", ws);
        return $"{ws[0]} +{ws.Count - 1}";
    }

    /// <summary>到点就立刻做动作的 S（自爆 / 放技能）：进入点位 30 亚尔就停寻路、下坐骑动手，
    /// 不必等 vnav 精确停稳。绿点不再只取一个——有多个就按最近邻一个一个接着试，
    /// 直到出刷新提示或全部试完；出本清「意志薄弱」后也会立刻回点位接着自爆。</summary>
    private static readonly HashSet<string> QuickActionHunts = new(StringComparer.Ordinal)
    {
        "多智兽", "阿姆斯特朗", "天气预报机器人",
    };

    private void AutoStep()
    {
        if (!this.autoRunning) return;
        if (this.autoIdx >= this.autoQueue.Count)
        {
            this.AutoFinish();
            return;
        }

        var job = this.autoQueue[this.autoIdx++];
        this.autoJob = job;
        this.autoHit = "";   // 换任务时清掉上一次的命中文本，免得监控链看到旧文本就一直空转
        this.autoCurrent = $"{job.World} · {job.HuntCn}";
        this.autoStatus = $"前往 {this.autoCurrent}（{this.autoIdx}/{this.autoTotal}）";
        this.autoDist = -1f;
        this.Print($"[自动前往] ({this.autoIdx}/{this.autoTotal}) {job.World} · {job.HuntCn}（{job.Map}）");

        // 换任务：上一只留下的东西全部作废——寻路、采集开关、顾尼图回调、牛头黑神自爆循环。
        // 不收的话会出现「已经飞到下一张图了，上一只还在自爆 / 还在采」这种串台。
        this.autoToken++;
        this.gounituDone = null;
        this.gounituFinishedAt = default;
        this.gounituFlowOk = false;
        this.gounituStartedAt = default;
        this.autoWaitBeganAt = default;
        this.aetheryteBombLimit = 0;
        this.autoGatherDoneAt = default;
        this.autoRouteDoneAt = default;
        this.autoRouteStartedAt = default;
        this.autoRetriedRoute = false;
        this.autoPrepFailed = false;
        this.autoJobGreens = new List<Pt>();
        this.autoDiscardDoneAt = default;
        this.autoLastLogAt = default;
        this.autoTravelAt = default;
        this.autoTravelRetry = 0;
        try { this.StopAll(true); } catch { }
        // 炫学改回 GatherBuddy 自动采集（v1.1.70）：换任务时同样先关掉上一只的自动采集，
        // 免得已经飞到下一张图了 GBR 还在原地挖。
        try { this.GatherBuddySetAuto(false); } catch { }

        var walk = PointHunts.Contains(job.HuntCn) && !NoPointHunts.Contains(job.HuntCn);
        var greens = new List<Pt>();
        if (job.HuntCn == "顾尼图")
        {
            // 顾尼图挂在 NoPointHunts 里（排点数据对它没意义），但触发其实有固定点位：
            // 不飞过去就只会站在大水晶旁边干等，所以自动前往自己补这一个点。
            greens.Add(GounituPt);
        }
        else if (walk)
        {
            var live = this.LiveGreens(job.HuntEn, job.World);
            var local = HuntPoints(this.srankData?.points.GetValueOrDefault(job.HuntEn), job.World);
            var localGreens = local.Where(q => q.S == 'g').ToList();

            // 实时排点返回「空列表」（这一轮点全被踩过）时也要回落到本地点位表。
            // 以前用 ?? 只在 null 时回落，空列表会让 greens.Count == 0 →
            // AutoDepart 走「只传大水晶」分支，多智兽/阿姆斯特朗就再也不会去点位自爆。
            greens = live is { Count: > 0 } ? live : localGreens;

            // 这些怪的触发和「这个点是不是绿点」无关（自爆 / 放狂风暴雪），
            // 本地表里随便挑一个非 B 怪点也要去，保证动作一定做得上。
            if (greens.Count == 0)
                greens = local.Where(q => q.S != 'b').Take(1).ToList();

            // 绿点全留着：S 刷在哪个候选点是随机的，这个点自爆没触发就得换下一个点再试。
            // StartGreenRoute 本来就按「离当前位置最近」逐个走，这里不再截成 1 个。
        }

        this.autoJobGreens = greens;
        var hint = AutoTriggerHint(job.HuntCn, job.Map);
        if (hint.Length > 0) this.Print($"[自动前往] {job.HuntCn} 的触发方式：{hint}");
        this.Print($"[自动前往] {job.HuntCn} 取到 {greens.Count} 个触发点" +
                   (greens.Count == 0 ? "（没有点位，只传大水晶，不会做点位动作）"
                    : greens.Count > 1 ? "（会一个一个接着试，直到刷出来或全部试完）" : ""));
        if (job.HuntCn == "牛头黑神") this.aetheryteBombLimit = AutoBombTries;
        this.autoHasRoute = greens.Count > 0;

        // 顾尼图：流程（诱钓 3 只 → 挑衅 → 等巨大化/最大化/口锥）结束时只写一个时间戳，
        // 由 AutoWaitTrigger 统一处理后续：再等 20 秒看聊天有没有刷新提示，没有就换下一只。
        // 之前这里是「判到受击就把整队停掉」，但那个判定只是「玩家掉了点血」，
        // 被别的怪摸一下也会中，于是经常误以为触发成功、整条队列停住，看着像卡死。

        // 上一只刚打完 / 被口锥摸过时人还在战斗状态：跨服和传送都会被游戏挡回来，
        // 于是「换下一只」看着像没动。先等脱战（最多 20 秒），等不到也强行出发并留日志。
        if (this.AutoInCombat())
        {
            this.Resume();   // StopAll 立过 halted，Sched 会被拦住，等脱战前要先解掉
            this.Print("[自动前往] 还在战斗状态，先等脱战再出发（最多 20 秒）");
            this.WaitUntilOr(() => !this.AutoInCombat(),
                () => this.AutoDepart(job, greens),
                () => this.AutoDepart(job, greens),
                40, 500);
            return;
        }

        this.AutoDepart(job, greens);
    }

    /// <summary>出发去这一只：插旗寻路（或传大水晶）并开始监控。
    /// 单独抽出来是为了「等脱战」之后能延后调用，不影响后面的监控链。</summary>
    private void AutoDepart(AutoJob job, List<Pt> greens)
    {
        try
        {
            if (greens.Count > 0)
            {
                var g0 = greens[0];
                var r = this.Goto(job.Map, g0.X, g0.Y, job.World, this.HuntLineOverride(job.HuntEn, job.World), job.HuntCn, true, greens);
                if (!r.Ok) this.Print("[自动前往] 前往失败：" + r.Message);
            }
            else
            {
                // 不踩点的怪（触发和点位无关）：传最近大水晶等刷新就够
                var r = this.GotoAetheryte(job.Map, job.World, job.HuntCn);
                if (!r.Ok) this.Print("[自动前往] 前往大水晶失败：" + r.Message);
            }
        }
        catch (Exception ex)
        {
            this.Print("[自动前往] 出发出错：" + ex.Message);
        }

        this.autoJobStart = DateTime.UtcNow;
        this.autoTravelAt = this.autoJobStart;   // 跨服 / 传送的计时起点
        this.autoTravelRetry = 0;
        this.autoLastLogAt = this.autoJobStart;
        this.AutoMonitor(job, greens);
    }

    /// <summary>现在是不是还在战斗状态（读不到就当不在，免得整条队列卡住）。</summary>
    private bool AutoInCombat()
    {
        try { return this.condition[ConditionFlag.InCombat]; } catch { return false; }
    }

    /// <summary>监控「到点」：最多 5 分钟飞到绿点（或进图），到了再进入触发等待（时长按怪分）。
    /// 用「本次任务令牌 + 墙上时间」判断，旧的监控链不会和新排队互相踩。
    /// 跨服 / 传送单独计时：45 秒没跨过去、90 秒没传到位就跳过这只——
    /// 以前这里只看「在不在图里」，跨服没成会一直显示「过图中」，白等满 5 分钟。</summary>
    private void AutoMonitor(AutoJob job, List<Pt> greens)
    {
        var token = this.autoToken;
        this.Sched(() =>
        {
            if (token != this.autoToken || !this.autoRunning) return;
            if (this.autoHit.Length > 0) return;   // 命中刷新提示，AutoPause 接管
            if (this.AutoPrepFailedStep(job)) return;
            this.AutoMaybeRetryRoute(job, greens);

            var now = DateTime.UtcNow;
            var elapsed = (now - this.autoJobStart).TotalSeconds;
            bool inWorld, inMap;
            try
            {
                // 服务器闸门：过图中 CurrentWorldName 可能已变成目标服，必须同时排除 BetweenAreas
                inWorld = this.WorldReady(job.World);
                inMap = this.InMap(job.Map);
            }
            catch { inWorld = true; inMap = false; }

            try
            {
                if (greens.Count > 0 && inWorld && this.TryResolveZone(job.Map, out _, out var mid, out _))
                {
                    var d = this.YalmsTo(mid, greens[0].X, greens[0].Y);
                    this.autoDist = d;
                    if (d <= 40f)
                    {
                        this.AutoBeginWait(job, token);
                        return;
                    }
                }
                else if (greens.Count == 0 && inWorld && inMap)
                {
                    this.autoDist = 0f;
                    this.AutoBeginWait(job, token);
                    return;
                }
            }
            catch { }

            // ── 还没到：先分清是「跨服没成」「传送没成」还是「正常在飞」 ──
            if (!inWorld || !inMap)
            {
                var stage = !inWorld ? "跨服" : "传送";
                var limit = !inWorld ? AutoCrossFailSeconds : AutoTeleFailSeconds;
                var wait = (now - this.autoTravelAt).TotalSeconds;

                if (wait >= limit)
                {
                    this.Print($"[自动前往] {job.World} · {job.HuntCn} {stage} {limit:F0} 秒没成功，跳过这只" +
                               (stage == "跨服" ? "（请确认 DailyRoutines 的「跨界传送指令」模块已启用）" : ""));
                    this.autoSkipped.Add((job.HuntCn, stage + "失败", $"{stage} {limit:F0} 秒没成功（{job.World}）"));
                    this.AutoStep();
                    return;
                }

                // 发出后一直没动静 → 重发同一条 /pdr worldtravel（最多 2 次，别太急，DR 自己也要走流程）
                if (wait >= 20.0 * (this.autoTravelRetry + 1) && this.autoTravelRetry < AutoTravelRetries)
                {
                    this.autoTravelRetry++;
                    this.autoTravelAt = now;
                    this.Print($"[自动前往] {stage}没动静（已经 {wait:F0} 秒），重发一次（第 {this.autoTravelRetry} 次）");
                    if (!inWorld) this.SendWorldTravel(job.World);
                    else this.AutoRetryTeleport(job, greens);
                }

                this.autoStatus = !inWorld
                    ? $"{job.World} · {job.HuntCn} 跨服中（→ {job.World}，{wait:F0}s）"
                    : $"{job.World} · {job.HuntCn} 传送中（{job.Map}，{wait:F0}s）";
            }
            else
            {
                this.autoStatus = this.autoDist > 0
                    ? $"{job.World} · {job.HuntCn} 飞行中（还差 {this.autoDist:F0} 亚尔）"
                    : $"{job.World} · {job.HuntCn} 在图上，定位中…";
            }

            var timeout = !inWorld ? AutoCrossFailSeconds : 300;
            if (elapsed >= timeout)
            {
                this.Print($"[自动前往] {job.World} · {job.HuntCn} {timeout:F0} 秒没到点，跳过");
                this.autoSkipped.Add((job.HuntCn, "没飞到点", $"{timeout:F0} 秒没到（{job.World}）"));
                this.AutoStep();
                return;
            }

            if ((now - this.autoLastLogAt).TotalSeconds >= 30)
            {
                this.autoLastLogAt = now;
                this.Print($"[自动前往] {this.autoStatus}（{this.autoIdx}/{this.autoTotal}）");
            }
            this.AutoMonitor(job, greens);
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>切职业失败：直接判这只没触发，换下一只（用户要求：切换失败就算触发失败）。
    /// 监控链 / 等待链每轮都查一次，所以不管当时是在飞行还是已经到点，都能立刻收掉这只。</summary>
    private bool AutoPrepFailedStep(AutoJob job)
    {
        if (!this.autoPrepFailed) return false;
        this.autoPrepFailed = false;
        this.Print($"[自动前往] {job.World} · {job.HuntCn} 切换职业失败，判定触发失败，去下一只");
        this.autoSkipped.Add((job.HuntCn, "换装失败", $"切职业没成功（{job.World}）"));
        this.AutoStep();
        return true;
    }

    /// <summary>自爆 / 技能型怪出发 30 秒还没进入「巡绿点」就补排一次动作链。
    /// 最常见的原因是换职业那一步把回调丢了（旧版 SwitchJobThen 的 WaitUntil 超时静默 return），
    /// 表现就是「传到大水晶、切了青魔，然后站着不动，不会自己去点位自爆」。
    /// 只在 StartGreenRoute 压根没被调用过时补，正常流程不会重复巡点/重复自爆。</summary>
    private void AutoMaybeRetryRoute(AutoJob job, List<Pt> greens)
    {
        if (this.autoRetriedRoute || this.autoRouteStartedAt != default) return;
        if (!QuickActionHunts.Contains(job.HuntCn) || greens.Count == 0) return;
        if (this.autoJobStart == default ||
            (DateTime.UtcNow - this.autoJobStart).TotalSeconds < 30) return;
        try { if (!this.InMap(job.Map)) return; } catch { return; }

        this.autoRetriedRoute = true;
        this.Print($"[自动前往] {job.World} · {job.HuntCn} 出发 30 秒还没开始巡点/自爆，" +
                   "重新排一次点位动作（多半是换装或上坐骑那一步被卡住）");
        this.Resume();
        this.StartGreenRoute(job.Map, greens, job.HuntCn);
    }

    /// <summary>传送卡住时补发一次：已经在目标服里，只重发「传最近大水晶」。</summary>
    private void AutoRetryTeleport(AutoJob job, List<Pt> greens)
    {
        try
        {
            var x = greens.Count > 0 ? greens[0].X : 0f;
            var y = greens.Count > 0 ? greens[0].Y : 0f;
            var ae = this.NearestAetheryte(job.Map, x, y, job.HuntCn);
            if (ae is { } a) this.Say("/pdrtelepo " + a.Name);
        }
        catch { /* 找不到水晶就算了，等超时跳过 */ }
    }

    /// <summary>到点 / 进图后：要丢道具的图先丢一组（这就是触发动作本身），然后等刷新提示。</summary>
    private void AutoBeginWait(AutoJob job, int token)
    {
        if (token != this.autoToken) return;
        this.autoWaitBeganAt = DateTime.UtcNow;
        var needDiscard = DiscardMaps.ContainsKey(job.Map);
        if (needDiscard) this.Print($"[自动前往] {job.HuntCn} 的触发要「丢道具」，落地 3 秒后自动丢");
        this.autoStatus = needDiscard
            ? $"{job.World} · {job.HuntCn} 已到图，丢弃中…"
            : $"{job.World} · {job.HuntCn} 已到点，等触发…";
        if (needDiscard) this.Sched(() => this.AutoDiscard(job, token), TimeSpan.FromSeconds(3));
        this.AutoWaitTrigger(job, AutoWaitSeconds(job.HuntCn), token);
    }

    /// <summary>顾尼图的任务推进。返回 true = 已经换下一只（本帧不用再往下走）。
    /// 两条收尾规则，保证这只怪永远不会把整条队列卡死：
    /// ① 流程走完（成功或超时）再给 20 秒等聊天提示，没提示就换下一只；
    /// ② 到点 60 秒流程还没起来（卡在过图 / 下坐骑 / 没走到点），直接换下一只。</summary>
    private bool AutoGounituTick(AutoJob job, int left)
    {
        if (this.gounituFinishedAt != default)
        {
            var since = (DateTime.UtcNow - this.gounituFinishedAt).TotalSeconds;
            if (since < 20)
            {
                this.autoStatus = $"{job.World} · {job.HuntCn} 流程已走完，等刷新提示（{20 - (int)since}s）";
                return false;
            }

            this.Print($"[自动前往] {job.World} · {job.HuntCn} 触发流程走完，{since:F0} 秒没出刷新提示，下一只");
            this.autoSkipped.Add((job.HuntCn, "没触发出来",
                (this.gounituFlowOk ? "判到受击但没出怪" : "流程超时") + $"（{job.World}）"));
            this.AutoStep();
            return true;
        }

        if (this.gounituStartedAt == default && this.autoWaitBeganAt != default &&
            (DateTime.UtcNow - this.autoWaitBeganAt).TotalSeconds >= 60)
        {
            this.Print($"[自动前往] {job.World} · {job.HuntCn} 已到点 60 秒，触发流程还没起来" +
                       "（多半卡在过图 / 下坐骑 / 没走到固定点），下一只");
            this.autoSkipped.Add((job.HuntCn, "没触发出来", $"到点 60 秒流程未启动（{job.World}）"));
            this.AutoStep();
            return true;
        }

        this.autoStatus = this.gounituStartedAt == default
            ? $"{job.World} · {job.HuntCn} 已到点，等落地开始触发…"
            : $"{job.World} · {job.HuntCn} 触发流程进行中…（兜底 {left}s）";
        return false;
    }

    /// <summary>到点 / 进图后最多等多久（秒）。触发流程越长给得越宽：自爆要死一次再复活、
    /// 顾尼图要等两段 buff、阿提卡斯要看 Artisan 做菜、炫学交给 GBR 自动采最长 30 分钟。
    /// 实际推进通常靠聊天提示或流程回调，这个只是兜底上限。</summary>
    private int AutoWaitSeconds(string huntCn)
    {
        var baseSec = huntCn switch
        {
            "顾尼图" => 300,
            // 自爆完还要「倒地 → 返回 → 复活 → 进退副本清意志薄弱」才回到 Step，
            // WaitDutyResetThen 本身最多 8 分钟；240 秒会在收尾途中提前判负换下一只。
            "多智兽" or "阿姆斯特朗" or "牛头黑神" => 480,
            "先驱勇士 阿提卡斯" => 200,
            "天气预报机器人" => 90,   // 狂风暴雪几秒放完，天气一变 S 立刻刷；不再白等两分多钟
            "得到宽恕的炫学" => 1900,   // GBR 自动采集上限 300 次，兜底窗口要够大
            _ => 100,
        };
        // 多个绿点要一个一个接着自爆，每个点都有一整套「死亡 → 复活 → 进退副本」收尾。
        // 按点多给时间，别让总倒计时在半路把还在巡点的任务判负换下一只。
        if (huntCn == "多智兽" || huntCn == "阿姆斯特朗")
            baseSec += Math.Max(0, this.autoJobGreens.Count - 1) * 90;
        return baseSec;
    }

    /// <summary>牛头黑神在大水晶最多自爆几次（概率触发，爆几次不出就换下一只）。</summary>
    private const int AutoBombTries = 4;

    /// <summary>自动前往在这只怪身上会做的触发动作（写进日志，一眼能对上是漏了还是没成功）。</summary>
    private static string AutoTriggerHint(string huntCn, string map) => huntCn switch
    {
        "顾尼图" => "飞到固定点 (29.8, 3.9) → 诱钓 3 只深海水蛭 → 挑衅冰海天使 → 等巨大化/最大化/口锥",
        "多智兽" or "阿姆斯特朗" => "到绿点下坐骑 → 即刻咏唱 + 自爆（阿姆斯特朗要先穿帝国套）",
        "牛头黑神" => $"传大水晶后自爆，最多 {AutoBombTries} 次",
        "天气预报机器人" => "到绿点下坐骑 → 放狂风暴雪",
        "先驱勇士 阿提卡斯" => "落地切烹调师 → Artisan 做 HQ 犎牛牛排",
        "得到宽恕的炫学" => "落地切园艺工 → 调用 GatherBuddy Reborn 自动采矮人棉（最多 300 次 / 触发即停；清单需配矮人棉×999）",
        _ => DiscardMaps.ContainsKey(map) ? "到图自动丢一组触发道具" : "",
    };

    /// <summary>列表里的短标签（长文案会挤掉右侧状态）。</summary>
    private static string AutoTriggerShort(string huntCn, string map) => huntCn switch
    {
        "顾尼图" => "自动诱钓挑衅",
        "多智兽" or "阿姆斯特朗" => "自动自爆",
        "牛头黑神" => $"自爆×{AutoBombTries}",
        "天气预报机器人" => "自动狂风暴雪",
        "先驱勇士 阿提卡斯" => "自动做 HQ 牛排",
        "得到宽恕的炫学" => "GBR 自动采矮人棉",
        _ => DiscardMaps.ContainsKey(map) ? "自动丢道具" : "",
    };

    /// <summary>丢一组触发道具（树海 ×50 / 厄尔庇斯 ×5 / 雷克兰德 ×1）。过图或没落地就稍后重试。</summary>
    private void AutoDiscard(AutoJob job, int token, int tries = 0)
    {
        if (token != this.autoToken || !this.autoRunning || this.autoHit.Length > 0) return;
        if (!this.InMap(job.Map))
        {
            // 还在过图：等落地再丢，过图期间丢必失败
            if (tries < 2) this.Sched(() => this.AutoDiscard(job, token, tries + 1), TimeSpan.FromSeconds(4));
            return;
        }

        var r = this.DiscardTrigger();
        if (r.Ok) this.autoDiscardDoneAt = DateTime.UtcNow;
        else if (tries < 2) this.Sched(() => this.AutoDiscard(job, token, tries + 1), TimeSpan.FromSeconds(5));
    }

    /// <summary>触发动作（走完绿点 / 丢完道具 / 采集完）什么时候做完的。还没做完返回 false。
    /// 「丢道具」「采集」这两类本身就是触发动作，做完就按它们计时；
    /// 其余怪以「走完所有绿点」为准。只看本任务开始之后的记录，免得拿上一只的进度误判。</summary>
    private bool AutoActionsDone(out double since, out string what)
    {
        since = 0;
        what = "";

        var discardDone = this.autoDiscardDoneAt != default && this.autoDiscardDoneAt >= this.autoJobStart;
        var gatherDone = this.autoGatherDoneAt != default && this.autoGatherDoneAt >= this.autoJobStart;

        var anchor = default(DateTime);
        if (discardDone) { anchor = this.autoDiscardDoneAt; what = "丢弃完成"; }
        if (gatherDone && (anchor == default || this.autoGatherDoneAt > anchor))
        {
            anchor = this.autoGatherDoneAt;
            what = "采集完成";
        }

        if (anchor == default)
        {
            // 没有专门动作的怪：绿点走完（或自爆次数爆完）就是动作做完
            if (this.autoRouteDoneAt == default || this.autoRouteDoneAt < this.autoJobStart) return false;
            anchor = this.autoRouteDoneAt;
            what = this.autoHasRoute ? "绿点走完" : "触发动作做完";
        }

        since = (DateTime.UtcNow - anchor).TotalSeconds;
        return true;
    }

    /// <summary>到点后等触发：每 1 秒一次；期间出现刷新提示就成功收工。
    /// 触发动作一旦做完（走完绿点 / 丢完道具 / 采集完），只再等 5 秒——没出提示直接算没触发，换下一只。
    /// 之前这里按「到点后干等 90~1500 秒」算，明明动作早做完了还在原地耗着。</summary>
    private void AutoWaitTrigger(AutoJob job, int left, int token)
    {
        this.Sched(() =>
        {
            if (token != this.autoToken || !this.autoRunning) return;
            if (this.autoHit.Length > 0) return;
            if (this.AutoPrepFailedStep(job)) return;
            this.AutoMaybeRetryRoute(job, this.autoJobGreens);

            var total = AutoWaitSeconds(job.HuntCn);

            // 炫学：GatherStrikesThen 收工时自己写 autoGatherDoneAt，直接按 5 秒规则收尾。
            // （比旧版在这里轮询 GBR 开关状态可靠：刚开 IPC、状态还没刷新时不会被误判成「采完了」。）

            // 顾尼图：流程跑完（无论它自己判成功还是超时）后再给一点时间等聊天提示。
            // 刷新提示一到，OnChatMessage 会记成功并暂停队列；没动静说明这趟只是空跑，立刻换下一只。
            if (job.HuntCn == "顾尼图" && this.AutoGounituTick(job, left)) return;

            // ★ 动作做完了：只再等 5 秒
            if (job.HuntCn != "顾尼图" && this.AutoActionsDone(out var since, out var what))
            {
                if (since >= TriggerGraceSeconds)
                {
                    this.Print($"[自动前往] {job.World} · {job.HuntCn} {what}后 {TriggerGraceSeconds} 秒没出刷新提示，判定没触发，下一只");
                    this.autoSkipped.Add((job.HuntCn, "没触发出来",
                        $"{what}后 {TriggerGraceSeconds} 秒没出提示（{job.World}）"));
                    this.AutoStep();
                    return;
                }
                this.autoStatus = $"{job.World} · {job.HuntCn} {what}，等提示（{TriggerGraceSeconds - (int)since}s）";
            }
            else
            {
                this.autoStatus = $"{job.World} · {job.HuntCn} 已到点，触发动作进行中…（{left}s）";
            }

            if (left <= 0)
            {
                this.Print($"[自动前往] {job.World} · {job.HuntCn} 已到点，等了 {total} 秒没出刷新提示，下一只");
                this.autoSkipped.Add((job.HuntCn, "没触发出来", $"到点等了 {total} 秒（{job.World}）"));
                this.AutoStep();
                return;
            }

            // 丢过一次还没出：隔 40 秒再补丢一组（个别图要丢不止一次）
            if (left == 60 && DiscardMaps.ContainsKey(job.Map))
                this.Sched(() => this.AutoDiscard(job, token), TimeSpan.Zero);

            this.AutoWaitTrigger(job, left - 1, token);
        }, TimeSpan.FromSeconds(1));
    }

    /// <summary>暂停队列：作废所有监控链、停掉正在做的触发动作，但队列、游标、命中记录全留着。
    /// 触发成功走这里（不是「整队停止」），玩家打完怪回来点「继续队列」就能接着跑。</summary>
    private void AutoPause(string reason)
    {
        if (!this.autoPaused)
        {
            this.autoRunning = false;
            this.autoPaused = true;
            this.autoGen++;
            this.autoToken++;
            try { this.StopAll(true); } catch { }
        }
        this.autoPauseReason = reason;
        this.autoStatus = "⏸ " + reason + " · 队列已暂停";
        this.Print($"[自动前往] {reason}（队列已暂停，还剩 {Math.Max(0, this.autoQueue.Count - this.autoIdx)} 个任务；" +
                   "点悬浮窗「继续队列」接着跑）");
    }

    /// <summary>从暂停处（或整队停止后）接着跑：autoIdx 已经指向下一个任务，直接 AutoStep 就行。</summary>
    private void AutoResume()
    {
        if (this.autoRunning || this.autoQueue.Count <= this.autoIdx) return;
        this.autoPaused = false;
        this.autoPauseReason = "";
        this.autoRunning = true;
        this.autoGen++;
        this.autoToken++;
        this.autoHit = "";
        this.autoJob = null;
        this.autoStatus = "继续队列…";
        this.Print($"[自动前往] 继续队列（还剩 {Math.Max(0, this.autoQueue.Count - this.autoIdx)} 个任务）");
        this.AutoStep();
    }

    /// <summary>「停止这只，去下一个」：立刻掐掉当前 S 的所有动作，记一笔手动跳过，接着跑下一个 S。</summary>
    private void AutoSkipCurrent()
    {
        var job = this.autoJob;
        if (job is null)
        {
            if (this.autoPaused) this.AutoResume();
            return;
        }
        this.autoGen++;
        this.autoToken++;
        try { this.StopAll(true); } catch { }
        this.autoSkipped.Add((job.HuntCn, "手动跳过", $"在悬浮窗点了「停止这只，去下一个」（{job.World}）"));
        this.Print($"[自动前往] 停止 {job.World} · {job.HuntCn} 的触发，去下一个 S");
        this.autoHit = "";
        this.autoPaused = false;
        this.autoPauseReason = "";
        this.autoRunning = true;
        this.autoJob = null;
        this.autoStatus = $"已跳过 {job.HuntCn}，去下一个…";
        this.AutoStep();
    }

    /// <summary>整队停止：和「暂停」不同，这个是玩家主动收工，队列不再继续。</summary>
    private void AutoStop()
    {
        this.autoRunning = false;
        this.autoPaused = false;
        this.autoPauseReason = "";
        this.autoGen++;
        this.autoToken++;
        this.autoJob = null;
        this.autoHit = "";
        try { this.StopAll(true); } catch { }
        this.autoStatus = "已停止";
        this.Print("[自动前往] 已停止");
        this.PrintHitReport();
        this.PrintSkipReport(false);
    }

    /// <summary>报一下这轮一共触发到了哪几只（继续模式下会攒好几只）。</summary>
    private void PrintHitReport()
    {
        if (this.autoHits.Count == 0) return;
        this.Print($"[自动前往] ✅ 已触发 {this.autoHits.Count} 只：" +
                   string.Join("、", this.autoHits.Select(h => $"{h.world} · {h.cn}")));
    }

    private void AutoFinish()
    {
        this.autoRunning = false;
        this.autoPaused = false;
        this.autoPauseReason = "";
        this.autoGen++;
        this.autoToken++;
        this.autoJob = null;
        var skipped = this.DistinctSkipped();
        this.autoStatus = this.autoHits.Count > 0
            ? $"✅ 触发 {this.autoHits.Count} 只" + (skipped > 0 ? $"，{skipped} 只没跑成" : "，全部跑完")
            : skipped > 0
                ? $"全部走完（{this.autoTotal} 个任务，{skipped} 只没跑成）"
                : $"全部走完（{this.autoTotal} 个），没等到刷新提示";
        this.Print($"[自动前往] 全部走完：共 {this.autoTotal} 个任务");
        this.PrintHitReport();

        if (this.autoSkipped.Count > 0)
        {
            this.Print("");
            this.PrintSkipReport(true);
            this.Print("[自动前往] 上面这些可以稍后重跑；条件窗口类的等窗口到了再来");
        }
    }

    // ───────────── v1.1.71 原生 HUD 试点：数据/委托桥 ─────────────
    // 这两个方法不含任何 KTK 类型，NativeHud.cs 只吃 HudSnapshot / HudActions。
    private HudActions BuildHudActions() => new HudActions
    {
        Primary = () =>
        {
            var remain = this.autoQueue.Count - this.autoIdx;
            if (this.autoRunning) this.AutoPause("手动暂停");
            else if (remain > 0) this.AutoResume();
            else this.AutoStart();
        },
        Skip = () => this.AutoSkipCurrent(),
        Stop = () => this.AutoStop(),
        Restart = () => this.AutoStart(),
        Closed = () => this.autoHudOpen = false,
    };

    private HudSnapshot BuildHudSnapshot()
    {
        var s = new HudSnapshot();
        var badge = this.autoPaused ? "已暂停" : this.autoRunning ? "触发中" : "空闲";
        s.Badge = "● " + badge;
        s.Progress = $"第 {Math.Min(this.autoIdx, this.autoTotal)}/{this.autoTotal} · 已触发 {this.autoHits.Count} 只";

        var job = this.autoJob;
        if (job is not null)
        {
            s.Job = $"{job.World} · {job.HuntCn}";
            var trig = AutoTriggerShort(job.HuntCn, job.Map);
            s.Map = "「" + job.Map + "」" + (trig.Length > 0 ? " · " + trig : "");
            if (this.autoIdx < this.autoQueue.Count)
            {
                var nx = this.autoQueue[this.autoIdx];
                s.Next = $"下一个：{nx.World} · {nx.HuntCn}（还剩 {this.autoQueue.Count - this.autoIdx} 个）";
            }
        }
        else
        {
            s.Job = this.autoCurrent.Length > 0 ? this.autoCurrent : "还没开始触发";
            s.Map = "勾选服务器和 S 后点「开始自动前往」";
        }

        var status = this.autoPaused && this.autoPauseReason.Length > 0
            ? this.autoPauseReason + "\n队列已暂停，点「继续队列」接着跑"
            : this.autoStatus;
        if (status.Length > 30 && !status.Contains('\n')) status = status[..30] + "\n" + status[30..];
        s.Status = status;

        var remain = this.autoQueue.Count - this.autoIdx;
        s.Primary = this.autoRunning ? "暂停" : remain > 0 ? "继续队列" : "重新开始";
        return s;
    }

    // ───────────── v1.1.72 原生「自动前往」窗口：数据/委托桥 ─────────────
    // 同样不含任何 KTK 类型；NativeAutoWindow.cs 只吃 AutoWindowSnapshot / AutoWindowActions。
    private AutoWindowActions BuildAutoWindowActions() => new AutoWindowActions
    {
        GroupStyle = () => this.autoStyle = 1,
        CardStyle = () => this.autoStyle = 2,
        ToggleOnlyOpen = () => this.autoOnlyOpen = !this.autoOnlyOpen,
        ToggleAllWorlds = () => this.autoAllWorlds = !this.autoAllWorlds,
        ToggleHud = () => this.autoHudOpen = !this.autoHudOpen,
        SelectAllWorlds = () =>
        {
            if (this.srankData?.data is not { } d) return;
            foreach (var w in d.worlds) if (w.Length > 0) this.autoWorlds.Add(w);
        },
        ClearWorlds = () => this.autoWorlds.Clear(),
        ToggleWorld = w =>
        {
            if (!this.autoWorlds.Remove(w)) this.autoWorlds.Add(w);
        },
        ToggleHunt = k =>
        {
            if (!this.autoHunts.Remove(k)) this.autoHunts.Add(k);
        },
        ClearHit = () => { this.autoHit = ""; this.autoHits.Clear(); },
        PrevPage = () => { if (this.autoNativePage > 0) this.autoNativePage--; },
        NextPage = () => this.autoNativePage++,   // 下一帧快照会把页码 clamp 回合法范围
        StartStop = () =>
        {
            var canResume = !this.autoRunning && !this.autoPaused && this.autoQueue.Count > this.autoIdx;
            if (this.autoPaused || canResume) this.AutoResume();
            else if (this.autoRunning) this.AutoStop();
            else this.AutoStart();
        },
        Closed = () => this.autoRunOpen = false,
    };

    private AutoWindowSnapshot BuildAutoWindowSnapshot()
    {
        var s = new AutoWindowSnapshot();
        var data = this.srankData?.data;
        s.Ready = data is not null;
        s.StyleGroup = this.autoStyle == 1;
        s.StyleCard = this.autoStyle == 2;
        s.OnlyOpen = this.autoOnlyOpen;
        s.AllWorlds = this.autoAllWorlds;
        s.HudOpen = this.autoHudOpen;

        if (this.autoHit.Length > 0 || this.autoHits.Count > 0)
        {
            s.ShowHitBar = true;
            s.Hit1 = this.autoHit.Length > 0
                ? "✅ 触发成功 · 队列已暂停"
                : $"✅ 已触发 {this.autoHits.Count} 只 · 队列已暂停（悬浮窗里继续）";
            s.Hit2 = this.autoHit.Length > 0
                ? $"「{this.autoHit}」"
                : string.Join("、", this.autoHits.Skip(Math.Max(0, this.autoHits.Count - 3)).Select(h => h.cn));
        }

        var worlds = data is not null ? data.worlds.Where(w => w.Length > 0).ToList() : new List<string>();
        s.WorldCaption = this.autoAllWorlds
            ? $"共 {worlds.Count} 服 · 每只 S 只去进闸的服（只看开闸：{(this.autoOnlyOpen ? "开" : "关")}）"
            : $"服务器（已选 {this.autoWorlds.Count}/{worlds.Count}）· 点服名勾选";
        foreach (var w in worlds.Take(16))
        {
            s.Worlds.Add(w);
            s.WorldOn.Add(this.autoWorlds.Contains(w));
        }

        var qTotal = this.autoQueue.Count;
        var qRemain = Math.Max(0, qTotal - this.autoIdx);
        s.QueueTitle = qTotal == 0
            ? "当前排队 · 还没生成队列（选好服务器和 S 怪后点底部开始）"
            : $"当前排队 · 共 {qTotal} 个 / 剩 {qRemain} 个";
        // 跟着游标走，保证「正在跑的那一条」始终看得见
        var from = Math.Max(0, Math.Min(this.autoIdx - 2, qTotal - 6));
        for (var qi = from; qi < qTotal && s.QueueLines.Count < 6; qi++)
        {
            var j = this.autoQueue[qi];
            var isCur = this.autoRunning && qi == this.autoIdx - 1;
            var isDone = qi < this.autoIdx - 1;
            var mark = isCur ? "▶" : isDone ? "✓" : "·";
            s.QueueLines.Add($"{mark} {qi + 1,2}. {j.World} · {j.HuntCn}（{j.Map}）");
        }

        if (data is not null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var mh = data.hunts;
            var states = new Dictionary<string, (string state, double eta, double pct)>(StringComparer.Ordinal);
            foreach (var hd in mh) states[hd.cn] = this.BestStateOf(hd, now);

            var shown = mh.Where(x => this.AutoShown(x, states));
            var list = (this.autoStyle == 1
                    ? shown.OrderBy(x => x.patch).ThenBy(x => x.cn, StringComparer.Ordinal)
                    : shown.OrderBy(x => x.cn, StringComparer.Ordinal))
                .ToList();
            foreach (var x in list)
            {
                var st = states.TryGetValue(x.cn, out var s0) ? s0 : (state: "unknown", eta: 0.0, pct: 0.0);
                var openWs = this.autoAllWorlds && st.state == "open" ? this.OpenWorldsOf(x, now) : null;
                var right = openWs is { Count: > 0 }
                    ? WorldsHint(openWs)
                    : st.state == "open" ? $"开闸 {st.pct:0}%"
                    : st.state == "cd" ? $"冷却 {TimeText(st.eta)}" : "已过窗";
                s.Hunts.Add(new AutoHuntRow
                {
                    Key = x.cn,
                    Label = x.cn,
                    Right = right,
                    Checked = this.autoHunts.Contains(x.cn),
                });
            }
        }

        s.PageCount = Math.Max(1, (s.Hunts.Count + AutoWindow.PageSize - 1) / AutoWindow.PageSize);
        if (this.autoNativePage < 0) this.autoNativePage = 0;
        if (this.autoNativePage >= s.PageCount) this.autoNativePage = s.PageCount - 1;
        s.Page = this.autoNativePage;

        // v1.1.95：底部说明精简为一行；vnavmesh 是唯一能实时检测的依赖，其余只列名称。
        s.Hints = this.autoRunning
            ? "队列运行中 · 暂停 / 继续 / 停止在「触发悬浮窗」里操作"
            : $"环境检测：vnavmesh {(this.VnavReady() ? "✓" : "✗")} | DailyRoutines 跨界传送 | GatherBuddyReborn";

        var canResume = !this.autoRunning && !this.autoPaused && qTotal > this.autoIdx;
        s.RunLabel = this.autoPaused || canResume ? "继续队列" : this.autoRunning ? "停止" : "开始自动前往";
        if (this.autoAllWorlds && this.autoOnlyOpen)
            s.Counts = $"全部 {worlds.Count} 服 · 已选 {this.autoHunts.Count} 只（先跑本服）";
        else if (this.autoAllWorlds)
            s.Counts = $"全部 {worlds.Count} 服 × {this.autoHunts.Count} 只 = {worlds.Count * this.autoHunts.Count} 个任务";
        else if (this.autoOnlyOpen)
            s.Counts = $"{this.autoWorlds.Count} 服 · 已选 {this.autoHunts.Count} 只（只跑开闸的）";
        else
            s.Counts = $"{this.autoWorlds.Count} 服 × {this.autoHunts.Count} 只 = {this.autoWorlds.Count * this.autoHunts.Count} 个任务";
        s.Status = this.autoStatus.Length > 64 ? this.autoStatus[..64] : this.autoStatus;
        if (this.autoSkipped.Count > 0)
            s.Skipped = $"没跑成 {this.DistinctSkipped()} 只：" +
                        string.Join(" / ", this.autoSkipped.GroupBy(x => x.kind)
                            .Select(g => $"{g.Key} {g.Select(x => x.cn).Distinct().Count()}"));
        return s;
    }

    // ───────────── 悬浮窗 ─────────────

    /// <summary>「正在触发什么」小窗：当前服 / S / 触发方式 / 状态，以及暂停·继续·跳过·整队停止。
    /// 触发成功后队列只是暂停，玩家打完怪回来点「继续队列」就能接着跑。</summary>
    // ImGui 版「触发中」悬浮窗已废弃：触发中 HUD 完全由原生 KTK 窗口（NativeHud）接管，
    // 不再绘制任何 ImGui 兜底窗，避免与原生窗口串台重叠。下方 DrawAutoHudInner 入口已删除，永不被调用（不可达死代码）。
    private void DrawAutoHudInner()
    {
        return; // v1.2.6：旧版 ImGui 触发悬浮窗已彻底废弃，本方法不可达，直接返回，永不绘制。
        // 独立窗口，样式栈必须自带一套（和自动前往主窗一样）
        PushAutoStyle();
        try
        {
            ImGui.SetNextWindowSize(new Vector2(330f, 232f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(270f, 190f), new Vector2(560f, 520f));
            var open = this.autoHudOpen;
            var visible = ImGui.Begin("触发中 · 魔法水桶###magicbucket-hud", ref open);
            this.autoHudOpen = open;
            if (!visible) { ImGui.End(); return; }

            var badge = this.autoPaused ? "已暂停" : this.autoRunning ? "触发中" : "空闲";
            var badgeCol = this.autoPaused ? V1Pink : this.autoRunning ? V1Mint : V1Dim;
            this.AutoText("● " + badge, 12.5f, badgeCol);
            ImGui.SameLine(0, 10);
            this.AutoText($"第 {Math.Min(this.autoIdx, this.autoTotal)}/{this.autoTotal} · 已触发 {this.autoHits.Count} 只",
                11.5f, V1Faint);

            var job = this.autoJob;
            if (job is not null)
            {
                this.AutoText($"{job.World} · {job.HuntCn}", 16f, V1Text);
                var trig = AutoTriggerShort(job.HuntCn, job.Map);
                this.AutoText("「" + job.Map + "」" + (trig.Length > 0 ? " · " + trig : ""), 11.5f, V1Dim);
                if (this.autoIdx < this.autoQueue.Count)
                {
                    var nx = this.autoQueue[this.autoIdx];
                    this.AutoText($"下一个：{nx.World} · {nx.HuntCn}（还剩 {this.autoQueue.Count - this.autoIdx} 个）",
                        10.5f, V1Faint);
                }
            }
            else
            {
                this.AutoText(this.autoCurrent.Length > 0 ? this.autoCurrent : "还没开始触发", 15f, V1Text);
                this.AutoText("勾选服务器和 S 后点「开始自动前往」", 11.5f, V1Dim);
            }

            var status = this.autoPaused && this.autoPauseReason.Length > 0
                ? this.autoPauseReason + "\n队列已暂停，点「继续队列」接着跑"
                : this.autoStatus;
            if (status.Length > 30 && !status.Contains('\n')) status = status[..30] + "\n" + status[30..];
            this.AutoText(status, 11.5f, this.autoRunning ? V1Accent : V1Dim);

            ImGui.Separator();
            var w = (ImGui.GetContentRegionAvail().X - 8f) / 2f;
            void Btn(string id, string label, Vector4 bg, Vector4 fg, Action act, float h = 30f)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, bg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                    new Vector4(bg.X, bg.Y, bg.Z, Math.Min(1f, bg.W + 0.12f)));
                ImGui.PushStyleColor(ImGuiCol.Text, fg);
                if (ImGui.Button(label + "##" + id, new Vector2(w, h))) act();
                ImGui.PopStyleColor(3);
            }

            var remain = this.autoQueue.Count - this.autoIdx;
            if (this.autoRunning)
                Btn("pause", "暂停", new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.30f), V1Text,
                    () => this.AutoPause("手动暂停"));
            else if (remain > 0)
                Btn("resume", "继续队列", new Vector4(V1Mint.X, V1Mint.Y, V1Mint.Z, 0.42f), V1Text, this.AutoResume);
            else
                Btn("resume", "重新开始", new Vector4(V1Mint.X, V1Mint.Y, V1Mint.Z, 0.22f), V1Dim, this.AutoStart);
            ImGui.SameLine(0, 8);
            Btn("skip", "停止这只，去下一个", new Vector4(V1Pink.X, V1Pink.Y, V1Pink.Z, 0.28f), V1Text,
                this.AutoSkipCurrent);

            var stopBg = lightTheme ? new Vector4(0.72f, 0.13f, 0.17f, 0.92f) : new Vector4(0.62f, 0.20f, 0.24f, 0.85f);
            Btn("stop", "停止整队", stopBg, new Vector4(1, 1, 1, 1), this.AutoStop, 28f);
            ImGui.SameLine(0, 8);
            Btn("restart", "重新开始", V1Card, V1Dim, this.AutoStart, 28f);

            this.AutoText("关掉后：主窗「触发悬浮窗」按钮 或 /水桶 hud", 10.5f, V1Faint);
            ImGui.End();
        }
        finally { PopAutoStyle(); }
    }

    // ImGui 版「自动前往」窗已废弃：自动前往窗完全由原生 KTK 窗口（NativeAutoWindow）接管，
    // 不再绘制任何 ImGui 兜底窗，避免与原生窗口串台重叠。下方 DrawAutoWindowInner 入口已删除，永不被调用（不可达死代码）。
    private void DrawAutoWindowInner()
    {
        return; // v1.2.6：旧版 ImGui 自动前往窗已彻底废弃，本方法不可达，直接返回，永不绘制。
        // 主窗口 End 之后才画这个窗，样式栈已弹出，这里必须自带一套主题色
        PushAutoStyle();
        try
        {
            ImGui.SetNextWindowSize(new Vector2(this.autoStyle == 2 ? 660f : 560f, 540f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(430f, 340f), new Vector2(1400f, 1200f));

            var open = this.autoRunOpen;
            var visible = ImGui.Begin("自动前往 · 魔法水桶###magicbucket-autorun", ref open);
            this.autoRunOpen = open;
            if (!visible)
            {
                ImGui.End();
                return;
            }

            if (this.srankData?.data is not { } data)
            {
                ImGui.TextDisabled("数据还没加载好…");
                ImGui.End();
                return;
            }

        // 触顶提示条：命中刷新提示就整窗变绿，别让用户找不到。
        // 继续模式下（默认）autoHit 已被清空，这里改显示「已触发 N 只」的进度。
        if (this.autoHit.Length > 0 || this.autoHits.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg,
                new Vector4(V1Mint.X, V1Mint.Y, V1Mint.Z, lightTheme ? 0.16f : 0.30f));
            ImGui.BeginChild("##hitbar", new Vector2(0, 56), true);
            if (this.autoHit.Length > 0)
            {
                this.AutoText("✅ 触发成功 · 队列已暂停", 15f, V1Mint);
                this.AutoText($"「{this.autoHit}」", 12f, V1Dim);
            }
            else
            {
                this.AutoText($"✅ 已触发 {this.autoHits.Count} 只 · 队列已暂停（悬浮窗里继续）", 14f, V1Mint);
                this.AutoText(string.Join("、", this.autoHits.Skip(Math.Max(0, this.autoHits.Count - 3))
                    .Select(h => h.cn)), 12f, V1Dim);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            if (ImGui.Button("知道了，清掉提示##clearhit", new Vector2(160, 26)))
            {
                this.autoHit = "";
                this.autoHits.Clear();
            }
            ImGui.Separator();
        }

        // 样式切换 + 只看开闸
        this.AutoText("样式", 12f, V1Faint);
        ImGui.SameLine(0, 8);
        for (var i = 1; i <= 2; i++)
        {
            if (i > 1) ImGui.SameLine(0, 6);
            var label = i == 1 ? "分组" : "卡片";
            var on = this.autoStyle == i;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleColor(ImGuiCol.Button, on ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.20f) : V1Card);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.30f) : V1Hover);
            ImGui.PushStyleColor(ImGuiCol.Text, on ? V1Accent : V1Dim);
            if (ImGui.Button(label + "##autostyle" + i, new Vector2(64, 26))) this.autoStyle = i;
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();
        }

        ImGui.SameLine(0, 14);
        var oo = this.autoOnlyOpen;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleColor(ImGuiCol.Button, oo ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.20f) : V1Card);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, oo ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.30f) : V1Hover);
        ImGui.PushStyleColor(ImGuiCol.Text, oo ? V1Accent : V1Dim);
        if (ImGui.Button((oo ? "只看开闸 ✓" : "只看开闸") + "##onlyopen", new Vector2(96, 26)))
            this.autoOnlyOpen = !oo;
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(oo
                ? "现在只列出已进闸（可触发窗口内）的 S\n点一下显示全部"
                : "现在列出全部 S\n点一下只看已进闸的");

        ImGui.SameLine(0, 10);
        this.AutoText($"仅 {AutoMaps.Count} 张图", 11.5f, V1Faint);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自动前往只跑这些地图的 S（其余地图不列出、也不去）：\n" +
                             string.Join("、", AutoMapNames));

        // 「正在触发什么」小悬浮窗开关（里面能暂停 / 继续 / 跳过 / 整队停止）
        ImGui.SameLine(0, 12);
        var hud = this.autoHudOpen;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleColor(ImGuiCol.Button, hud ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.20f) : V1Card);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hud ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.30f) : V1Hover);
        ImGui.PushStyleColor(ImGuiCol.Text, hud ? V1Accent : V1Dim);
        if (ImGui.Button((hud ? "触发悬浮窗 ✓" : "触发悬浮窗") + "##hudtoggle", new Vector2(112, 26)))
            this.autoHudOpen = !hud;
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("显示 / 隐藏「正在触发什么」的小悬浮窗\n" +
                             "里面可以暂停、继续队列、停止当前这只去下一个、或整队停止");

        ImGui.Separator();

        // ── 当前排队：按顺序把「服务器 + S 怪」摊开，跑到哪一条一眼可见 ──
        var qTotal = this.autoQueue.Count;
        var qRemain = Math.Max(0, qTotal - this.autoIdx);
        if (ImGui.CollapsingHeader($"当前排队 · 共 {qTotal} 个 / 剩 {qRemain} 个##autoqueue",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (qTotal == 0)
            {
                this.AutoText("还没生成队列：选好服务器和 S 怪，点底部「开始自动前往」", 11.5f, V1Dim);
            }
            else
            {
                ImGui.BeginChild("##autoqueuelist", new Vector2(0, Math.Min(120f, 20f * Math.Min(qTotal, 6))), true);
                for (var qi = 0; qi < qTotal; qi++)
                {
                    var j = this.autoQueue[qi];
                    var isCur = this.autoRunning && qi == this.autoIdx - 1;
                    var isDone = qi < this.autoIdx - 1;
                    var col = isCur ? V1Mint : isDone ? V1Faint : V1Dim;
                    var mark = isCur ? "▶" : isDone ? "✓" : "·";
                    this.AutoText($"{mark} {qi + 1,2}. {j.World} · {j.HuntCn}（{j.Map}）", 11.5f, col);
                }
                ImGui.EndChild();
            }
        }
        ImGui.Separator();

        // 服务器选择（两种样式共用顶部 chips）
        var worlds = data is not null ? data.worlds.Where(w => w.Length > 0).ToList() : new List<string>();
        this.AutoText(this.autoAllWorlds ? "服务器" : $"服务器（已选 {this.autoWorlds.Count}/{worlds.Count}）", 12.5f, V1Dim);
        ImGui.SameLine(0, 8);

        // 「全部服务器」：不用逐个挑服，列表里直接标出每只 S 在哪些服进闸
        var aw = this.autoAllWorlds;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleColor(ImGuiCol.Button, aw ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.20f) : V1Card);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, aw ? new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.30f) : V1Hover);
        ImGui.PushStyleColor(ImGuiCol.Text, aw ? V1Accent : V1Dim);
        if (ImGui.Button((aw ? "全部服务器 ✓" : "全部服务器") + "##allworlds", new Vector2(104, 26)))
            this.autoAllWorlds = !aw;
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(aw
                ? "现在用全部服务器\n每只 S 只去已进闸的服，列表里会写出来\n点一下改成逐个挑服"
                : "一键用全部服务器，不用逐个挑\n勾上后列表里直接写出每只 S 进闸的服");

        if (this.autoAllWorlds)
        {
            ImGui.SameLine(0, 10);
            this.AutoText($"共 {worlds.Count} 服 · 每只 S 只去进闸的服", 12f, V1Faint);
        }
        else
        {
            ImGui.SameLine(0, 6);
            if (ImGui.SmallButton("全选##allworld")) foreach (var w in worlds) this.autoWorlds.Add(w);
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("清空##clrworld")) this.autoWorlds.Clear();
            this.AutoChips(worlds);
        }

        ImGui.Separator();

        // 启用前提示应当开好的插件（只在还没开始跑时显示，避免干扰触发中的状态）
        if (!this.autoRunning)
        {
            var vnavOk = this.VnavReady();
            this.AutoText("启用前请先开好：", 11.5f, V1Dim);
            this.AutoText("· vnavmesh-cn" + (vnavOk ? " ✓" : " ✗ 未检测到，请启用"), 11f, vnavOk ? V1Mint : V1Pink);
            this.AutoText("· DailyRoutines · 跨界传送模块", 11f, V1Dim);
            this.AutoText("· GatherBuddyReborn（采集类 S，清单配矮人棉×999）", 11f, V1Dim);
        }

        // 主体：先按当前勾选的服算出每只怪的最好状态，两个样式共用
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var states = new Dictionary<string, (string state, double eta, double pct)>(StringComparer.Ordinal);
        foreach (var hd in data.hunts) states[hd.cn] = this.BestStateOf(hd, now);

        var bodyH = Math.Max(120f, ImGui.GetContentRegionAvail().Y - (this.autoSkipped.Count > 0 ? 80f : 62f));
        if (this.autoStyle == 2) this.AutoBodyCards(bodyH, states);
        else this.AutoBodyGrouped(bodyH, states);

        // 底部：状态 + 开始/停止
        ImGui.Separator();
        var btnW = 152f;
        var run = this.autoRunning;
        var paused = this.autoPaused;
        var canResume = !run && !paused && this.autoQueue.Count > this.autoIdx;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        // 停止键在亮色下用饱和深红，暗色下压一点亮度，两种主题都能一眼看出是「危险」
        var stopBg = lightTheme ? new Vector4(0.72f, 0.13f, 0.17f, 0.95f) : new Vector4(0.62f, 0.20f, 0.24f, 0.85f);
        var stopHi = lightTheme ? new Vector4(0.80f, 0.17f, 0.21f, 1f) : new Vector4(0.72f, 0.24f, 0.28f, 0.95f);
        var goBg = paused || canResume ? new Vector4(V1Mint.X, V1Mint.Y, V1Mint.Z, 0.85f)
                                       : new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 0.85f);
        ImGui.PushStyleColor(ImGuiCol.Button, run ? stopBg : goBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, run ? stopHi : new Vector4(V1Accent.X, V1Accent.Y, V1Accent.Z, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, run ? new Vector4(1, 1, 1, 1) : V1OnAccent);
        var runLabel = paused || canResume ? "继续队列##autorun" : run ? "停止##autorun" : "开始自动前往##autorun";
        if (ImGui.Button(runLabel, new Vector2(btnW, 32)))
        {
            if (paused || canResume) this.AutoResume();
            else if (run) this.AutoStop();
            else this.AutoStart();
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        ImGui.SameLine(0, 10);
        if (this.autoAllWorlds && this.autoOnlyOpen)
            this.AutoText($"全部 {worlds.Count} 服 · 已选 {this.autoHunts.Count} 只（先跑本服）", 12f, V1Faint);
        else if (this.autoAllWorlds)
            this.AutoText($"全部 {worlds.Count} 服 × {this.autoHunts.Count} 只 = {worlds.Count * this.autoHunts.Count} 个任务", 12f, V1Faint);
        else if (this.autoOnlyOpen)
            this.AutoText($"{this.autoWorlds.Count} 服 · 已选 {this.autoHunts.Count} 只（只跑开闸的）", 12f, V1Faint);
        else
        {
            var sel = this.autoWorlds.Count * this.autoHunts.Count;
            this.AutoText($"{this.autoWorlds.Count} 服 × {this.autoHunts.Count} 只 = {sel} 个任务", 12f, V1Faint);
        }

        this.AutoText(this.autoStatus, 12.5f,
            this.autoRunning ? V1Accent : (this.autoHit.Length > 0 || this.autoHits.Count > 0 ? V1Mint : V1Dim));

        // 跳过汇总：一行分类计数，明细放悬浮提示，不占高度
        if (this.autoSkipped.Count > 0)
        {
            var kinds = this.autoSkipped.GroupBy(s => s.kind)
                .Select(g => $"{g.Key} {g.Select(x => x.cn).Distinct().Count()}");
            this.AutoText($"没跑成 {this.DistinctSkipped()} 只：{string.Join(" / ", kinds)}", 11.5f, V1Pink);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n\n", this.autoSkipped.GroupBy(s => s.kind)
                    .Select(g => g.Key + "\n  " + string.Join("、", g.Select(x => x.cn).Distinct()))));
        }

            ImGui.End();
        }
        finally
        {
            PopAutoStyle();
        }
    }

    // ── 主题：主窗口用的是自绘透明底，这个窗在主窗 End 之后画，样式栈已经弹空，
    //    所以自己推一套。数量必须和 PushAutoStyle 一致（23 色 / 9 变量）。
    private void PushAutoStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, V1Bg);
        ImGui.PushStyleColor(ImGuiCol.Text, V1Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, V1Dim);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, V1Input);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, V1Card);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, V1BtnAct);
        ImGui.PushStyleColor(ImGuiCol.Button, V1Card);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V1BtnHi);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, V1BtnAct);
        ImGui.PushStyleColor(ImGuiCol.Header, V1Sel);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, V1Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, V1HoverMid);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, V1PopBg);
        ImGui.PushStyleColor(ImGuiCol.Separator, V1Border);
        ImGui.PushStyleColor(ImGuiCol.Border, V1BorderHi);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Transparent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Transparent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, V1Scroll);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, V1ScrollHi);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, V1ScrollHi);
        // 标题栏：亮色主题下默认那条深灰最扎眼
        ImGui.PushStyleColor(ImGuiCol.TitleBg, V1Bg2);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, V1Bg2);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, V1Bg2);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
    }

    private static void PopAutoStyle()
    {
        ImGui.PopStyleColor(23);
        ImGui.PopStyleVar(9);
    }

    /// <summary>世界 chips：点一下勾选/取消，当前所在服加一圈。</summary>
    private void AutoChips(List<string> worlds)
    {
        var cur = this.CurrentWorldName();
        var dl = ImGui.GetWindowDrawList();
        var lineRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 4f));
        foreach (var w in worlds)
        {
            var on = this.autoWorlds.Contains(w);
            var c = WorldColor(w);
            var wd = this.TW(w, 12.5f) + 30f;
            if (ImGui.GetItemRectMax().X + 6f + wd <= lineRight) ImGui.SameLine(0, 6);
            ImGui.PushStyleColor(ImGuiCol.Button, on ? new Vector4(c.X, c.Y, c.Z, 0.16f) : V1Card);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(c.X, c.Y, c.Z, 0.26f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.Border, on ? new Vector4(c.X, c.Y, c.Z, 0.8f) : V1Border);
            var clicked = ImGui.Button("##w" + w, new Vector2(wd, 24f));
            ImGui.PopStyleColor(4);
            var r = ImGui.GetItemRectMin();
            var rm = ImGui.GetItemRectMax();
            var isCur = string.Equals(cur, w, StringComparison.OrdinalIgnoreCase);
            dl.AddCircle(new Vector2(r.X + 11f, (r.Y + rm.Y) / 2f), 4.2f,
                ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, on ? 1f : 0.45f)), 12);
            if (isCur)
                dl.AddCircle(new Vector2(r.X + 11f, (r.Y + rm.Y) / 2f), 7.4f,
                    ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, 0.85f)), 16, 1.4f);
            dl.AddText(this.FontFor(12.5f), 12.5f, new Vector2(r.X + 21f, r.Y + 4f),
                ImGui.GetColorU32(on ? c : V1Dim), w);
            if (clicked)
            {
                if (on) this.autoWorlds.Remove(w);
                else this.autoWorlds.Add(w);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(w + (isCur ? "（你在这个服）" : "") + (on ? "\n点击取消" : "\n点击勾选"));
        }
        ImGui.PopStyleVar(2);
    }

    // ── 样式 1：按版本分组、可折叠 ──
    private void AutoBodyGrouped(float h, Dictionary<string, (string state, double eta, double pct)> states)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var mh = this.srankData?.data?.hunts ?? System.Array.Empty<HuntData>();
        var groups = new (string key, string label)[]
        {
            ("DT", "7.x 金曦之遗辉"), ("EW", "6.x 晓月之终途"), ("ShB", "5.x 暗影之逆焰"),
            ("SB", "4.x 红莲之狂潮"), ("HW", "3.x 苍天之龙骑士"), ("ARR", "2.x 重生之境"),
        };

        ImGui.BeginChild("##body2", new Vector2(0, h), false);
        var any = false;
        foreach (var (key, label) in groups)
        {
            var list = mh
                .Where(x => x.patch == key && this.AutoShown(x, states))
                .OrderBy(x => x.cn, StringComparer.Ordinal)
                .ToList();
            if (list.Count == 0) continue;
            any = true;

            var closed = this.autoGroupsClosed.Contains(key);
            var picked = list.Count(x => this.autoHunts.Contains(x.cn));
            var head = $"{label}　{picked}/{list.Count}##grp{key}";
            if (ImGui.CollapsingHeader(head, closed ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (closed) this.autoGroupsClosed.Remove(key);
                if (ImGui.SmallButton("全选本组##g" + key))
                    foreach (var x in list) this.autoHunts.Add(x.cn);
                ImGui.SameLine(0, 4);
                if (ImGui.SmallButton("清空本组##gc" + key))
                    foreach (var x in list) this.autoHunts.Remove(x.cn);
                foreach (var x in list)
                {
                    var st = states.TryGetValue(x.cn, out var s0) ? s0 : (state: "unknown", eta: 0.0, pct: 0.0);
                    // 全部服务器模式：右边不写百分比，改写成这只 S 在哪些服进闸了——这才是要看的
                    var openWs = this.autoAllWorlds && st.state == "open" ? this.OpenWorldsOf(x, now) : null;
                    var right = openWs is { Count: > 0 }
                        ? WorldsHint(openWs)
                        : st.state == "open" ? $"开闸 {st.pct:0}%" : st.state == "cd" ? $"冷却 {TimeText(st.eta)}" : "已过窗";
                    var tip = openWs is { Count: > 1 } ? "进闸的服：" + string.Join("、", openWs) : "";
                    var on = this.autoHunts.Contains(x.cn);
                    var trig = AutoTriggerShort(x.cn, x.map);
                    var sub = trig.Length > 0 ? x.map + " · " + trig : x.map;
                    var full = AutoTriggerHint(x.cn, x.map);
                    if (full.Length > 0) tip = tip.Length > 0 ? full + "\n" + tip : full;
                    if (this.AutoCheckRow("g" + x.en, x.cn, sub, right, on, StateColor(st.state),
                            ImGui.GetContentRegionAvail().X, 28f, tip))
                    {
                        if (on) this.autoHunts.Remove(x.cn);
                        else this.autoHunts.Add(x.cn);
                    }
                }
            }
            else if (!closed) this.autoGroupsClosed.Add(key);
        }
        if (!any) this.AutoEmptyHint();
        ImGui.EndChild();
    }

    // ── 样式 2：极简卡片（只有名字 + 状态色条，详情走悬浮提示） ──
    private void AutoBodyCards(float h, Dictionary<string, (string state, double eta, double pct)> states)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hunts = (this.srankData?.data?.hunts ?? System.Array.Empty<HuntData>())
            .Where(x => this.AutoShown(x, states))
            .OrderBy(x => x.patch)
            .ThenBy(x => x.cn, StringComparer.Ordinal)
            .ToList();

        ImGui.BeginChild("##cards", new Vector2(0, h), false);
        const float cardW = 152f, cardH = 38f, gap = 6f;
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)Math.Floor((avail + gap) / (cardW + gap)));
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < hunts.Count; i++)
        {
            var x = hunts[i];
            if (i % perRow != 0) ImGui.SameLine(0, gap);
            var on = this.autoHunts.Contains(x.cn);
            var st = states.TryGetValue(x.cn, out var s1) ? s1 : (state: "unknown", eta: 0.0, pct: 0.0);
            var accent = StateColor(st.state);
            // 全部服务器模式：卡片右侧标个「N服」，具体哪几个服看悬浮提示
            var openWs = this.autoAllWorlds && st.state == "open" ? this.OpenWorldsOf(x, now) : null;

            ImGui.PushID("c" + x.en);
            var p = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##card", new Vector2(cardW, cardH)))
            {
                if (on) this.autoHunts.Remove(x.cn);
                else this.autoHunts.Add(x.cn);
            }
            var hov = ImGui.IsItemHovered();
            dl.AddRectFilled(p, p + new Vector2(cardW, cardH),
                ImGui.GetColorU32(on ? new Vector4(accent.X, accent.Y, accent.Z, 0.15f) : (hov ? V1Hover : V1Card)), 7f);
            dl.AddRect(p + new Vector2(0.5f, 0.5f), p + new Vector2(cardW - 0.5f, cardH - 0.5f),
                ImGui.GetColorU32(on ? new Vector4(accent.X, accent.Y, accent.Z, 0.70f) : V1Border), 7f);
            // 左侧一道状态色，代替文字
            dl.AddRectFilled(p + new Vector2(0, 8), p + new Vector2(2.5f, cardH - 8), ImGui.GetColorU32(accent), 2f);

            dl.AddText(this.FontFor(13f), 13f, new Vector2(p.X + 11f, p.Y + (cardH - 17f) / 2f),
                ImGui.GetColorU32(on ? V1Text : V1Dim), x.cn);

            // 进闸的服有几个（全部服务器模式下才画），太窄放不下名字就靠 tooltip
            if (openWs is { Count: > 0 })
            {
                var badge = openWs.Count > 1 ? $"{openWs.Count}服" : "";
                if (badge.Length > 0)
                {
                    var bw = this.TW(badge, 10.5f);
                    dl.AddText(this.FontFor(10.5f), 10.5f,
                        new Vector2(p.X + cardW - 24f - bw, p.Y + (cardH - 13f) / 2f),
                        ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, 0.9f)), badge);
                }
            }

            // 右上小勾选圈
            var cx = new Vector2(p.X + cardW - 14f, p.Y + cardH / 2f);
            dl.AddCircle(cx, 5.5f, ImGui.GetColorU32(on ? accent : V1Border), 16, 1.4f);
            if (on) dl.AddCircleFilled(cx, 3f, ImGui.GetColorU32(accent), 12);

            if (hov)
            {
                var tip = $"{x.cn}\n{x.map} · {x.patch}\n" + (st.state == "open"
                    ? $"开闸中 {st.pct:0}%"
                    : st.state == "cd" ? "冷却 " + TimeText(st.eta) : "窗口已过");
                if (openWs is { Count: > 0 }) tip += "\n进闸的服：" + string.Join("、", openWs);
                var trig = AutoTriggerHint(x.cn, x.map);
                if (trig.Length > 0) tip += "\n自动触发：" + trig;
                ImGui.SetTooltip(tip);
            }
            ImGui.PopID();
        }
        if (hunts.Count == 0) this.AutoEmptyHint();
        ImGui.EndChild();
    }

    // ── 小组件 ──

    /// <summary>「只看开闸」开着时，是否列出这只怪（地图在范围内，且所选服里有一服开闸）。
    /// </summary>
    private bool AutoShown(HuntData x, Dictionary<string, (string state, double eta, double pct)> states)
        => AutoMapOk(x) && (!this.autoOnlyOpen || (states.TryGetValue(x.cn, out var st) && st.state == "open"));

    /// <summary>过滤后一只都不剩时的提示。</summary>
    private void AutoEmptyHint()
    {
        ImGui.Dummy(new Vector2(0, 18));
        this.AutoText("当前没有已开闸的 S", 13.5f, V1Dim);
        this.AutoText("点上面的「只看开闸 ✓」可以显示全部", 12f, V1Faint);
    }

    /// <summary>一行「勾选 + 名字 + 副标题 + 右侧状态」；tip 非空时悬停显示。</summary>
    private bool AutoCheckRow(string id, string title, string sub, string right, bool on, Vector4 accent,
                              float width, float h, string tip = "")
    {
        ImGui.PushID(id);
        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##hit", new Vector2(Math.Max(60f, width), h));
        var hov = ImGui.IsItemHovered();
        if (hov && tip.Length > 0) ImGui.SetTooltip(tip);
        var dl = ImGui.GetWindowDrawList();
        if (hov || on)
        {
            dl.AddRectFilled(p, p + new Vector2(width, h),
                ImGui.GetColorU32(on ? new Vector4(accent.X, accent.Y, accent.Z, 0.12f) : V1Hover), 6f);
        }

        var box = new Vector2(p.X + 8f, p.Y + (h - 15f) / 2f);
        dl.AddRect(box, box + new Vector2(15, 15), ImGui.GetColorU32(on ? accent : V1Border), 4f, 0, 1.4f);
        if (on) dl.AddRectFilled(box + new Vector2(3, 3), box + new Vector2(12, 12), ImGui.GetColorU32(accent), 2f);

        var tx = box.X + 24f;
        var hasSub = sub.Length > 0;
        dl.AddText(this.FontFor(13.5f), 13.5f, new Vector2(tx, p.Y + (hasSub ? 2f : (h - 17f) / 2f)),
            ImGui.GetColorU32(on ? V1Text : V1Dim), title);
        if (hasSub)
            dl.AddText(this.FontFor(11f), 11f, new Vector2(tx, p.Y + h - 14f), ImGui.GetColorU32(V1Faint), sub);
        if (right.Length > 0)
        {
            var rw = this.TW(right, 11.5f);
            dl.AddText(this.FontFor(11.5f), 11.5f,
                new Vector2(p.X + width - rw - 10f, p.Y + (h - 14f) / 2f), ImGui.GetColorU32(V1Faint), right);
        }
        ImGui.PopID();
        return clicked;
    }

    private void AutoText(string text, float size, Vector4 col)
    {
        ImGui.PushFont(this.FontFor(size));
        ImGui.PushStyleColor(ImGuiCol.Text, col);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    /// <summary>一只怪在当前大区里的最好状态（有服开闸就算开闸）。</summary>
    private (string state, double eta, double pct) BestStateOf(HuntData h, long now)
    {
        var best = (state: "unknown", eta: 0.0, pct: 0.0);
        var rank = 9;
        foreach (var w in h.worlds)
        {
            if (w.lastDeath <= 0) continue;
            if (!this.autoAllWorlds && this.autoWorlds.Count > 0 && !this.autoWorlds.Contains(w.world)) continue;
            var c = CalcState(h, w.lastDeath, now);
            var r = c.State switch { "open" => 0, "cd" => 1, "over" => 2, _ => 3 };
            if (r < rank) { rank = r; best = (c.State, c.Eta, c.Pct); }
        }
        return best;
    }

    private static string TimeText(double seconds)
    {
        if (seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : $"{t.Minutes}m{t.Seconds:00}s";
    }
}
