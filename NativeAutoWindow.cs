using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.1.72：把「自动前往」窗口也换成游戏原生窗口（KamiToolKit）。
// v1.1.73：紧凑排版。原来的固定 660×720 + 固定 Y 坐标会留大量空白：
//   ① 命中提示条默认隐藏却占 46px；② 服务器 chips 固定 2 行；
//   ③ 排队固定 6 行；④ S 怪列表固定 8 行，且「先竖着排」使右列常年整列空；
//   ⑤ 底部空文本节点照样占位。
// 现在改为「内容驱动布局」：Reflow 每次 Apply 按实际可见条数算 Y 游标，
// 逐块重排节点位置，并把窗口高度收缩到 内容底 + 内边距（仅高度变化 ≥2px 才 Resize，
// 避免 200ms 同步造成抖动）。S 怪改为横向填充两列：先填满每行左右，再换行。
//
// 和 NativeHud.cs 的「触发中」窗口共用一个 KTK 初始化（KamiToolKitLibrary 是全局静态，
// 只能 InitializeAsync / Dispose 一次），由 NativeHudRuntime 统一 Tick 与卸载。
//
// 设计要点：
//   ① 原生窗口只认「纯数据快照 + 委托」，快照里没有任何 KTK 类型，Plugin 侧不需要引用 KTK。
//   ② 动态内容（服务器 chips / 排队 / S 怪列表）用固定节点池，按 IsVisible + String 复用，
//      不每帧销毁重建节点；S 怪列表分页，避免一次挂几百个节点。
//   ③ 可点击项一律用 TextButtonNode（v1.1.71 已在本机 KTK 上验证过）。
//      刻意不用 CheckboxNode：它的 IsChecked 写入会触发 OnClick，做双向同步容易自激。
//   ④ 任一步异常由 NativeHudRuntime 置 autoFailed，DrawAutoWindow 自动回退原 ImGui 窗口。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>「自动前往」窗口一行的纯数据。</summary>
internal sealed class AutoHuntRow
{
    internal string Key = "";      // 怪的中文名，点击回调用它定位
    internal string Label = "";    // 行标题
    internal string Right = "";    // 右侧状态
    internal bool Checked;
}

/// <summary>「自动前往」窗口的纯数据快照（每 200ms 同步一次）。</summary>
internal sealed class AutoWindowSnapshot
{
    internal bool Ready;
    internal bool ShowHitBar;
    internal string Hit1 = "";
    internal string Hit2 = "";

    internal bool StyleGroup;
    internal bool StyleCard;
    internal bool OnlyOpen;
    internal bool AllWorlds;
    internal bool HudOpen;
    internal string WorldCaption = "";

    internal readonly List<string> Worlds = new();
    internal readonly List<bool> WorldOn = new();

    internal string QueueTitle = "";
    internal readonly List<string> QueueLines = new();

    internal readonly List<AutoHuntRow> Hunts = new();
    internal int Page;
    internal int PageCount;

    internal string Hints = "";
    internal string RunLabel = "";
    internal string Counts = "";
    internal string Status = "";
    internal string Skipped = "";
}

/// <summary>原生窗口按钮回调用的委托集合，同样不含任何 KTK 类型。</summary>
internal sealed class AutoWindowActions
{
    internal Action GroupStyle = () => { };
    internal Action CardStyle = () => { };
    internal Action ToggleOnlyOpen = () => { };
    internal Action ToggleAllWorlds = () => { };
    internal Action ToggleHud = () => { };
    internal Action SelectAllWorlds = () => { };
    internal Action ClearWorlds = () => { };
    internal Action<string> ToggleWorld = _ => { };
    internal Action<string> ToggleHunt = _ => { };
    internal Action ClearHit = () => { };
    internal Action PrevPage = () => { };
    internal Action NextPage = () => { };
    internal Action StartStop = () => { };
    internal Action Closed = () => { };
}

