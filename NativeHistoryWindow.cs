using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.2.12：S 怪历史记录窗口（需求里的 NativeHistoryWindow）。
//
// 【为什么照抄主窗的骨架】
//   NativeMainWindow 已经把「固定层 + ScrollingNode<ResNode> + 行节点池」在国服客户端上
//   验证过：固定层挂 addon 根（表头/工具区不随列表滚走）、行节点挂 scroll.ContentNode、
//   裁剪区与滚动条每帧显式对齐视口、窗口 Size 只在 OnSetup 设一次（Reflow 绝不改 this.Size）。
//   这里完整沿用这套布局，避免重新踩 KTK 的裁剪 / 滚动条 / 节点释放坑。
//
// 【节点集】只用已验证安全的节点：TextNode（可见文字一律自有）、TextButtonNode（按钮）、
//   ColorImageNode（条纹底；ResNode 不产生绘制图元）、ScrollingNode<ResNode>。
//   绝不引入 ProgressBar / Icon / ListBox / BorderNineGrid 等带纹理组件（国服常缺失，构造即抛）。
//   TextButtonNode 统一 Prepare + InitHover，OnClick 走原生 OnClick，绝不注册
//   MouseDown / MouseClick（KTK 会加 HasCollision 把点击吞掉）。
//
// 【分页而不是铺 500 行】数据层保留最近 500 条，但 UI 每页只渲染 24 行：
//   500 个原生节点会有明显性能与 CTD 风险；分页 + 筛选可以翻到全部历史，节点池恒定大小。
//
// 【v1.2.10 铁律】所有 Apply 都有 setupDone / 节点就绪守卫，单窗异常绝不拖垮 Runtime。
// 【v1.2.11 铁律】插件不得替用户点亮任何窗口；OnHide 如实回写，程序内 Close 用 SuppressClosed 屏蔽。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>原生历史窗口：标题 + 筛选 + 分页/清空/关闭 + 5 列表头 + 滚动列表。</summary>
internal sealed class NativeHistoryWindow : NativeAddon
{
    internal const float W = 700f;
    internal const float H = 520f;

    private const float CW = 676f;                 // 固定层宽度（= W - ContentStartPosition.X）
    private const float TopInset = 8f;
    private const float BottomPad = 8f;
    private const float HeaderH = 22f;
    private const float RowH = 24f;
    private const float ScrollBarTrackW = 8f;
    private const int RowPool = 24;                // = SrankHistory.PageSize，节点池恒定

    // 5 列绝对坐标（与表头严格共用同一组常量，文字左缘才能对齐）
    private const float TimeX = 8f, TimeW = 112f;
    private const float NameX = 126f, NameW = 172f;
    private const float ServerX = 304f, ServerW = 104f;
    private const float EventX = 414f, EventW = 62f;
    private const float SourceX = 482f, SourceW = 92f;

    // 配色：与主窗同一套 FFXIV 暗色原生风
    private static readonly Vector4 MutedText = new(0.52f, 0.56f, 0.62f, 1f);
    private static readonly Vector4 ListText = new(0.86f, 0.89f, 0.94f, 1f);
    private static readonly Vector4 KillRed = new(0.90f, 0.34f, 0.36f, 1f);     // 击杀：暗红
    private static readonly Vector4 SpawnGreen = new(0.44f, 0.88f, 0.50f, 1f);   // 出生：绿
    private static readonly Vector4 SonarGold = new(1.00f, 0.80f, 0.30f, 1f);    // Sonar：金
    private static readonly Vector4 SiteGray = new(0.56f, 0.60f, 0.66f, 1f);     // 主站：灰
    private static readonly Vector4 StripeA = new(0.12f, 0.15f, 0.21f, 0.85f);
    private static readonly Vector4 StripeB = new(0.18f, 0.21f, 0.28f, 0.85f);

    private static readonly (HistoryFilter Filter, string Label)[] Filters =
    {
        (HistoryFilter.All,          "全部"),
        (HistoryFilter.KillOnly,     "只看击杀"),
        (HistoryFilter.SpawnOnly,    "只看出生"),
        (HistoryFilter.SonarOnly,    "只看 Sonar"),
        (HistoryFilter.MainSiteOnly, "只看主站"),
    };

