using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.1.75：三个原生窗口共用的一套按钮规则。
//   ① 宽度贴合文字：短按钮不再按固定宽度留大片空白；
//   ② 文字太长就换行：超过 maxW 时按可用宽度折行，按钮自动加高；
//   ③ 选中态变色：选中按钮改底色 + 字色，不再只靠 ✓ / ● 前缀区分。
//
// 不使用 ButtonBase.IsChecked —— KTK 里写它会触发 OnClick（CheckboxNode 源码
// 明确注明），做双向同步会自激。这里直接写 ResNode->ColorVector / TextColor，
// 不派发任何事件；原始颜色按按钮缓存在 ConditionalWeakTable 里，取消选中时还原，
// 这样亮色/暗色主题都能保持原来的字色。
//
// 宽度下限来自 KTK 的 TextButtonNode：LabelNode.Position.X = 16，
// LabelNode.Size.Width = Width - 32，即按钮左右各 16 像素是九宫格边框，
// 所以「文字宽 + 36」是既贴文字又不裁字的最小尺寸。
//
// ★ v1.2.00：可见文字改由「自有 TextNode」承担，KTK 的 LabelNode 直接隐藏。
//   根因：TextButtonNode 构造里 LoadThreePartTimelines 给 LabelNode 挂的 Timeline
//   每一帧都写死 Position = foregroundPositionOffset（即构造默认的 (16,3)），
//   按下帧再 +1px → (16,4)。我们自定义的 (0,1)/(5,2) 每次 hover / 按下都会被
//   打回 (16,3)，所以文字会「动来动去」。Timeline 的 setter 是 internal，插件侧
//   无法摘除，于是改成「隐藏会动的层 + 自己画一层不会动的文字」。
// ─────────────────────────────────────────────────────────────────────────────
internal static class NativeButtonStyle
{
    internal const float MinW = 40f;
    private const float PadX = 36f;
    private const float CjkPx = 12.5f;      // TextNode 默认 FontSize = 12，中文按全角略留余量
    private const float AsciiPx = 6.8f;
    private const float LineH = 15f;        // 每多一行按钮增高

    private sealed class Origin
    {
        internal Vector4 Bg;
        internal Vector4 Fg;
    }

    private static readonly ConditionalWeakTable<TextButtonNode, Origin> Origins = new();

    // ── v1.1.99 全局 Hover 规范 / v1.2.00 文字层锁定 ─────────────────────────
    private sealed class HoverState
    {
        internal bool Hovered;
        internal Vector4 Idle;
        internal Vector4 Hover;
        /// <summary>v1.2.00：自有文字层（无 Timeline，坐标永不被游戏改写）。</summary>
        internal TextNode? Label;
    }

    private static readonly ConditionalWeakTable<TextButtonNode, HoverState> Hovers = new();

    // 统一配色（集中在这里调）：未选中 hover = 纯白，选中 hover = 稍亮琥珀。
    private static readonly Vector4 HoverWhite     = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 HoverNearWhite = new(0.92f, 0.94f, 0.98f, 1f);
    private static readonly Vector4 HoverGold      = new(1.00f, 0.78f, 0.30f, 1f);
    private static readonly Vector4 HoverGoldLight = new(1.00f, 0.88f, 0.55f, 1f);

    private static HoverState Ensure(TextButtonNode b)
        => Hovers.GetValue(b, _ => new HoverState
        {
            Idle  = b.LabelNode.TextColor,
            Hover = HoverWhite,
        });

    /// <summary>
    /// v1.2.00 ★ 自有文字层。KTK 的 LabelNode 被隐藏，所有可见文字写在这上面。
    /// 该节点没有任何 Timeline，游戏不会改写它的 Position / Size / Color，
    /// 因此 hover、按下、拖拽都不会产生 1px 位移或重排。
    /// 初始几何镜像构造期的 LabelNode（(16,3) / W-32 × H-8 / 居中），
    /// 保证「不改样式的普通按钮」视觉与旧版完全一致。
    /// </summary>
    private static TextNode Lab(TextButtonNode b)
    {
        var st = Ensure(b);
        if (st.Label is not null) return st.Label;

        var src = b.LabelNode;
        var lab = new TextNode
        {
            Position      = src.Position,
            Size          = new Vector2(MathF.Max(1f, src.Width), MathF.Max(1f, src.Height)),
            FontSize      = src.FontSize,
            FontType      = FontType.Axis,
            LineSpacing   = 12,
            AlignmentType = src.AlignmentType,
            TextColor     = src.TextColor,
            String        = "",
        };
        lab.AddTextFlags(TextFlags.MultiLine);
        lab.AttachNode(b);          // 作为按钮子节点 → 画在九宫格底之上

        st.Label = lab;
        st.Idle  = src.TextColor;   // 主题原色仍从 KTK 节点取，配色不变
        src.IsVisible = false;      // ★ 会动的那层彻底不参与绘制
        return lab;
    }