/// <summary>
/// 原生「自动前往」窗口：标准游戏标题栏 + 一排工具按钮 + 服务器 chips +
/// 排队列表 + 可分页的 S 怪选择区 + 底部开始/停止。布局由 Reflow 动态计算。
/// </summary>
internal sealed class AutoWindow : NativeAddon
{
    internal const float W = 660f;
    internal const float DefaultH = 430f;
    private const float CW = 636f;          // 内容区可用宽度（窗口宽 - 背景/内边距）
    private const float MinH = 300f;
    private const float MaxH = 820f;
    private const float BottomPad = 8f;
    private const float ViewportMaxH = 560f;   // 内容超过这个高度就用原生滑块滚，而不是继续拉长窗口
    private const float ScrollBarW = 12f;
    private const float MinWindowW = 380f;

    // v1.1.95：次要说明小字与 1px 分割线配色
    private static readonly Vector4 MutedText = new(0.52f, 0.56f, 0.62f, 1f);
    private static readonly Vector4 DividerColor = new(0.24f, 0.26f, 0.30f, 1f);

    private const int ChipCount = 16;        // 最多 2 行 × 8
    private const int ChipPerRow = 8;
    private const float ChipW = 76f;
    private const float ChipPitch = 80f;
    private const float ChipRowH = 26f;

    private const int QueueLines = 6;
    private const int RowCount = 16;        // 2 列 × 8 行 / 页
    internal const int PageSize = RowCount;
    private const float HuntColPitch = 320f;
    private const float HuntBtnW = 212f;
    private const float HuntStateX = 216f;
    private const float HuntRowH = 27f;

    // 工具行：分组 / 卡片 / 只看开闸 / 触发悬浮窗 / 全部服务器 / 全选服 / 清空服
    private static readonly float[] ToolW = { 62f, 62f, 92f, 106f, 104f, 68f, 68f };
    private const float ToolGap = 8f;
    private const int AlwaysTools = 5;

    private readonly AutoWindowActions actions;
    private readonly string[] worldNames = new string[ChipCount];
    private readonly string[] rowKeys = new string[RowCount];
    private readonly TextButtonNode?[] tools = new TextButtonNode?[7];

    private bool setupDone;
    private float lastHeight = DefaultH;
    private float lastViewH;
    private readonly List<NodeBase> allNodes = new();
    private float lastW = W;
    private float lastScrollW;
    private ScrollingNode<ResNode>? scroll;
    private ResNode? content;
    private TextNode? hit1, hit2, worldCaption, queueTitle, pageLabel, hints, counts, status, skipped;
    private TextButtonNode? clearHit, bPrev, bNext, bRun;
    private ColorImageNode? sepLine;      // v1.1.95：工具行与说明之间 1px 深灰分割线
    private readonly TextButtonNode?[] chips = new TextButtonNode?[ChipCount];
    private readonly TextNode?[] queueLines = new TextNode?[QueueLines];
    private readonly TextButtonNode?[] huntButtons = new TextButtonNode?[RowCount];
    private readonly TextNode?[] huntStates = new TextNode?[RowCount];

    internal AutoWindow(AutoWindowActions actions) => this.actions = actions;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        SetWindowSize(new Vector2(W, DefaultH));
        var p = ContentStartPosition;

        // 所有内容挂进原生滚动容器：内容超过 ViewportMaxH 时窗口不再拉长，右侧出滑块。
        // 容器原点就放在内容原点，所以下面 Reflow 的坐标全部是「相对容器」，无需再加 p。
        this.scroll = new ScrollingNode<ResNode>
        {
            Position = p,
            Size = new Vector2(CW, DefaultH - p.Y - BottomPad),
            AutoHideScrollBar = true,
            ScrollSpeed = 32,
        };
        this.scroll.AttachNode(this);
        this.content = this.scroll.ContentNode;

        // 顶部命中提示条（默认隐藏，不占高度）
        this.hit1 = this.T(p, 480f, 20f, 14, "");
        this.hit2 = this.T(p, 480f, 16f, 11, "");
        this.clearHit = this.B(p, 150f, 26f, "知道了，清掉提示",
            () => this.actions.ClearHit());