    internal static Action<string>? Logger;
    private static void Log(string m)
    {
        try { Logger?.Invoke("[SrankFlag.UI] " + m); } catch { }
    }

    private sealed class RowNodes
    {
        internal ColorImageNode? Bg;
        internal TextNode? Time, Monster, Server, Event, Source;
    }

    private readonly HistoryActions actions;
    private readonly RowNodes?[] rows = new RowNodes?[RowPool];
    private readonly TextButtonNode?[] filterButtons = new TextButtonNode?[5];
    private readonly TextNode?[] headers = new TextNode?[5];

    private bool setupDone;                 // v1.2.10：OnSetup 未就绪 / OnFinalize 已释放时 Apply 直接跳过
    private ResNode? fixedRoot;
    private ScrollingNode<ResNode>? scroll;
    private ResNode? content;
    private NodeBase? attachParent;         // 固定层阶段指向 fixedRoot，行阶段为 null（挂 content）

    private TextNode? title, status, mapHint;
    private TextButtonNode? bPrev, bNext, bClear, bClose;
    private float lastViewH = -1f, lastScrollW = -1f;
    private bool clearArmed;
    private DateTime clearArmedAtUtc;       // 清空需 5 秒内二次确认，防误触

    internal NativeHistoryWindow(HistoryActions actions) => this.actions = actions;

    internal bool SuppressClosed;