    /// <summary>写可见文字（永远写自有层，绝不碰会动的 LabelNode）。</summary>
    internal static void SetLabel(TextButtonNode b, string? text)
    {
        try { Lab(b).String = text ?? ""; }
        catch { /* 节点已释放则忽略 */ }
    }

    /// <summary>把自有文字层的几何钉死到样式要求的坐标 / 尺寸 / 对齐。</summary>
    private static void PinLabel(TextButtonNode b, Vector2 pos, Vector2 size, AlignmentType align)
    {
        var lab = Lab(b);
        lab.Position      = pos;
        lab.Size          = new Vector2(MathF.Max(1f, size.X), MathF.Max(1f, size.Y));
        lab.AlignmentType = align;
    }

    /// <summary>
    /// 通用按钮（Apply 系列）沿用 KTK 默认内边距：把隐藏 LabelNode 的几何原样镜像到自有层。
    /// ★ 必须在改完 b.Size 之后调用 —— Size setter 会重算 LabelNode 的 Position/Size。
    /// </summary>
    private static void SyncLabel(TextButtonNode b)
    {
        var src = b.LabelNode;
        PinLabel(b, src.Position,
                 new Vector2(MathF.Max(1f, src.Width), MathF.Max(1f, src.Height)),
                 src.AlignmentType);
    }

    /// <summary>尺寸每帧重写一次，彻底杜绝「hover / 按下时重新测量导致宽高微调」。</summary>
    private static void SetSize(TextButtonNode b, float w, float h)
        => b.Size = new Vector2(w, h);

    /// <summary>v1.2.2：外部（详情横向按钮）按需改尺寸并同步自有文字层几何，顺序同 Apply。</summary>
    internal static void SizePin(TextButtonNode b, float w, float h)
    {
        SetSize(b, w, h);
        SyncLabel(b);
    }

    /// <summary>
    /// v1.1.99：批量拦截入口 —— 每个 TextButtonNode 工厂里调用一次即可。
    /// ★ 原理：KTK 的 TextButtonNode 构造里 ApplyTexture() 会调
    ///   LoadThreePartTimelines(this, BackgroundNode, LabelNode, ...)，给按钮自身、
    ///   九宫格底、文字各挂一条 ATK Timeline。hover 时 AtkComponentButton 自动播
    ///   label 2：BackgroundNode 帧 addColor+16 把 ButtonA.tex 底与边框整体提亮，
    ///   pressed 帧让文字下移 1px，disabled 帧把 alpha 压到 178/153。
    ///   KTK 的 Timeline 属性 setter 是 internal，插件侧无法替换；但可见效果可以
    ///   100% 覆盖：文字改由无 Timeline 的自有 TextNode 承担（Lab），会动的 LabelNode
    ///   隐藏；九宫格底保留（工具按钮仍要那块底板），但每帧把 AddColor 归零、
    ///   Position 拉回 (0,0)，按下帧的位移立刻被抹平。
    /// ★ MouseOver/MouseOut 在 KTK 里只加 EmitsEvents|RespondToMouse，不加
    ///   HasCollision，因此不会像 MouseDown/MouseClick 那样吞掉组件 OnClick。
    /// ★ 事件回调只改状态 + 重刷文字色，不触碰任何尺寸/文本/坐标 → 零闪烁。
    /// </summary>
    internal static void InitHover(TextButtonNode b)
    {
        var st = Ensure(b);
        NeutralizeColors(b);
        b.ShowClickableCursor = true;   // 只保留手型光标作为「可点」提示
        Lab(b);                         // 建立自有文字层并隐藏会动的 LabelNode

        b.AddEvent(AtkEventType.MouseOver, (Action)(() => { st.Hovered = true;  RePaint(b); }));
        b.AddEvent(AtkEventType.MouseOut,  (Action)(() => { st.Hovered = false; RePaint(b); }));
    }