        // 一行 7 个工具按钮（后两个只在逐服勾选模式显示）
        this.tools[0] = this.B(p, ToolW[0], 24f, "分组", () => this.actions.GroupStyle());
        this.tools[1] = this.B(p, ToolW[1], 24f, "卡片", () => this.actions.CardStyle());
        this.tools[2] = this.B(p, ToolW[2], 24f, "只看开闸", () => this.actions.ToggleOnlyOpen());
        this.tools[3] = this.B(p, ToolW[3], 24f, "触发悬浮窗", () => this.actions.ToggleHud());
        this.tools[4] = this.B(p, ToolW[4], 24f, "全部服务器", () => this.actions.ToggleAllWorlds());
        this.tools[5] = this.B(p, ToolW[5], 24f, "全选服",
            () => this.actions.SelectAllWorlds());
        this.tools[6] = this.B(p, ToolW[6], 24f, "清空服",
            () => this.actions.ClearWorlds());

        // v1.1.95：1px 深灰水平分割线（ColorImageNode 纯色，零纹理依赖）
        //   ★ Color 是 new 隐藏属性，静态类型必须是 ColorImageNode 才会写 AddColor；
        //     不设 TexturePath → 空 Part 的 LoadTexture("") 直接 return，不加载任何游戏 .tex。
        this.sepLine = new ColorImageNode
        {
            Position = p,
            Size = new Vector2(CW, 1f),
            Color = DividerColor,
        };
        this.allNodes.Add(this.sepLine);       // 参与 Reflow 的窗口宽度统计
        this.sepLine.AttachNode(this.content);

        this.worldCaption = this.T(p, CW, 16f, 11, "");
        this.worldCaption.TextColor = MutedText;

        for (var i = 0; i < ChipCount; i++)
        {
            var idx = i;
            this.chips[i] = this.B(p, ChipW, 24f, "", () => this.ClickWorld(idx));
        }

        this.queueTitle = this.T(p, CW, 18f, 12, "");
        this.queueTitle.TextColor = MutedText;
        for (var i = 0; i < QueueLines; i++)
            this.queueLines[i] = this.T(p, CW - 6f, 16f, 11, "");

        this.pageLabel = this.T(p, CW - 190f, 20f, 12, "");
        this.bPrev = this.B(p, 90f, 24f, "← 上一页", () => this.actions.PrevPage());
        this.bNext = this.B(p, 90f, 24f, "下一页 →", () => this.actions.NextPage());

        for (var i = 0; i < RowCount; i++)
        {
            var idx = i;
            this.huntButtons[i] = this.B(p, HuntBtnW, 24f, "", () => this.ClickHunt(idx));
            this.huntStates[i] = this.T(p, 100f, 18f, 10, "");
            this.huntStates[i]!.TextColor = MutedText;   // 与怪名拉开层次，不再融为一体
        }

        this.hints = this.T(p, CW, 16f, 10, "");
        this.hints.TextColor = MutedText;
        this.bRun = this.B(p, 160f, 28f, "开始自动前往", () => this.actions.StartStop());
        this.counts = this.T(p, CW - 170f, 20f, 11, "");
        this.status = this.T(p, CW, 18f, 11, "");
        this.skipped = this.T(p, CW, 16f, 10, "");