    // ───────────────────── OnSetup：一次性建节点 ─────────────────────

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        this.setupDone = false;
        try
        {
            SetWindowSize(new Vector2(W, H));    // ★ 只在这里设一次，Reflow 绝不写 this.Size
            var p = this.ContentStartPosition;

            // 关窗重开（或过图释放 addon）时 OnSetup 会再次调用，但 Runtime 复用同一个实例。
            // 必须清空上一轮的节点池与「已构建」标记，否则会复用原生指针已释放的节点。
            for (var i = 0; i < this.rows.Length; i++) this.rows[i] = null!;
            for (var i = 0; i < this.filterButtons.Length; i++) this.filterButtons[i] = null;
            for (var i = 0; i < this.headers.Length; i++) this.headers[i] = null;
            this.fixedRoot = null; this.scroll = null; this.content = null; this.attachParent = null;
            this.title = this.status = this.mapHint = null;
            this.bPrev = this.bNext = this.bClear = this.bClose = null;
            this.lastViewH = this.lastScrollW = -1f;

            // ① 固定层：标题 / 筛选 / 分页 / 表头，全部不随列表滚动
            this.fixedRoot = new ResNode { Position = p, Size = new Vector2(CW, H) };
            this.fixedRoot.AttachNode(this);

            // ② 滚动区：与主窗同样的 ScrollingNode<ResNode>
            this.scroll = new ScrollingNode<ResNode>
            {
                Position = p,
                Size = new Vector2(CW, H - p.Y - BottomPad),
                AutoHideScrollBar = true,
                ScrollSpeed = 32,
            };
            this.scroll.AttachNode(this);
            this.scroll.ScrollBarNode.IsAcceptingMouseWheelEvents = true;
            this.content = this.scroll.ContentNode;
            this.attachParent = this.fixedRoot;   // 以下 B()/T() 全挂固定层

            this.title = this.T(p, 320f, 22f, 14, "S 怪历史记录");

            // 筛选组：互斥单选，复用主窗大区/版本那套 CompactRadio（纯文字、不折行、选中琥珀金）
            for (var i = 0; i < Filters.Length; i++)
            {
                var f = Filters[i].Filter;
                this.filterButtons[i] = this.B(p, 96f, 22f, Filters[i].Label,
                    () => this.actions.SetFilter(f));
                NativeButtonStyle.CompactRadio(this.filterButtons[i]!, Filters[i].Label, 22f, false);
            }

            this.bPrev = this.B(p, 96f, 24f, "◀ 上一页", () => this.actions.PageBy(-1));
            this.bNext = this.B(p, 96f, 24f, "下一页 ▶", () => this.actions.PageBy(1));
            this.bClear = this.B(p, 130f, 26f, "清空历史", () => this.TryClear());
            this.bClose = this.B(p, 80f, 26f, "关闭", () => this.actions.Closed());

            // 表头：时间 | 怪名 | 服务器 | 事件 | 来源（TextNode，与数据列共用同一组 X 常量）
            string[] names = { "时间", "怪名", "服务器", "事件", "来源" };
            float[] xs = { TimeX, NameX, ServerX, EventX, SourceX };
            float[] ws = { TimeW, NameW, ServerW, EventW, SourceW };
            for (var i = 0; i < names.Length; i++)
            {
                this.headers[i] = this.T(p, ws[i], HeaderH, 12, names[i]);
                this.headers[i]!.TextColor = MutedText;
            }

            this.status = this.T(p, CW - 24f, 18f, 11, "");
            this.status!.TextColor = MutedText;
            this.mapHint = this.T(p, CW - 24f, 18f, 11, "");
            this.mapHint!.TextColor = MutedText;

            // ③ 行节点池：固定 24 行，预建隐藏，由 Apply 按当前页数据打开。
            //    绝不在 Apply 里懒创建（原生指针未注册时写 String 会 NRE，表现为整列空白）。
            this.attachParent = null;          // 从此处起挂 content（可滚动）
            for (var i = 0; i < RowPool; i++) this.rows[i] = this.MakeRow(i);

            this.setupDone = true;
        }
        catch (Exception ex)
        {
            // 带纹理组件在国服可能构造失败：吞掉并保持 setupDone=false，
            // Apply 逐帧跳过，绝不让异常冒泡到 TickHistory 把整个原生 UI 停用。
            this.setupDone = false;
            Log("[原生UI] 历史窗口节点初始化失败，本窗暂不刷新：" + ex.Message);
        }
    }

    private RowNodes MakeRow(int idx)
    {
        var p = this.ContentStartPosition;
        var r = new RowNodes
        {
            // ColorImageNode 才有绘制图元；条纹色构造时定一次，Apply 只改几何与可见性。
            Bg = new ColorImageNode
            {
                Position = p,
                Size = new Vector2(CW - ScrollBarTrackW - 4f, RowH - 2f),
                Color = idx % 2 == 0 ? StripeA : StripeB,
            },
            Time = new TextNode
            { Position = p, Size = new Vector2(TimeW, 20f), FontSize = 12, FontType = FontType.Axis, TextColor = ListText },
            Monster = new TextNode
            { Position = p, Size = new Vector2(NameW, 20f), FontSize = 12, FontType = FontType.Axis, TextColor = ListText },
            Server = new TextNode
            { Position = p, Size = new Vector2(ServerW, 20f), FontSize = 12, FontType = FontType.Axis, TextColor = ListText },
            Event = new TextNode
            { Position = p, Size = new Vector2(EventW, 20f), FontSize = 12, FontType = FontType.Axis, TextColor = ListText },
            Source = new TextNode
            { Position = p, Size = new Vector2(SourceW, 20f), FontSize = 12, FontType = FontType.Axis, TextColor = ListText },
        };
        r.Bg!.AttachNode(this.content);
        r.Time!.AttachNode(this.content);
        r.Monster!.AttachNode(this.content);
        r.Server!.AttachNode(this.content);
        r.Event!.AttachNode(this.content);
        r.Source!.AttachNode(this.content);
        Show(r, false);
        return r;
    }

    private TextNode T(Vector2 pos, float w, float h, uint fontSize, string value)
    {
        var n = new TextNode
        {
            Position = pos,
            Size = new Vector2(w, h),
            FontSize = fontSize,
            String = value,          // TextNode.String 不能为 null
        };
        n.AttachNode(this.attachParent ?? this.content);
        return n;
    }

    private TextButtonNode B(Vector2 pos, float w, float h, string label, Action onClick)
    {
        var b = new TextButtonNode
        {
            Position = pos,
            Size = new Vector2(w, h),
            String = label,
            OnClick = onClick,       // 不注册 MouseDown / MouseClick，避免吞掉 OnClick
        };
        NativeButtonStyle.Prepare(b);
        NativeButtonStyle.InitHover(b);
        b.AttachNode(this.attachParent ?? this.content);
        return b;
    }

    private static void Show(RowNodes r, bool v)
    {
        if (r.Bg is not null) r.Bg.IsVisible = v;
        if (r.Time is not null) r.Time.IsVisible = v;
        if (r.Monster is not null) r.Monster.IsVisible = v;
        if (r.Server is not null) r.Server.IsVisible = v;
        if (r.Event is not null) r.Event.IsVisible = v;
        if (r.Source is not null) r.Source.IsVisible = v;
    }

    // ───────────────────── 交互 ─────────────────────

    /// <summary>[清空历史] 二次确认：第一次点只把标签改成「再点一次确认」，5 秒内再点才真清。</summary>
    private void TryClear()
    {
        if (!this.clearArmed || (DateTime.UtcNow - this.clearArmedAtUtc).TotalSeconds > 5)
        {
            this.clearArmed = true;
            this.clearArmedAtUtc = DateTime.UtcNow;
            return;
        }
        this.clearArmed = false;
        this.actions.Clear();
    }

    // ───────────────────── Apply：每 200ms 由框架主线程调用 ─────────────────────

    internal void Apply(HistorySnapshot s)
    {
        // v1.2.10 铁律：节点尚未 OnSetup / 已被 OnFinalize 释放时跳过本帧，下一帧重试。
        if (!this.setupDone || this.fixedRoot is null || this.scroll is null ||
            this.content is null || this.rows[0] is null) return;
        try
        {
            var p = this.ContentStartPosition;
            var scrollW = W - p.X;
            Vector2 At(float x, float y) => new(x, y);
            float fy = 0f;

            // ① 标题行 + 右侧 [清空历史] [关闭]
            if (this.title is not null) this.title.Position = At(TopInset, fy);
            if (this.bClose is not null)
            {
                NativeButtonStyle.Apply(this.bClose, "关闭", 80f, 26f, false);
                this.bClose.Position = At(CW - this.bClose.Width, fy);
            }
            if (this.bClear is not null)
            {
                if (this.clearArmed && (DateTime.UtcNow - this.clearArmedAtUtc).TotalSeconds > 5)
                    this.clearArmed = false;
                NativeButtonStyle.Apply(this.bClear,
                    this.clearArmed ? "再点一次确认" : "清空历史", 130f, 26f, this.clearArmed);
                this.bClear.Position = this.bClose is not null
                    ? At(CW - this.bClose.Width - this.bClear.Width - 8f, fy)
                    : At(CW - this.bClear.Width, fy);
            }
            fy += 26f + 8f;

            // ② 筛选行：5 个互斥 CompactRadio
            var fx = TopInset;
            for (var i = 0; i < Filters.Length; i++)
            {
                var b = this.filterButtons[i];
                if (b is null) continue;
                NativeButtonStyle.CompactRadio(b, Filters[i].Label, 22f, s.Filter == Filters[i].Filter);
                b.Position = At(fx, fy);
                fx += b.Width + 10f;
            }
            fy += 22f + 8f;

            // ③ 分页 + 状态
            if (this.bPrev is not null)
            {
                NativeButtonStyle.Apply(this.bPrev, "◀ 上一页", 96f, 24f, false);
                this.bPrev.Position = At(TopInset, fy);
                this.bPrev.IsVisible = s.Page > 0;
            }
            if (this.bNext is not null)
            {
                NativeButtonStyle.Apply(this.bNext, "下一页 ▶", 96f, 24f, false);
                this.bNext.Position = At(TopInset + 102f, fy);
                this.bNext.IsVisible = s.Page + 1 < s.PageCount;
            }
            if (this.status is not null)
            {
                this.status.String = s.Status.Length > 0 ? s.Status : " ";
                this.status.Position = At(TopInset + 210f, fy + 3f);
            }
            fy += 24f + 8f;

            // ④ 表头 + 底部地图提示
            float[] xs = { TimeX, NameX, ServerX, EventX, SourceX };
            for (var i = 0; i < this.headers.Length; i++)
                if (this.headers[i] is not null) this.headers[i]!.Position = At(xs[i], fy);
            var fixedH = fy + HeaderH + 4f;

            if (this.mapHint is not null)
            {
                var first = s.Rows.Count > 0 ? s.Rows[0] : null;
                this.mapHint.String = first is null
                    ? " "
                    : $"最近：{first.MonsterName} · {first.MapName}";
                this.mapHint.Position = At(TopInset, H - p.Y - 22f);
            }

            // ⑤ 行：按当前页数据填充，多余节点隐藏
            var rowW = scrollW - ScrollBarTrackW - 4f;
            for (var i = 0; i < this.rows.Length; i++)
            {
                var r = this.rows[i];
                if (r is null) continue;
                if (i >= s.Rows.Count) { Show(r, false); continue; }

                var rec = s.Rows[i];
                var y = i * RowH;
                r.Bg!.Position = At(0f, y);
                r.Bg.Size = new Vector2(rowW, RowH - 2f);

                r.Time!.String = FormatTime(rec.EventTime);
                r.Time.Position = At(TimeX, y + 2f);
                r.Monster!.String = rec.MonsterName;
                r.Monster.Position = At(NameX, y + 2f);
                r.Server!.String = rec.ServerName;
                r.Server.Position = At(ServerX, y + 2f);

                // 击杀暗红 / 出生绿；Sonar 金色 / 主站灰
                var isKill = rec.EventType == HistoryEventType.Kill;
                r.Event!.String = isKill ? "击杀" : "出生";
                r.Event.TextColor = isKill ? KillRed : SpawnGreen;
                r.Event.Position = At(EventX, y + 2f);

                var isSonar = rec.Source == HistorySource.Sonar;
                r.Source!.String = isSonar ? "Sonar" : "主站";
                r.Source.TextColor = isSonar ? SonarGold : SiteGray;
                r.Source.Position = At(SourceX, y + 2f);

                Show(r, true);
            }

            this.Reflow(p, scrollW, fixedH, s.Rows.Count * RowH + BottomPad);
        }
        catch (Exception ex)
        {
            Log("[原生UI] 历史窗口 Apply 失败：" + ex.GetType().Name);
        }
    }

    private static string FormatTime(double unixSeconds)
    {
        try
        {
            if (unixSeconds <= 0) return "--";
            return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds)
                .ToLocalTime().ToString("MM-dd HH:mm:ss");
        }
        catch { return "--"; }
    }

    /// <summary>固定视口 + 裁剪区 + 滚动条对齐（照抄主窗 Reflow 的已验证写法）。</summary>
    private void Reflow(Vector2 p, float scrollW, float fixedH, float listContentH)
    {
        var viewH = H - p.Y - fixedH - BottomPad - 22f;   // 再留 22 给底部地图提示
        if (viewH < 120f) viewH = 120f;

        this.fixedRoot!.Size = new Vector2(CW, fixedH);
        this.scroll!.Position = new Vector2(p.X, p.Y + fixedH);
        if (MathF.Abs(viewH - this.lastViewH) >= 0.5f ||
            MathF.Abs(scrollW - this.lastScrollW) >= 0.5f)
        {
            this.lastViewH = viewH;
            this.lastScrollW = scrollW;
            this.scroll.Size = new Vector2(scrollW, viewH);
        }
        // 行少时 ContentNode 也要撑满视口，否则下半部空白且滚动参数算错。
        this.content!.Height = MathF.Max(listContentH, viewH);

        var clipSize = new Vector2(scrollW, viewH);
        this.scroll.ClippingContentNode.Size = clipSize;
        this.scroll.ScrollingCollisionNode.Size = clipSize;

        var sb = this.scroll.ScrollBarNode;
        sb.Position = new Vector2(scrollW - ScrollBarTrackW, 0f);
        sb.Size = new Vector2(ScrollBarTrackW, viewH);
        sb.IsAcceptingMouseWheelEvents = true;
        sb.UpdateScrollParams();
    }

    // ───────────────────── 生命周期 ─────────────────────

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        base.OnHide(addon);
        // 过图 hide 如实回写；程序内主动 Close() 由 Runtime 置 SuppressClosed 屏蔽。
        if (!this.SuppressClosed) this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        for (var i = 0; i < this.rows.Length; i++) this.rows[i] = null!;
        for (var i = 0; i < this.filterButtons.Length; i++) this.filterButtons[i] = null;
        for (var i = 0; i < this.headers.Length; i++) this.headers[i] = null;
        this.fixedRoot = null; this.scroll = null; this.content = null; this.attachParent = null;
        this.title = this.status = this.mapHint = null;
        this.bPrev = this.bNext = this.bClear = this.bClose = null;
        this.setupDone = false;   // 节点已释放，下一帧 Apply 必须等重新 OnSetup
    }
}