    /// <summary>纯文字交互节点：把原生九宫格底/边框彻底关掉（双保险）。</summary>
    internal static void HideBackground(TextButtonNode b)
    {
        b.BackgroundNode.IsVisible     = false;
        b.BackgroundNode.Alpha         = 0f;
        b.BackgroundNode.AddColor      = Vector3.Zero;
        b.BackgroundNode.MultiplyColor = Vector3.One;
    }

    /// <summary>
    /// 每个样式方法末尾调用：登记「常态色 / hover 色」并按当前 hover 态落色。
    /// ApplyInner 每帧重刷样式时也会走这里，所以 hover 色不会被冲掉 —— 防闪烁关键。
    /// </summary>
    internal static void Paint(TextButtonNode b, Vector4 idle, Vector4 hover)
    {
        var st = Ensure(b);
        st.Idle  = idle;
        st.Hover = hover;
        Lab(b).TextColor = st.Hovered ? hover : idle;
        NeutralizeColors(b);
    }

    private static void RePaint(TextButtonNode b)
    {
        if (!Hovers.TryGetValue(b, out var st)) return;
        Lab(b).TextColor = st.Hovered ? st.Hover : st.Idle;
        NeutralizeColors(b);
    }

    /// <summary>Timeline 每次状态切换都会写 AddColor / MultiplyColor / Position，这里全部抹平。</summary>
    private static void NeutralizeColors(TextButtonNode b)
    {
        var lab = Lab(b);
        lab.AddColor      = Vector3.Zero;
        lab.MultiplyColor = Vector3.One;

        b.LabelNode.AddColor      = Vector3.Zero;
        b.LabelNode.MultiplyColor = Vector3.One;

        b.BackgroundNode.AddColor      = Vector3.Zero;
        b.BackgroundNode.MultiplyColor = Vector3.One;
        // 按下帧（18..26）会把九宫格底整体下移 1px；这里每帧拉回原点，底板也不抖。
        b.BackgroundNode.Position = Vector2.Zero;
    }

    /// <summary>
    /// v1.1.99：搜索框等 TextInputNode 的 Hover / 聚焦边框统一为微弱灰蓝。
    /// KTK 构造里 BackgroundNode 与 FocusBorderNode 都用 ui/uld/TextInputA.tex，
    /// LoadTimelines 会让 FocusBorderNode 在焦点帧 alpha 0→255 突然亮起（刺眼）。
    /// 这里不隐藏它（保留「已聚焦」反馈），只染成灰蓝并中和 addColor；
    /// 想完全无边框，把 FocusBorderNode.IsVisible 设为 false 即可。
    /// </summary>
    internal static void SoftenTextInput(TextInputNode n)
    {
        n.FocusBorderNode.MultiplyColor = new Vector3(0.46f, 0.56f, 0.72f);
        n.FocusBorderNode.AddColor      = Vector3.Zero;
        n.BackgroundNode.MultiplyColor  = new Vector3(0.70f, 0.76f, 0.86f);
        n.BackgroundNode.AddColor       = Vector3.Zero;
    }

    /// <summary>创建按钮后调用一次：让文字层认识 \n，多行文字才能显示出来。</summary>
    internal static void Prepare(TextButtonNode b)
    {
        b.LabelNode.AddTextFlags(TextFlags.MultiLine);
        Lab(b).AddTextFlags(TextFlags.MultiLine);
    }

    /// <summary>
    /// v1.1.85：KTK 的 TextNode.String 收到 null 时会在 builder.Append(null) 里抛
    /// NullReferenceException（游戏内实测堆栈：KamiToolKit.Nodes.TextNode.set_String）。
    /// 服务端 JSON 里的名字/标签字段可能为 null，所以所有文本入口一律 null→""。
    /// </summary>
    internal static string Safe(string? s) => s ?? "";

    internal static float Measure(string? s)
    {
        var w = 0f;
        if (s is null) return w;
        foreach (var c in s)
        {
            if (c is '\n' or '\r') continue;
            w += c < 0x80 ? AsciiPx : CjkPx;
        }
        return w;
    }