        this.setupDone = true;
    }

    private TextNode T(Vector2 pos, float w, float h, uint fontSize, string value)
    {
        var n = new TextNode
        {
            Position = pos,
            Size = new Vector2(w, h),
            FontSize = fontSize,
            String = value,
        };
        this.allNodes.Add(n);
        n.AttachNode(this.content);
        return n;
    }

    private TextButtonNode B(Vector2 pos, float w, float h, string label, Action onClick)
    {
        var b = new TextButtonNode
        {
            Position = pos,
            Size = new Vector2(w, h),
            String = label,
            OnClick = onClick,
        };
        NativeButtonStyle.Prepare(b);
        NativeButtonStyle.InitHover(b);     // v1.1.99：全局 Hover 统一拦截
        this.allNodes.Add(b);
        b.AttachNode(this.content);
        return b;
    }

    private void ClickWorld(int i)
    {
        var name = this.worldNames[i];
        if (name.Length > 0) this.actions.ToggleWorld(name);
    }

    private void ClickHunt(int i)
    {
        var key = this.rowKeys[i];
        if (key.Length > 0) this.actions.ToggleHunt(key);
    }

    /// <summary>把快照刷到节点上。所有写入都在游戏主线程（IFramework.Update）执行。</summary>
    internal void Apply(AutoWindowSnapshot s)
    {
        if (!this.setupDone) return;

        NativeButtonStyle.FitText(this.hit1!, s.ShowHitBar ? s.Hit1 : "", 480f);
        NativeButtonStyle.FitText(this.hit2!, s.ShowHitBar ? s.Hit2 : "", 480f);
        NativeButtonStyle.Apply(this.clearHit!, "知道了，清掉提示", 190f, 26f, false);

        // v1.1.95：开关类按钮统一「纯文字色」——选中琥珀金 / 未选中暗灰、去掉 ✓ 圆点与边框；
        //   动作类（全选服 / 清空服）保留普通按钮，避免看起来像开关。
        var toolLabels = new[] { "分组", "卡片", "只看开闸", "触发悬浮窗", "全部服务器", "全选服", "清空服" };
        var toolOn = new[] { s.StyleGroup, s.StyleCard, s.OnlyOpen, s.HudOpen, s.AllWorlds, false, false };
        for (var i = 0; i < this.tools.Length; i++)
        {
            if (i < AlwaysTools)
                NativeButtonStyle.PlainRadio(this.tools[i]!, toolLabels[i], 150f, 24f,
                                             toolOn[i], dimOff: true);
            else
                NativeButtonStyle.Apply(this.tools[i]!, toolLabels[i], 150f, 24f, false);
        }

        NativeButtonStyle.FitText(this.worldCaption!, s.WorldCaption, CW);

        var showChips = s.Ready && !s.AllWorlds;
        for (var i = 0; i < ChipCount; i++)
        {
            var name = i < s.Worlds.Count ? s.Worlds[i] : "";
            this.worldNames[i] = name;
            var on = i < s.WorldOn.Count && s.WorldOn[i];
            NativeButtonStyle.Radio(this.chips[i]!, (on ? "● " : "○ ") + name, 118f, 24f, on);
        }

        NativeButtonStyle.FitText(this.queueTitle!, s.QueueTitle, 300f);
        for (var i = 0; i < QueueLines; i++)
            NativeButtonStyle.FitText(this.queueLines[i]!, i < s.QueueLines.Count ? s.QueueLines[i] : "", CW);

        NativeButtonStyle.FitText(this.pageLabel!,
            $"选择要触发的 S · 共 {s.Hunts.Count} 只 · 第 {s.Page + 1}/{Math.Max(1, s.PageCount)} 页", 360f);

        var start = s.Page * PageSize;
        for (var i = 0; i < RowCount; i++)
        {
            var r = start + i < s.Hunts.Count ? s.Hunts[start + i] : null;
            this.rowKeys[i] = r?.Key ?? "";
            NativeButtonStyle.Apply(this.huntButtons[i]!,
                r is null ? "" : (r.Checked ? "✓ " : "□ ") + r.Label,
                200f, 24f, r is not null && r.Checked);
            NativeButtonStyle.FitText(this.huntStates[i]!, r?.Right ?? "", 140f);
        }

        NativeButtonStyle.Apply(this.bPrev!, "← 上一页", 120f, 24f, false);
        NativeButtonStyle.Apply(this.bNext!, "下一页 →", 120f, 24f, false);
        NativeButtonStyle.FitText(this.hints!, s.Hints, CW);
        // v1.1.95：未选怪 → 暗灰禁用；已选 / 继续 / 停止 → 亮字 + 琥珀底
        var runEnabled = s.RunLabel.Length > 0 && s.RunLabel != "开始自动前往";
        if (!runEnabled)
            foreach (var h in s.Hunts) if (h.Checked) { runEnabled = true; break; }
        NativeButtonStyle.RunButton(this.bRun!, s.RunLabel, 210f, 28f, runEnabled);
        NativeButtonStyle.FitText(this.counts!, s.Counts, 320f);
        NativeButtonStyle.FitText(this.status!, s.Status, CW);
        NativeButtonStyle.FitText(this.skipped!, s.Skipped, CW);

        this.Reflow(s, showChips);
    }

    /// <summary>
    /// v1.1.73 紧凑排版核心：按实际可见条数从上往下排，隐藏块不占高度，
    /// 最后把窗口高度收缩到内容底部。仅当高度变化 ≥2px 才 Resize，避免抖动。
    /// </summary>
    private void Reflow(AutoWindowSnapshot s, bool showChips)
    {
        var p = this.ContentStartPosition;     // 只用来算窗口总高；节点坐标相对滚动容器
        float y = 0f;

        Vector2 At(float x, float dy) => new Vector2(x, dy);

        // ① 命中提示：不出现就完全不占高度
        var hit = s.ShowHitBar;
        this.hit1!.IsVisible = hit;
        this.hit2!.IsVisible = hit;
        this.clearHit!.IsVisible = hit;
        if (hit)
        {
            this.hit1.Position = At(0f, y);
            this.hit2.Position = At(0f, y + 19f);
            this.clearHit!.Position = At(this.hit1!.Width + 10f, y);
            y += MathF.Max(32f, this.clearHit.Height + 4f);
        }

        // ② 工具行：按每个按钮的实际宽度依次排，短按钮不再留空
        float tx = 0f;
        var toolH = 24f;
        for (var i = 0; i < this.tools.Length; i++)
        {
            var b = this.tools[i]!;
            b.IsVisible = i < AlwaysTools || showChips;
            if (!b.IsVisible) continue;
            b.Position = At(tx, y);
            tx += b.Width + ToolGap;
            toolH = MathF.Max(toolH, b.Height);
        }
        y += toolH + 6f;

        // ②b v1.1.95：工具行与下方说明之间的 1px 深灰分割线
        this.sepLine!.Position = At(0f, y);
        this.sepLine.Size = new Vector2(CW, 1f);
        y += 1f + 10f;

        // ③ 服务器说明（常显示，一行）
        this.worldCaption!.Position = At(0f, y);
        y += 17f;

        // ④ 服务器 chips：按实际宽度横向流式排列，放不下才换行
        var chipVisible = 0;
        var cx = 0f;
        var cy = y;
        var chipRowH = 0f;
        for (var i = 0; i < ChipCount; i++)
        {
            var chip = this.chips[i]!;
            var vis = showChips && i < s.Worlds.Count && s.Worlds[i].Length > 0;
            chip.IsVisible = vis;
            if (!vis) continue;
            if (cx + chip.Width > CW && cx > 0f)
            {
                cy += chipRowH + 2f;
                cx = 0f;
                chipRowH = 0f;
            }
            chip.Position = At(cx, cy);
            cx += chip.Width + 4f;
            chipRowH = MathF.Max(chipRowH, chip.Height);
            chipVisible++;
        }
        if (chipVisible > 0) y = cy + chipRowH + 2f;

        // ⑤ 排队标题 + 实际行数
        this.queueTitle!.Position = At(0f, y);
        y += 19f;
        var qVisible = 0;
        for (var i = 0; i < QueueLines; i++)
        {
            var line = this.queueLines[i]!;
            var vis = i < s.QueueLines.Count && s.QueueLines[i].Length > 0;
            line.IsVisible = vis;
            if (!vis) continue;
            line.Position = At(6f, y + qVisible * 16f);
            qVisible++;
        }
        y += qVisible * 16f + 6f;

        // ⑥ S 怪列表标题 / 分页
        this.pageLabel!.Position = At(0f, y);
        var px2 = this.pageLabel.Width + 10f;
        this.bPrev!.Position = At(px2, y - 2f);
        this.bNext!.Position = At(px2 + this.bPrev.Width + 6f, y - 2f);
        this.bPrev.IsVisible = s.Page > 0;
        this.bNext.IsVisible = s.Page + 1 < s.PageCount;
        y += 27f;

        // ⑦ S 怪行：横向填充两列（第 0 只左、第 1 只右、第 2 只换行左…）
        //    比原来的「先竖着排 8 个再排右列」少掉整列空白。
        var start = s.Page * PageSize;
        var huntVisible = 0;
        var huntRowH = HuntRowH;
        for (var i = 0; i < RowCount; i++)
        {
            var r = start + i < s.Hunts.Count ? s.Hunts[start + i] : null;
            var btn = this.huntButtons[i]!;
            var st = this.huntStates[i]!;
            btn.IsVisible = r is not null;
            st.IsVisible = r is not null;
            if (r is null) continue;
            huntRowH = MathF.Max(huntRowH, btn.Height + 3f);   // 折行的按钮会加高，整行跟着加高
            huntVisible++;
        }
        // 两列间距按本页最宽的一行收紧，不再固定 320 留空白
        var huntPitch = 210f;
        for (var i = 0; i < RowCount; i++)
        {
            var btn = this.huntButtons[i]!;
            if (!btn.IsVisible) continue;
            huntPitch = MathF.Max(huntPitch, btn.Width + this.huntStates[i]!.Width + 14f);
        }
        huntPitch += 20f;                  // v1.1.95：左右两列之间再加 20px 呼吸
        var placed = 0;
        for (var i = 0; i < RowCount; i++)
        {
            var btn = this.huntButtons[i]!;
            var st = this.huntStates[i]!;
            if (!btn.IsVisible) continue;
            var col = placed % 2;
            var row = placed / 2;
            var x = col * huntPitch;
            var ry = y + row * huntRowH;
            btn.Position = At(x, ry);
            st.Position = At(x + btn.Width + 10f, ry + 2f);     // v1.1.95：与怪名拉开 + 调暗
            placed++;
        }
        y += Math.Max(1, (huntVisible + 1) / 2) * huntRowH + 6f;

        // ⑧ 底部：主按钮在上，精简说明在下（v1.1.95）
        this.bRun!.Position = At(0f, y);
        this.counts!.Position = At(this.bRun.Width + 10f, y + 4f);
        y += MathF.Max(30f, this.bRun.Height + 2f) + 2f;

        if (s.Hints.Length > 0)
        {
            this.hints!.IsVisible = true;
            this.hints.Position = At(0f, y);
            y += 17f;
        }
        else this.hints!.IsVisible = false;

        if (s.Status.Length > 0)
        {
            this.status!.IsVisible = true;
            this.status.Position = At(0f, y);
            y += 18f;
        }
        else this.status!.IsVisible = false;

        if (s.Skipped.Length > 0)
        {
            this.skipped!.IsVisible = true;
            this.skipped.Position = At(0f, y);
            y += 17f;
        }
        else this.skipped!.IsVisible = false;

        // 窗口宽度也随内容收缩：统计所有可见节点右边界
        var maxRight = 0f;
        foreach (var n in this.allNodes)
            if (n.IsVisible) maxRight = MathF.Max(maxRight, n.X + n.Width);
        var wantW = Math.Clamp(maxRight + ScrollBarW + 20f, MinWindowW, W);

        // 窗口高度贴合内容，但最多到 ViewportMaxH；超出的内容由滚动容器裁剪 + 滑块浏览
        var contentH = y + BottomPad;
        var viewH = MathF.Min(contentH, ViewportMaxH);
        var h = Math.Clamp(p.Y + viewH + BottomPad, MinH, MaxH);
        if (MathF.Abs(h - this.lastHeight) >= 2f || MathF.Abs(wantW - this.lastW) >= 8f)
        {
            if (MathF.Abs(wantW - this.lastW) >= 8f) this.lastW = wantW;
            this.lastHeight = h;
            this.Size = new Vector2(this.lastW, h);
        }
        var scrollW = this.lastW - p.X * 2f;
        this.content!.Height = contentH;
        if (MathF.Abs(viewH - this.lastViewH) >= 2f || MathF.Abs(scrollW - this.lastScrollW) >= 2f)
        {
            this.lastViewH = viewH;
            this.lastScrollW = scrollW;
            this.scroll!.Size = new Vector2(scrollW, viewH);
        }
        this.scroll!.ScrollBarNode.UpdateScrollParams();
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        base.OnHide(addon);
        // 用户点原生窗口的 X 时把开关同步回去；主动 Close() 时 autoRunOpen 本来就是 false。
        this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        this.setupDone = false;
    }
}