    /// <summary>文本节点也按内容收窄，避免透明节点撑出大片空白、影响窗口宽度计算。</summary>
    internal static float FitText(TextNode n, string? text, float maxW, float minW = 40f)
    {
        text = Safe(text);
        n.String = text;
        var w = Math.Clamp(Measure(text) + 8f, minW, maxW);
        if (MathF.Abs(n.Width - w) >= 0.5f) n.Width = w;
        return w;
    }

    /// <summary>
    /// 把按钮刷成「贴合文字 / 超长换行 / 选中变色」。返回实际宽度，
    /// 也可以直接在布局里读 b.Width / b.Height。
    /// </summary>
    internal static float Apply(TextButtonNode b, string? label, float maxW, float baseH, bool selected)
    {
        label = Safe(label);
        if (maxW < MinW) maxW = MinW;
        var avail = MathF.Max(16f, maxW - PadX);
        var textW = Measure(label);
        string text;
        int lines;
        float w;

        if (label.Length == 0 || textW <= avail)
        {
            text = label;
            lines = 1;
            w = Math.Clamp(textW + PadX, MinW, maxW);
        }
        else
        {
            text = Wrap(label, avail, out lines);
            w = maxW;
        }

        var h = baseH + Math.Max(0, lines - 1) * LineH;
        SetSize(b, w, h);                 // OnSizeChanged 会同步九宫格底与 LabelNode 尺寸
        SetLabel(b, text);
        SyncLabel(b);                     // 尺寸定稿后再镜像几何，顺序不能反
        SetSelected(b, selected);
        return w;
    }

    /// <summary>按估算宽度逐字折行；已有的 \n 原样保留。</summary>
    private static string Wrap(string s, float avail, out int lines)
    {
        var sb = new StringBuilder(s.Length + 8);
        var lineW = 0f;
        lines = 1;
        foreach (var c in s)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                sb.Append('\n');
                lineW = 0f;
                lines++;
                continue;
            }
            var cw = c < 0x80 ? AsciiPx : CjkPx;
            if (lineW + cw > avail && lineW > 0f)
            {
                sb.Append('\n');
                lineW = 0f;
                lines++;
            }
            sb.Append(c);
            lineW += cw;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 截图那种「圆点 + 文字」的筛选行：去掉按钮底，选中时圆点与文字一起变琥珀，
    /// 未选中保持主题原色；同样支持贴合文字宽与超长换行。
    /// </summary>
    internal static float Radio(TextButtonNode b, string? text, float maxW, float baseH, bool on)
    {
        text = Safe(text);
        b.BackgroundNode.IsVisible = false;
        var w = Apply(b, text, maxW, baseH, false);
        var o = GetOrigin(b);
        Paint(b, on ? new Vector4(1.00f, 0.72f, 0.24f, o.Fg.W) : o.Fg,
                 on ? HoverGoldLight : HoverWhite);
        HideBackground(b);
        return w;
    }

    /// <summary>
    /// v1.1.95：纯文字单选（主窗大区/版本、自动前往窗开关类按钮共用）。
    /// ★ 不折行：宽度按 Measure + PadX 给足，绝不调用 Wrap()。
    ///   旧 Radio → Apply 的 avail = maxW - 36，78f 的按钮只有 42px 可用，
    ///   而「● 全部」实测约 44.3px，于是被折成上下两行（用户报的折行 bug）。
    /// ★ 只靠字色区分：选中琥珀金 / 未选中浅灰(dimOff 时暗灰)；无 ● ○ 圆点、无边框底色。
    /// </summary>
    internal static float PlainRadio(TextButtonNode b, string? label, float maxW,
                                     float baseH, bool on, bool dimOff = false)
    {
        label = Safe(label);
        var w = Math.Clamp(Measure(label) + PadX, MinW, maxW);
        // 先改尺寸：OnSizeChanged 会重设 LabelNode 的位置/尺寸，最后再写文本与颜色
        SetSize(b, w, baseH);
        SetLabel(b, label);                     // 原样写入，绝不插入 \n
        SyncLabel(b);
        var o = GetOrigin(b);
        var idle = on
            ? new Vector4(1.00f, 0.78f, 0.30f, o.Fg.W)              // 选中：琥珀金
            : dimOff ? new Vector4(0.42f, 0.45f, 0.50f, o.Fg.W)     // 自动窗：未选中暗灰
                     : new Vector4(0.66f, 0.70f, 0.76f, o.Fg.W);    // 主窗：未选中浅灰
        Paint(b, idle, on ? HoverGoldLight : dimOff ? HoverNearWhite : HoverWhite);
        HideBackground(b);
        return w;
    }

    /// <summary>
    /// v1.1.97：搜索框内右端的小「×」。不套九宫格底、去掉 KTK 默认 16px 内边距，
    /// 把文字层铺满 26x24 并居中，避免它变成一个游离的大按钮。
    /// </summary>
    internal static void ClearBadge(TextButtonNode b, float baseH)
    {
        const float w = 26f;
        SetSize(b, w, baseH);
        SetLabel(b, "×");
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, baseH - 2f), AlignmentType.Center);
        Paint(b, new Vector4(0.70f, 0.74f, 0.80f, 1f), HoverWhite);
        HideBackground(b);
    }

    /// <summary>
    /// v1.1.97：服务器折叠控件里的 [全服][清空][本服] 迷你按钮。
    /// 宽 = 文字 + 6px（约 31px），刻意绕过 Apply 的 PadX=36，三个才能和
    /// 「服务器 5/5」挤在同一行而不重叠。
    /// </summary>
    internal static void MiniAction(TextButtonNode b, string? label, float baseH, bool on)
    {
        label = Safe(label);
        var w = MathF.Max(28f, Measure(label) + 6f);
        SetSize(b, w, baseH);
        SetLabel(b, label);
        PinLabel(b, new Vector2(1f, 1f), new Vector2(w - 2f, baseH - 2f), AlignmentType.Center);
        b.BackgroundNode.IsVisible = true;
        var o = GetOrigin(b);
        b.BackgroundNode.Color = on ? new Vector4(0.92f, 0.64f, 0.18f, o.Bg.W) : o.Bg;
        Paint(b, o.Fg, on ? HoverGoldLight : HoverWhite);
    }

    /// <summary>
    /// v1.1.97：排序表头。文字左对齐到按钮 x=0（覆盖默认 16px 内边距），
    /// 这样表头文字与下方数据列左缘严格重合，不再被「三等分」推开。
    /// </summary>
    internal static void SortHeader(TextButtonNode b, string? label, float h, bool on)
    {
        label = Safe(label);
        var w = Math.Clamp(Measure(label) + 8f, 30f, 120f);
        SetSize(b, w, h);
        SetLabel(b, label);
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, h - 2f), AlignmentType.Left);
        Paint(b, on ? new Vector4(1.00f, 0.78f, 0.30f, 1f) : new Vector4(0.58f, 0.62f, 0.68f, 1f),
                 on ? HoverGoldLight : HoverWhite);
        HideBackground(b);
    }

    /// <summary>
    /// v1.1.98：紧凑纯文字单选（大区 / 版本共用）。宽 = 文字 + 8，绕过 Apply 的 PadX=36，
    /// 四个大区 + 七个版本才能同排；不折行、无圆点、无底色。未选中压到浅灰，
    /// 让选中的琥珀金更突出（自动前往窗的 PlainRadio 不受影响）。
    /// </summary>
    internal static void CompactRadio(TextButtonNode b, string? label, float baseH, bool on)
    {
        label = Safe(label);
        var w = MathF.Max(28f, Measure(label) + 8f);
        SetSize(b, w, baseH);
        SetLabel(b, label);
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, baseH - 2f), AlignmentType.Center);
        Paint(b, on ? new Vector4(1.00f, 0.78f, 0.30f, 1f)      // 选中：琥珀金
                    : new Vector4(0.62f, 0.64f, 0.68f, 1f),     // 未选中：≈ #9EA3AD 浅灰
                 on ? HoverGoldLight : HoverWhite);
        HideBackground(b);
    }

    /// <summary>v1.1.98：设置齿轮——透明底、零内边距、hover 时文字转琥珀金。</summary>
    internal static void GhostButton(TextButtonNode b, string? label, float baseH, bool hover)
    {
        label = Safe(label);
        var w = MathF.Max(28f, Measure(label) + 8f);
        SetSize(b, w, baseH);
        SetLabel(b, label);
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, baseH - 2f), AlignmentType.Center);
        // v1.1.99：hover 态由 InitHover 的 MouseOver/MouseOut 统一驱动，
        // 这里只登记两档颜色；保留 hover 形参仅为兼容旧调用点。
        Paint(b, new Vector4(0.74f, 0.80f, 0.90f, 1f), HoverGold);
        HideBackground(b);
    }

    /// <summary>v1.1.96 提示标签；v1.1.98 去掉突兀的纯黑底，只留亮白文字。</summary>
    internal static void HintTag(TextButtonNode b, string? label, float maxW, float baseH)
    {
        label = Safe(label);
        var w = Math.Clamp(Measure(label) + 8f, MinW, maxW);
        SetSize(b, w, baseH);
        SetLabel(b, label);
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, baseH - 2f), AlignmentType.Left);
        var tag = new Vector4(0.86f, 0.90f, 0.98f, 1f);
        Paint(b, tag, tag);
        HideBackground(b);
    }

    /// <summary>
    /// v1.2.00：详情区的折叠表头（▾ / ▸ 目的地与操作）。
    /// 纯文字、无底色、18px 高、左对齐到 x=0，点击由外部绑定折叠逻辑；
    /// 展开时琥珀金、折叠时浅灰，hover 提亮一档。
    /// </summary>
    internal static void SectionHeader(TextButtonNode b, string? label, float h, bool open)
    {
        label = Safe(label);
        var w = MathF.Max(28f, Measure(label) + 8f);
        SetSize(b, w, h);
        SetLabel(b, label);
        PinLabel(b, new Vector2(0f, 1f), new Vector2(w, h - 2f), AlignmentType.Left);
        Paint(b, open ? new Vector4(0.94f, 0.78f, 0.42f, 1f)     // 展开：琥珀金
                      : new Vector4(0.62f, 0.66f, 0.72f, 1f),    // 折叠：浅灰
                 open ? HoverGoldLight : HoverWhite);
        HideBackground(b);
    }

    /// <summary>v1.1.95：底部主操作按钮——禁用暗灰、启用琥珀高亮。</summary>
    internal static void RunButton(TextButtonNode b, string? label, float w, float h, bool enabled)
    {
        label = Safe(label);
        SetSize(b, w, h);
        SetLabel(b, label);
        SyncLabel(b);
        b.BackgroundNode.IsVisible = true;
        var o = GetOrigin(b);
        b.BackgroundNode.Color = enabled
            ? new Vector4(0.92f, 0.64f, 0.18f, o.Bg.W)
            : new Vector4(0.18f, 0.20f, 0.24f, o.Bg.W);
        Paint(b, enabled
            ? new Vector4(1.00f, 0.98f, 0.90f, o.Fg.W)
            : new Vector4(0.45f, 0.48f, 0.53f, o.Fg.W),
              enabled ? HoverWhite : new Vector4(0.45f, 0.48f, 0.53f, o.Fg.W));
    }

    /// <summary>
    /// v1.1.77：固定列宽的列表行按钮。宽度不再随文字变化（服名/进度/怪名三列对齐），
    /// 文字过长仍在固定宽度内折行加高；底色与字色由调用方给定，用于条纹 / 选中态。
    /// 另外覆盖 KTK 默认的 16px 左右内边距，让 42px 的图标按钮也能放下文字。
    /// </summary>
    internal static void Fixed(TextButtonNode b, string? label, float w, float baseH,
                               Vector4 bg, Vector4 fg)
    {
        label = Safe(label);
        var avail = MathF.Max(16f, w - 12f);
        var textW = Measure(label);
        string text;
        int lines;
        if (label.Length == 0 || textW <= avail) { text = label; lines = 1; }
        else text = Wrap(label, avail, out lines);

        var h = baseH + Math.Max(0, lines - 1) * LineH;
        SetSize(b, w, h);
        SetLabel(b, text);
        PinLabel(b, new Vector2(5f, 2f), new Vector2(w - 10f, h - 4f), AlignmentType.Left);
        b.BackgroundNode.IsVisible = true;

        var o = GetOrigin(b);
        b.BackgroundNode.Color = new Vector4(bg.X, bg.Y, bg.Z, o.Bg.W);
        Paint(b, new Vector4(fg.X, fg.Y, fg.Z, o.Fg.W), HoverWhite);
    }

    /// <summary>v1.1.77：截图那种资料片徽章——方形彩色图标按钮，选中时提亮 + 暖白字。</summary>
    internal static void Icon(TextButtonNode b, string label, Vector4 tint, bool on)
    {
        const float side = 42f;
        SetSize(b, side, side);
        SetLabel(b, label);
        PinLabel(b, new Vector2(2f, 2f), new Vector2(side - 4f, side - 4f), AlignmentType.Center);
        b.BackgroundNode.IsVisible = true;

        var o = GetOrigin(b);
        var k = on ? 1.0f : 0.55f;      // 未选中的图标压暗，模拟游戏里未激活的版本徽章
        b.BackgroundNode.Color = new Vector4(tint.X * k, tint.Y * k, tint.Z * k, o.Bg.W);
        Paint(b, on
            ? new Vector4(1.00f, 0.96f, 0.72f, o.Fg.W)
            : new Vector4(0.82f, 0.85f, 0.92f, o.Fg.W), HoverWhite);
    }

    /// <summary>v1.1.77：游戏传送列表那种一行一个目的地，交替条纹底 + 选中琥珀高亮。</summary>
    internal static void Stripe(TextButtonNode b, string label, float w, float baseH,
                                bool alternate, bool selected)
    {
        var bg = selected ? new Vector4(0.90f, 0.58f, 0.16f, 1f)
            : alternate ? new Vector4(0.20f, 0.23f, 0.30f, 1f)
                        : new Vector4(0.12f, 0.15f, 0.21f, 1f);
        var fg = selected ? new Vector4(1.00f, 0.98f, 0.86f, 1f)
                          : new Vector4(0.88f, 0.92f, 1.00f, 1f);
        Fixed(b, label, w, baseH, bg, fg);
    }

    /// <summary>
    /// v1.1.78：游戏里「◷ 12:26」那种青蓝发光计时文字。
    /// TextNode 自带 Edge/Glare/Bold，配合亮青字 + 深蓝描边即可复刻，
    /// 不需要额外的图标贴图。
    /// </summary>
    internal static void Glow(TextNode n, string text, Vector4 color, uint fontSize)
    {
        n.String = text;
        n.FontSize = fontSize;
        n.FontType = FontType.TrumpGothic;
        n.TextColor = color;
        n.TextOutlineColor = new Vector4(0.06f, 0.45f, 0.78f, 1f);
        n.AddTextFlags(TextFlags.Edge | TextFlags.Glare | TextFlags.Bold);
        n.AlignmentType = AlignmentType.Left;
    }

    /// <summary>
    /// v1.1.78：传送列表行样式 —— 未选中完全透明（无边框无底色），
    /// 选中/悬停才铺一条横贯整行的半透明灰底；文字一律左对齐、不变色。
    /// </summary>
    internal static void TeleportLine(TextButtonNode b, string? label, float w, float baseH, bool selected)
    {
        label = Safe(label);
        var bg = new Vector4(0.62f, 0.66f, 0.72f, 0.42f);
        Fixed(b, label, w, baseH, bg, new Vector4(0.90f, 0.93f, 0.98f, 1f));
        if (selected) b.BackgroundNode.Color = bg;
        b.BackgroundNode.IsVisible = selected;
    }

    private static Origin GetOrigin(TextButtonNode b)
    {
        if (!Origins.TryGetValue(b, out var o))
        {
            o = new Origin { Bg = b.BackgroundNode.Color, Fg = b.LabelNode.TextColor };
            Origins.Add(b, o);
        }
        return o;
    }

    internal static void SetSelected(TextButtonNode b, bool on)
    {
        var o = GetOrigin(b);

        if (on)
        {
            // 琥珀底 + 暖白字：亮/暗主题下都能一眼看出「已选中」。
            b.BackgroundNode.Color = new Vector4(1.00f, 0.68f, 0.22f, o.Bg.W);
            Paint(b, new Vector4(1.00f, 0.97f, 0.82f, o.Fg.W), HoverWhite);
        }
        else
        {
            b.BackgroundNode.Color = o.Bg;
            Paint(b, o.Fg, HoverWhite);
        }
    }
}
