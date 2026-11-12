using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.1.94：主窗**只用纹理无关节点**（TextNode / TextButtonNode / ColorImageNode / TextInputNode），
//           彻底移除所有会依赖游戏纹理的 KTK 组件，从根上消除「国服纹理缺失 → 构造/使用崩溃」。
//   ★★ ResNode 是 ATK 的基类容器节点（NodeType.Res，无 PartsList、无纹理），**不产生任何绘制图元**；
//      v1.1.80~v1.1.93 的血条 / 详情迷你条 / 选中行边框因误用 ResNode 而从未渲染过。
//      v1.1.92 的「兄弟节点 Z 序重叠」结论已被 v1.1.93 金丝雀实机推翻，勿再据此调坐标或层级。
//      正解：ColorImageNode（NodeType.Image + 空 Part + AddColor），构造不设 TexturePath 即零 .tex 依赖。
//
//   · 大区（豆豆柴/莫古力/猫小胖/鸟）→ 一行圆点单选按钮（● 豆豆柴 / ○ 莫古力），
//     选中即 SelectDc（切换大区 = 切换该大区的世界筛选，列表随之过滤）。超宽自动换行。
//   · 版本号（2.0~7.0）→ 一行纯文字多选：左键切换、右键独奏（v1.1.96）。超宽自动换行。
//   · S 怪列表每行 = ◆ 状态色 + 小服务器(金色突出) + S 怪名 + 无圆角双 ColorImageNode 血条 + 百分比。
//     v1.1.91：血条固定为「长方形无圆角 / 深槽 + 琥珀金填充」单一风格，宽度按百分比渲染；
//     样式选择行已移除。不用 ProgressBarNode（依赖 ui/uld/ToDoList.tex，国服缺失会崩窗）。
//     v1.1.92：两矩形改为「不重叠整数分段」（左填充 / 右深槽），接缝零缝，不依赖 Z 序。
//     选中行 = 半透明琥珀灰底 + 左侧 4px 琥珀竖条（v1.1.95，替代四边框）。
//   · S 怪详情（右键某行展开）= 固定表头「▾ 目的地与操作」+ 目的地按钮列表
//     （v1.1.95 去掉与列表行重复的服务器/怪名 meta 与底部迷你条）。
//   · 被弃用的组件（原因：纹理依赖，国服客户端缺失）
//       ProgressBarNode   → ui/uld/ToDoList.tex（构造即加载，v1.1.79 崩窗元凶）
//       CollapsingHeaderNode / ListBoxNode → 内部纹理 + 布局按 Height 堆叠易错位
//       RadioButtonGroupNode / BorderNineGridNode → 内部纹理 + 使用期异常
//   · Apply 拆成外层兜底 + 内层 ApplyInner；外层 catch 里强制补跑一次 Reflow，
//     保证任何异常都不会让窗口「叠成一坨」或整窗回退 ImGui。
//   · 原 ImGui 完整版仍可用：「完整」按钮 / /水桶 legacy 临时切回。
//   · v1.1.97：搜索框归位 fixedRoot（修左上角幽灵 ×）；列坐标左侧留白（修首列被吃）；
//     轨道与滚动条改同父同源（修分截悬空）；表头文字左对齐列起点（修三等分）；
//     [全服/清空/本服] 并入「服务器 5/5」折叠控件，点完自动收起（顶部减负）。
//   · v1.1.98：窗口外框固定 800（Reflow 不再写 Size）；顶部压成三行并统一左边距 16；
//     版本左键改走 ReceiveEvent（AddEvent 的 HasCollision 会吞掉组件 OnClick）；
//     右上角「设置」齿轮把旧 ImGui 面板降级为高级设置。
//   · v1.1.99：全局 Hover 统一 —— TextButtonNode 构造期 LoadThreePartTimelines 的
//     原生悬停底色/边框由 InitHover 拦截（HideBackground + 中和 AddColor），
//     MouseOver/MouseOut 只改文字色、绝不重算尺寸；搜索框聚焦边框染成微弱灰蓝。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>展开面板里的一行目的地：绿点坐标，或大水晶，或顾尼图特殊触发。</summary>
internal sealed class MainPointRow
{
    internal string Label = "";
    internal float X;
    internal float Y;
    internal bool Aetheryte;
    internal bool Special;
}

internal sealed class MainHuntRow
{
    internal string Key = "";        // en + "\u0001" + world，唯一行 key
    internal string En = "";
    internal string World = "";
    internal string Cn = "";
    internal string Map = "";
    internal string RowLabel = "";
    internal string ProgressLabel = "";
    internal double PctValue;
    internal string State = "";
    internal string Pct = "";
    internal string Patch = "";      // v1.1.96：资料片 key（ARR/HW/SB/ShB/EW/DT），排序用
    internal string Eta = "";
    internal string Clock = "";
    internal string BestWorld = "";
    internal string PrimaryLabel = "";
    internal string Condition = "";
    internal string Tips = "";
    internal string PointsHint = "";
    internal bool Expanded;
    internal bool HasSpecial;   // v1.2.2：是否有特殊触发脚本（切职/采集/自爆等），决定执行触发/自动前往文案
    internal bool Triggered;    // v1.2.12：已被他人触发（行内红标 + 详情按钮置灰不可点）
    internal readonly List<MainPointRow> Points = new();
}

/// <summary>v1.1.96：列表排序维度。sortActive=false 时保持数据层原顺序（开闸优先）。</summary>
internal enum SortBy { Server = 0, Version = 1, Probability = 2 }

/// <summary>主窗口纯数据快照（每 200ms 同步一次，不含任何 KTK 类型）。</summary>
internal sealed class MainSnapshot
{
    internal bool Ready;
    internal bool OnlyOpen;
    internal bool Fetching;
    internal string DataError = "";
    internal string Dc = "";
    internal string DcNext = "";
    internal string Search = "";
    internal string WorldMenuLabel = "";
    internal string Status = "";
    internal string BearStatus = "";
    internal string Counts = "";
    internal string CurrentWorld = "";
    internal bool NeedsManualRefresh;

    internal readonly List<string> Worlds = new();
    internal readonly List<bool> WorldOn = new();

    internal readonly List<string> Dcs = new();
    internal string DcSelected = "";

    internal readonly List<string> PatchKeys = new();
    internal readonly List<string> PatchLabels = new();
    internal readonly List<bool> PatchOn = new();

    internal readonly List<MainHuntRow> Hunts = new();
}

/// <summary>主窗口按钮回调（同样不含 KTK 类型）。</summary>
internal sealed class MainActions
{
    internal Action<string> Search = _ => { };
    internal Action NextDc = () => { };
    internal Action ToggleOnlyOpen = () => { };
    internal Action Refresh = () => { };
    internal Action OpenAuto = () => { };
    internal Action Legacy = () => { };
    internal Action Mini = () => { };
    internal Action StopAll = () => { };
    internal Action AllWorlds = () => { };
    internal Action ClearWorlds = () => { };
    internal Action CurrentWorldOnly = () => { };
    internal Action<string> ToggleWorld = _ => { };
    internal Action<string> TogglePatch = _ => { };
    internal Action<string> SelectDc = _ => { };
    internal Action<string> SelectPatch = _ => { };
    internal Action OpenSettings = () => { };   // v1.1.98：设置齿轮 → ImGui 高级设置
    internal Action OpenHistory = () => { };    // v1.2.12：齿轮旁的 [历史] → 原生历史窗口
    internal Action<string, string> Primary = (_, _) => { };
    internal Action<string, string, float, float> GotoPoint = (_, _, _, _) => { };
    internal Action<string, string> GotoAetheryte = (_, _) => { };
    internal Action<string, float, float> Flag = (_, _, _) => { };       // v1.2.2：坐标按钮 → 只在地图插旗
    internal Action<string, string> FlagAetheryte = (_, _) => { };         // v1.2.2：大水晶按钮 → 插旗到该区大水晶
        internal Action<string> Expand = _ => { };
        internal Action<string, string> SelectHuntWorld = (_, _) => { };
        internal Action Closed = () => { };
    }

/// <summary>原生主窗口：工具行 + 大区/版本单选组 + 传送列表式 S 怪列表 + 折叠详情。</summary>
internal sealed unsafe class MainWindow : NativeAddon
{
    internal const float W = 600f;
    // v1.1.98 ★ 固定窗口高度：外框恒为 800，与列表行数完全无关（不再动态计算）。
    internal const float DefaultH = 800f;
    internal const float FixedWindowH = DefaultH;

    private const float CW = 576f;
    private const float IconW = 20f;
    private const float WorldW = 100f;   // v1.1.97：收窄 12px 让给左侧留白，5 字服名仍放得下
    private const float NameW = 154f;
    private const float BarW = 232f;
    private const float TailW = 32f;
    private const float RowTotalW = 576f;
    // v1.1.97 ★ 绝对列坐标：0..4 选中竖条、10..30 ◆ 图标、40 起服务器文字。
    //   旧版 IconX=0 / WorldX=26 让竖条(-2..2)压住图标、文字又紧贴图标，首列被吃掉。
    private const float IconX = 10f;
    private const float WorldX = 40f;
    private const float NameX = 146f;
    private const float BarX = 306f;
    private const float TailX = 544f;
    private const float RowH = 28f;
    private const float BarH = 12f;
    private const float BottomPad = 8f;
    private const float TopInset = 16f;    // v1.1.98：顶部各行统一左边距
    private const float SearchW = 240f;    // v1.1.98：搜索框占左侧大部分空间
    private const int WorldMenuMax = 16;
    private const int PatchCount = 7;
    private const int MaxRows = 320;
    private const int RowPoolAtSetup = 64;   // OnSetup 里预建的行节点数（超出才懒增长）

    // v1.1.95：顶部四行之间的呼吸间距；选中行整行半透明琥珀灰底（由 ColorImageNode 承担）。
    private const float RowGap = 6f;         // v1.1.98：顶部各行统一间距
    // v1.2.00：列表滚动视口上限 —— 再长的列表也只在容器内滚动，绝不画到窗口外。
    private const float MaxListViewH = 600f;
    private const float MinListViewH = 160f;
    private const float DetailBtnH = 22f;        // v1.2.2：详情单行按钮高度
    private const float DetailIndent = 20f;      // v1.2.2：详情整行左缩进（从属于列表行）
    private const float DetailGap = 14f;         // v1.2.2：按钮元素间距
    private const float ScrollBarTrackW = 8f;
    private const float HeaderH = 22f;
    private static readonly string[] HeaderNames = { "服务器", "怪名", "概率" };
    private static readonly Vector4 MutedText = new(0.52f, 0.56f, 0.62f, 1f);
    private static readonly Vector4 TrackColor = new(0.10f, 0.12f, 0.16f, 0.85f);
    private static readonly Vector4 SelBg = new(0.78f, 0.66f, 0.36f, 0.20f);
    private static readonly Vector4 Cyan = new(0.30f, 0.82f, 1.00f, 1f);
    private static readonly Vector4 ClockWhite = new(0.90f, 0.98f, 1.00f, 1f);
    private static readonly Vector4 BarDeep = new(0.04f, 0.07f, 0.10f, 1f);
    private static readonly Vector4 BarFill = new(0.95f, 0.78f, 0.28f, 1f);   // 固定=琥珀金填充

    // v1.1.93 金丝雀代码保留作诊断开关；v1.1.94 起正式版关闭，整数分段逻辑生效。
    // 用 static readonly 而非 const：两个分支都参与编译，且不产生 CS0162 不可达警告。
    private static readonly bool CanaryBar = false;    // 需要复测时临时改回 true 重新编译
    private const float CanaryOutX = 544f;             // 对照条 544..560：轨道外，且避开滚动条(568..576)与裁剪(566)
    private const float CanaryOutW = 16f;
    private static readonly Vector4 CanaryRed = new(1f, 0f, 0f, 1f);
    // v1.1.79：S 怪列表文字统一成游戏传送列表那种 Axis 浅灰白。
    private static readonly Vector4 ListText = new(0.86f, 0.89f, 0.94f, 1f);
    // v1.1.81：小服务器金色突出，S 怪名浅灰白。
    private static readonly Vector4 Gold = new(1.00f, 0.82f, 0.30f, 1f);
    // v1.2.2：详情区按钮配色（坐标浅灰/hover白；大水晶暗金/hover亮金；执行触发浅绿/hover亮绿）
    private static readonly Vector4 CoordIdle = new(0.66f, 0.70f, 0.76f, 1f);
    private static readonly Vector4 CoordHover = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 AetIdle = new(0.80f, 0.64f, 0.26f, 1f);
    private static readonly Vector4 AetHover = new(1.00f, 0.86f, 0.50f, 1f);
    private static readonly Vector4 ActIdle = new(0.55f, 0.92f, 0.55f, 1f);
    private static readonly Vector4 ActHover = new(0.80f, 1.00f, 0.80f, 1f);
    // v1.2.2+：详情条件标签配色（满足亮绿 / 不满足暗红 / 仅提示中性灰金）
    private static readonly Vector4 CondOk = new(0.45f, 0.95f, 0.50f, 1f);
    private static readonly Vector4 CondBad = new(0.74f, 0.30f, 0.30f, 1f);
    private static readonly Vector4 CondNeutral = new(0.80f, 0.70f, 0.42f, 1f);
    // v1.2.12：已被他人触发 —— 行内红标 + 详情动作按钮的置灰色
    internal const string TrigBadgeText = "已触发";
    private static readonly Vector4 TrigRed = new(0.95f, 0.33f, 0.30f, 1f);
    private static readonly Vector4 ActBlocked = new(0.46f, 0.48f, 0.53f, 1f);
    private const float BadgeW = 40f;

    // v1.1.91：进度条不再提供样式选择，固定「长方形无圆角 / 深槽 + 填充」一种风格。
    // v1.1.94：只用两个 ColorImageNode（空 Part + AddColor，不加载任何 .tex）。
    // ProgressBarNode 依赖 ui/uld/ToDoList.tex，国服客户端缺该纹理会在构造期直接崩窗
    // （v1.1.79 事故），故绝不引入；也不要给 ColorImageNode 设 TexturePath。

    private static readonly Vector4[] PatchTint =
    {
        new(0.46f, 0.48f, 0.54f, 1f),
        new(0.22f, 0.42f, 0.80f, 1f),
        new(0.24f, 0.66f, 0.90f, 1f),
        new(0.82f, 0.20f, 0.22f, 1f),
        new(0.50f, 0.26f, 0.78f, 1f),
        new(0.76f, 0.80f, 0.68f, 1f),
        new(0.86f, 0.68f, 0.22f, 1f),
    };

    /// <summary>由 NativeHudRuntime 注入的日志回调（指向 Dalamud /xllog），便于定位异常。</summary>
    internal static Action<string>? Logger;

    private static void Log(string m)
    {
        try { Logger?.Invoke("[SrankFlag.UI] " + m); } catch { }
        try { System.Console.WriteLine("[SrankFlag.UI] " + m); } catch { }
    }

    /// <summary>
    /// v1.1.85：KTK 的 TextNode.String 收到 null 会在内部 builder.Append 里抛
    /// NullReferenceException（游戏内堆栈：KamiToolKit.Nodes.TextNode.set_String）。
    /// 服务端 JSON 的名字/标签字段可能为 null，所有文本写入统一经过这里兜底。
    /// </summary>
    private static void SetT(TextNode? n, string? text)
    {
        if (n is null) return;
        try { n.String = text ?? ""; }
        catch (Exception ex) { Log("[原生UI] 文本写入失败：" + ex.GetType().Name); }
    }

    private static void SetT(TextButtonNode? b, string? text)
    {
        if (b is null) return;
        try { b.String = text ?? ""; }
        catch (Exception ex) { Log("[原生UI] 按钮文本写入失败：" + ex.GetType().Name); }
    }

    /// <summary>单个节点设置可见性：任何一个节点异常都不连坐其他节点。</summary>
    private static void ShowNode(NodeBase? n, bool v)
    {
        if (n is null) return;
        try { n.IsVisible = v; } catch { /* 无效节点忽略，其余节点照常 */ }
    }

    /// <summary>v1.1.98：滚动区内行节点 Alpha 恒为 1，杜绝「越往下越淡」。</summary>
    private static void KeepOpaque(NodeBase? n)
    {
        if (n is null) return;
        try { n.Alpha = 1f; } catch { /* 节点已释放则忽略 */ }
    }

    /// <summary>
    /// 行内单项刷新隔离：某一行某一项（背景/图标/服务器/怪名/血条/百分比）失败时只丢该项，
    /// 且同一种失败只在首次打印完整堆栈到 /xllog，避免每帧刷屏。
    /// </summary>
    private void RowStep(string stage, Action a, string who)
    {
        try { a(); }
        catch (Exception ex)
        {
            var sig = stage + "|" + ex.GetType().Name;
            if (sig == this.rowFailSig) return;
            this.rowFailSig = sig;
            Log($"[原生UI] 列表行[{stage}]失败（{who}）：{ex}");
        }
    }

    /// <summary>
    /// v1.1.94：按百分比绘制进度条——长方形、无圆角、深槽 + 琥珀金填充。
    /// ★ 节点必须是 ColorImageNode（ATK ImageNode + 空 Part + AddColor，零 .tex 依赖）。
    ///   ResNode 是基类容器，不产生绘制图元，v1.1.80~v1.1.93 用它画色块从未显示过。
    /// ★ ColorImageNode.Color 是 new 隐藏属性：setter 把 RGB 写进 AddColor、把 multiply RGB 置 0。
    ///   形参必须声明为 ColorImageNode，且本方法**只改 Size、绝不写 Color**——颜色在
    ///   EnsureRows / BuildDetail 构造时一次性设好，避免静态类型退化到 NodeBase 导致颜色丢失。
    /// 整数分段：fg 画左段 [0, fgW]，bg 画右段 [fgW, trackW]；返回 fgW 供 Reflow 定位。
    /// </summary>
    private int PaintBar(ColorImageNode bg, ColorImageNode fg, float trackW, float barH, float prog)
    {
        // ★ 整数分段：定位与宽度必须同源。SetWidth((ushort)) 会截断小数，
        //   若用浮点 fillW 定位 bg、用截断后的 fgW 画 fg，接缝会差 ~1px。
        //   fgW = (ushort)(trackW*prog)；bgW = trackW - fgW；bg.X = trackX + fgW。
        var fillW = Math.Clamp(trackW * prog, 0f, trackW);
        var fgW = (int)(ushort)fillW;                  // 与 SetWidth 的 ushort 截断完全同源
        var bgW = (int)(ushort)(trackW - fgW);         // 232 - 102 = 130，接缝严密
        // 只改 Size：颜色在构造时由 ColorImageNode.Color(new setter) 写入 AddColor
        fg.Size = new Vector2(fgW, barH);
        bg.Size = new Vector2(bgW, barH);

        if (!this.barLogged)
        {
            this.barLogged = true;
            Log($"[原生UI] 血条诊断 prog={prog:F3} fgW={fgW} bgW={bgW} " +
                $"fg实宽={fg.Width:F0} bg实宽={bg.Width:F0} " +
                $"fgVis={fg.IsVisible} bgVis={bg.IsVisible} fgColor={fg.Color}");
        }
        return fgW;
    }

    private readonly MainActions actions;

    private sealed class RowNodes
    {
        internal TextButtonNode Hit = null!;      // 整行点击区：左键选中 / 右键详情
        internal TextNode Icon = null!;           // ◆ 水晶图标（颜色表状态）
        internal TextNode World = null!;          // 小服务器（金色突出）
        internal TextNode Name = null!;           // S 怪名
        internal ColorImageNode BarBg = null!;    // 血条深底（ColorImageNode 纯色，零纹理依赖）
        internal ColorImageNode BarFg = null!;    // 血条前景填充（按百分比缩放宽度）
        internal TextNode Tail = null!;           // 62%
        internal TextNode Badge = null!;          // v1.2.12：红色「已触发」（只在被抢触时出现）
        internal int FillW;                       // 本帧整数填充宽度（供 Reflow 定位深槽起点）
    }


    private readonly List<RowNodes> rows = new();
    private readonly List<string> rowKeys = new();

    // ── v1.2.2：详情（单行横向按钮：坐标 / 大水晶 / 执行触发）──
    private TextNode? detailLabel;                              // “目的地与操作” 浅灰纯文字
    private TextNode? detailCond;       // v1.2.2+ 特殊触发条件单行展示（天气/时间/月相）
    private float detailCondW;
    private float detailCondRelX;
    private float detailCondRelY;
    private readonly List<TextButtonNode> detailBtns = new();    // 横向按钮池（坐标/大水晶/执行触发）
    private readonly List<Action?> detailActions = new();        // 各按钮点击行为（每帧重绑）
    private readonly List<(float rx, float ry, float w)> detailLayout = new();  // 各按钮相对位置/宽（Reflow 定位用）
    private int detailLineCount;                                 // 本次排版占几行（1 或 2）
    private float detailLabelW;
    private float detailLabelRelX, detailLabelRelY;

    // ── 顶部大区单选（v1.1.84：纯 TextButtonNode 圆点样式，零纹理依赖）──
    private const int MaxDcButtons = 8;
    private readonly TextButtonNode?[] dcButtons = new TextButtonNode?[MaxDcButtons];
    private readonly List<string> dcNames = new();
    private bool dcBuilt;

    // ── 版本单选（复用 patchButtons 的圆点样式）──
    private readonly List<string> patchKeys = new();
    private readonly List<string> patchLabels = new();

    // ── 选中行强调（v1.1.95：整行半透明底 + 左侧 4px 琥珀竖条）──
    private ColorImageNode? selRowBg, selFrameLeft;

    // ── 详情（单行横向按钮）──
    private bool detailBuilt, detailOk;
    private bool barLogged;                   // 血条诊断日志只打一次
    private ColorImageNode? canaryOut;        // 金丝雀对照条（x=544, w=16）
    private bool canaryLogged;                // 金丝雀诊断日志只打一次

    // ── v1.1.96：固定层（ScrollingNode 外部）+ 排序表头 + 贯通滚动条轨道 ──
    private ResNode? fixedRoot;               // 顶部工具区/表头容器，不随列表滚动
    private NodeBase? attachParent;           // OnSetup 期间 B()/T() 的挂载目标（null = content）
    private ColorImageNode? trackNode;        // 自画的上下贯通深灰轨道
    private TextButtonNode? hintTag;          // 「右键详情」小标签
    private readonly TextButtonNode?[] sortHeaders = new TextButtonNode?[3];
    private SortBy sortBy = SortBy.Server;
    private bool sortAsc = true;
    private bool sortActive;                  // 未点过表头时保持数据层原排序
    private long patchSoloTick;
    private string patchSoloKey = "";

    private readonly string[] patchKeysArr = new string[PatchCount];
    private readonly string[] worldNames = new string[WorldMenuMax];

    private bool setupDone;
    private bool onSetupOk = true;
    private bool applyLogged;
    private string rowFailSig = "";
    private bool menuOpen;
    private bool searchInitialized;
    private bool suppressSearch;
    private bool syncedExpand;
    private string selectedKey = "";
    private string expandedKey = "";
    private long lastRightTick;
    private string lastRightKey = "";
    private float lastViewH;
    private float lastScrollW;            // v1.1.97：宽度变化也要触发 scroll.Size 重设
    private bool settingsHover;           // v1.1.98：齿轮 hover 态（避免被每帧样式刷新覆盖）
    private ScrollingNode<ResNode>? scroll;
    private ResNode? content;

    private TextInputNode? searchInput;
    private TextButtonNode? bDc, bRefresh, bAuto, bLegacy, bMini, bStop, bClearSearch, bSettings, bHistory;
    private TextButtonNode? bWorldMenu, bOnlyOpen;
    private TextButtonNode? bWorldAll, bWorldClear, bWorldCurrent;
    private readonly TextButtonNode?[] worldMenuItems = new TextButtonNode?[WorldMenuMax];
    private readonly TextButtonNode?[] patchButtons = new TextButtonNode?[PatchCount];
    private TextNode? listTitle, counts, status, bManualHint;

    internal MainWindow(MainActions actions) => this.actions = actions;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        SetWindowSize(new Vector2(W, FixedWindowH));   // v1.1.98：只设一次，Reflow 绝不再改 Size
        var p = ContentStartPosition;

        // ★ 关窗再开窗（或切场景导致 addon 被释放重建）时，游戏会彻底释放并重建 addon，
        // 并再次调用 OnSetup，但 NativeHudRuntime 复用同一个 MainWindow 实例。
        // 必须把上一轮的托管节点池与“已构建”标记全部清空，否则 EnsureRows/EnsureDetailBtns 会因
        // `rows.Count` 已满而跳过重建，复用上一轮已被释放的节点（原生指针为 null），
        // 表现就是「列表空白 / 行项 NullReferenceException / 文本写入失败」。
        // 工具栏按钮、scroll、detail、selFrame 在 OnSetup/Apply 里都是按字段重新赋值或按
        // 标记重建，只有这几个 List 池与 *Built 标记需要在这里手动复位。
        this.rows.Clear();
        this.rowKeys.Clear();
        this.detailBtns.Clear();
        this.detailActions.Clear();
        this.detailLayout.Clear();
        this.detailLineCount = 0;
        this.detailLabel = null;
        this.detailCond = null;
        this.detailBuilt = false;
        this.dcBuilt = false;
        this.selRowBg = this.selFrameLeft = null;
        this.searchInitialized = false;
        this.canaryOut = null;                 // ★ OnSetup 复用实例：对照条随 addon 一起重建
        this.canaryLogged = false;
        this.fixedRoot = null;
        this.trackNode = null;
        this.hintTag = null;
        this.attachParent = null;
        for (var i = 0; i < this.sortHeaders.Length; i++) this.sortHeaders[i] = null;

        try
        {

        // ★ v1.1.96：固定层挂在 addon 根（ScrollingNode 外部），顶部工具区与排序表头
        //   全部放这里，滚动列表时不会消失。trackNode 必须先于 scroll attach，
        //   这样自画轨道位于游戏滚动条的绘制层之下。
        this.fixedRoot = new ResNode { Position = p, Size = new Vector2(CW, FixedWindowH) };
        this.fixedRoot.AttachNode(this);

        this.scroll = new ScrollingNode<ResNode>
        {
            Position = p,
            Size = new Vector2(CW, FixedWindowH - p.Y - BottomPad),
            AutoHideScrollBar = true,
            ScrollSpeed = 32,
        };
        this.scroll.AttachNode(this);
        // v1.1.98：固定视口下必须让滚动条接管滚轮（构造默认 true，这里显式钉死）
        this.scroll.ScrollBarNode.IsAcceptingMouseWheelEvents = true;

        // ★ v1.1.97：轨道挂到 scroll 自己的第一个子节点，与 ScrollBarNode 同父、
        //   同坐标系（x=scrollW-8, y=0, 高=视口），不再用绝对坐标二次换算，
        //   从根上消除「分截/悬空」。AsFirstChild 保证先绘制、滑块盖在上层。
        this.trackNode = new ColorImageNode
        {
            Size = new Vector2(ScrollBarTrackW, 1f),
            Color = TrackColor,
        };
        this.trackNode.AttachNode(this.scroll, NodePosition.AsFirstChild);
        this.trackNode.IsVisible = false;

        this.content = this.scroll.ContentNode;
        this.attachParent = this.fixedRoot;   // ★ 后续 B()/T() 全挂固定层

        // 工具行 A：搜索 / × / 大区(降级按钮) / 刷新 / 前往 / 完整 / 收起 / 停止
        this.searchInput = new TextInputNode
        {
            Position = p,
            Size = new Vector2(SearchW, 26f),
            PlaceholderString = "搜索",
            MaxCharacters = 64,
            OnInputReceived = s =>
            {
                if (!this.suppressSearch) this.actions.Search(s.ToString());
            },
        };
        // ★ v1.1.97：搜索框必须与 × / 工具栏同挂 fixedRoot。
        //   之前误挂 content（随列表滚动），顶部于是只剩一个孤零零的 × 幽灵按钮。
        this.searchInput.AttachNode(this.fixedRoot);
        // v1.1.99：搜索框原生聚焦/悬停边框太刺眼 → 统一染成微弱灰蓝
        NativeButtonStyle.SoftenTextInput(this.searchInput!);
        this.bClearSearch = this.B(p, 34f, 26f, "×", () =>
        {
            this.suppressSearch = true;
            this.searchInput!.String = "";
            this.suppressSearch = false;
            this.actions.Search("");
        });
        // 大区：原生单选组为主，降级时本按钮作为循环切换兜底。
        this.bDc = this.B(p, 100f, 26f, "大区", () => this.actions.NextDc());
        this.bRefresh = this.B(p, 84f, 26f, "刷新", () => this.actions.Refresh());
        // v1.1.98：右上角「设置」→ 打开旧 ImGui 面板（降级为高级设置）。
        // MouseOver/MouseOut 只加 EmitsEvents|RespondToMouse，不加 HasCollision，
        // 因此不会像 MouseClick 那样吞掉组件 OnClick。
        this.bSettings = this.B(p, 60f, 26f, "设置", () => this.actions.OpenSettings());
        try
        {
            this.bSettings!.AddEvent(AtkEventType.MouseOver, (Action)(() =>
            {
                this.settingsHover = true;
                NativeButtonStyle.GhostButton(this.bSettings!, "设置", 24f, true);
            }));
            this.bSettings!.AddEvent(AtkEventType.MouseOut, (Action)(() =>
            {
                this.settingsHover = false;
                NativeButtonStyle.GhostButton(this.bSettings!, "设置", 24f, false);
            }));
        }
        catch { /* hover 变色失败不影响点击 */ }
        // v1.2.12：齿轮左侧的 [历史] → 打开第 5 个原生窗口。与齿轮同一套 GhostButton 透明底样式，
        // hover 由 NativeButtonStyle.InitHover 的 MouseOver/MouseOut 统一驱动，无需再手写事件。
        this.bHistory = this.B(p, 60f, 26f, "历史", () => this.actions.OpenHistory());
        this.bAuto = this.B(p, 96f, 26f, "前往", () => this.actions.OpenAuto());
        this.bLegacy = this.B(p, 84f, 26f, "完整", () => this.actions.Legacy());
        this.bMini = this.B(p, 64f, 26f, "收起", () => this.actions.Mini());
        // v1.2.7：这两个按钮原本打开旧 ImGui（完整版 / 收起小窗），已封死 → 建好即隐藏，
        // 避免第一帧 Reflow 之前闪一下。回调保持空操作，绝不唤起任何 ImGui 窗口。
        this.bLegacy!.IsVisible = false;
        this.bMini!.IsVisible = false;
        this.bStop = this.B(p, 64f, 26f, "停止", () => this.actions.StopAll());

        // 工具行 B：服务器筛选菜单 + 只看开闸 + 计数
        this.bWorldMenu = this.B(p, 220f, 26f, "服务器", () => this.menuOpen = !this.menuOpen);
        this.bOnlyOpen = this.B(p, 104f, 26f, "开闸", () => this.actions.ToggleOnlyOpen());
        this.counts = this.T(p, 400f, 20f, 11, "");
        this.bManualHint = this.T(p, 160f, 20f, 11, "");
        this.bManualHint!.TextColor = MutedText;
        this.bManualHint.IsVisible = false;

        // v1.1.97：三个快捷按钮与「服务器 5/5」合并成同一个折叠控件，点完即收起，
        // 顶部不再常驻一大片筛选区。
        this.bWorldAll = this.B(p, 62f, 22f, "全服", () => { this.actions.AllWorlds(); this.menuOpen = false; });
        this.bWorldClear = this.B(p, 62f, 22f, "清空", () => { this.actions.ClearWorlds(); this.menuOpen = false; });
        this.bWorldCurrent = this.B(p, 62f, 22f, "本服", () => { this.actions.CurrentWorldOnly(); this.menuOpen = false; });
        for (var i = 0; i < WorldMenuMax; i++)
        {
            var idx = i;
            this.worldMenuItems[i] = this.B(p, 216f, 24f, "", () =>
            {
                var n = this.worldNames[idx];
                if (n.Length > 0) this.actions.ToggleWorld(n);
            });
        }

        // v1.1.96：版本多选——左键 OnClick 切换选中（TogglePatch），
        // 右键 AddEvent 独奏（SelectPatch：清空其他、只留当前）。
        for (var i = 0; i < PatchCount; i++)
        {
            var idx = i;
            // v1.1.98 ★ 左键 Bug 根因：AddEvent(MouseDown|MouseClick) 会给节点加
            // NodeFlags.HasCollision（KTK 注释：prevent events from propagating），
            // 点击被节点吞掉，ButtonBase 的 AtkEventType.ButtonClick / OnClick 永远收不到。
            // 因此 OnClick 置空，左右键统一交给 WirePatchClick 的 ReceiveEvent 处理。
            this.patchButtons[i] = this.B(p, 42f, 22f, "", () => { });
            this.WirePatchClick(this.patchButtons[i]!, idx);
            NativeButtonStyle.CompactRadio(this.patchButtons[i]!, "", 22f, false);
            this.patchButtons[i]!.IsVisible = false;    // 有数据后再显示，避免空按钮抢占点击区
        }

        // 大区圆点单选（● 豆豆柴 / ○ 莫古力 …）：选中即切大区 + 过滤列表
        for (var i = 0; i < MaxDcButtons; i++)
        {
            var idx = i;
            this.dcButtons[i] = this.B(p, 90f, 22f, "", () =>
            {
                if (idx < this.dcNames.Count) this.actions.SelectDc(this.dcNames[idx]);
            });
            NativeButtonStyle.CompactRadio(this.dcButtons[i]!, "", 22f, false);
            this.dcButtons[i]!.IsVisible = false;
        }

        this.listTitle = this.T(p, 500f, 20f, 12, "");
        // v1.1.96：「右键详情」提亮成带深灰底的小标签（替代原来挤在标题里的暗字）
        this.hintTag = this.B(p, 86f, 18f, "右键详情", () => { });
        // v1.1.96：排序表头（挂在 fixedRoot，永不随列表滚走）
        for (var i = 0; i < 3; i++)
        {
            var by = (SortBy)i;
            this.sortHeaders[i] = this.B(p, 78f, HeaderH, "", () => this.SetSort(by));
            NativeButtonStyle.SortHeader(this.sortHeaders[i]!, HeaderNames[i] + " ↕", HeaderH, false);
        }
        this.counts!.TextColor = MutedText;

        this.attachParent = null;   // ★ 从这里起，列表/详情/状态节点一律挂 content（可滚动）
        this.status = this.T(p, CW, 20f, 11, "");
        this.status!.TextColor = MutedText;

        // ★ 行节点池必须在 OnSetup 里预建：若在 Apply 里懒创建，个别节点的原生指针
        // 未正确注册，写文本时会抛 NullReferenceException（栈顶 TextNode.set_String），
        // 表现就是「列表空白」。这里与标题/按钮同批创建，全部正常。
        this.EnsureRows(RowPoolAtSetup);

        // ★ 金丝雀对照条：必须在 OnSetup 预建（Apply 里懒建的原生指针会不稳）。
        //   x=544..560 在血条轨道 304..536 之外，且 < 566（滚动条可见时的内容宽），
        //   专门验证「是否只有 304..536 这一段被同层兄弟覆盖」。
        if (CanaryBar)
        {
            this.canaryOut = new ColorImageNode
            {
                Position = p,
                Size = new Vector2(CanaryOutW, BarH),
                Color = CanaryRed,
            };
            this.canaryOut.AttachNode(this.content);
            this.canaryOut.IsVisible = false;
        }

        this.setupDone = true;
        }
        catch (Exception ex)
        {
            this.onSetupOk = false;
            Log("[原生UI] OnSetup 构造异常，主窗停用：" + ex);
        }
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
            OnClick = onClick,
        };
        NativeButtonStyle.Prepare(b);
        NativeButtonStyle.InitHover(b);     // v1.1.99：全局 Hover 统一拦截
        b.AttachNode(this.attachParent ?? this.content);
        return b;
    }

    /// <summary>列表节点池按需增长：不分页，几十到几百行都直接铺在滚动容器里。</summary>
    private void EnsureRows(int need)
    {
        var p = this.ContentStartPosition;
        while (this.rows.Count < need && this.rows.Count < MaxRows)
        {
            var idx = this.rows.Count;

            var hit = this.B(p, RowTotalW, RowH, "", () => this.Select(idx));
            hit.BackgroundNode.IsVisible = false;
            this.WireRightClick(hit, idx);

            var r = new RowNodes
            {
                Hit = hit,
                Icon = new TextNode
                {
                    Position = p,
                    Size = new Vector2(IconW, 20f),
                    FontSize = 15,
                    FontType = FontType.Axis,
                    String = "◆",
                    TextColor = Cyan,
                },
                World = new TextNode
                {
                    Position = p,
                    Size = new Vector2(WorldW, 22f),
                    FontSize = 13,
                    FontType = FontType.Axis,
                    TextColor = Gold,
                },
                Name = new TextNode
                {
                    Position = p,
                    Size = new Vector2(NameW, 22f),
                    FontSize = 13,
                    FontType = FontType.Axis,
                    TextColor = ListText,
                },
                BarBg = new ColorImageNode
                {
                    Position = p,
                    Size = new Vector2(BarW, BarH),
                    Color = BarDeep,          // ★ 走 new setter -> AddColor
                },
                BarFg = new ColorImageNode
                {
                    Position = p,
                    Size = new Vector2(BarW, BarH),
                    Color = BarFill,          // ★ 走 new setter -> AddColor
                },
                Tail = new TextNode
                {
                    Position = p,
                    Size = new Vector2(TailW, 18f),
                    FontSize = 12,
                    FontType = FontType.Axis,
                    AlignmentType = AlignmentType.Right,
                    TextColor = ListText,
                },
                // v1.2.12：红色「已触发」。贴著怪名列右缘右对齐，最长怪名（先驱勇士 阿提卡斯 106.8px）
                // 落在 146..252.8，徽标占 258..298，中间还留 5px，任何怪名都不会压到它。
                Badge = new TextNode
                {
                    Position = p,
                    Size = new Vector2(BadgeW, 20f),
                    FontSize = 12,
                    FontType = FontType.Axis,
                    AlignmentType = AlignmentType.Right,
                    TextColor = TrigRed,
                    String = TrigBadgeText,
                },
            };
            r.Icon.AttachNode(this.content);
            r.World.AttachNode(this.content);
            r.Name.AttachNode(this.content);
            r.BarBg.AttachNode(this.content);
            r.BarFg.AttachNode(this.content);
            r.Tail.AttachNode(this.content);
            r.Badge.AttachNode(this.content);
            // 预建池默认隐藏，由 Apply 按数据打开，避免空行在首帧闪一下
            r.Hit.IsVisible = r.Icon.IsVisible = r.World.IsVisible = r.Name.IsVisible =
                r.BarBg.IsVisible = r.BarFg.IsVisible = r.Tail.IsVisible = r.Badge.IsVisible = false;
            this.rows.Add(r);
            this.rowKeys.Add("");
        }
    }

    /// <summary>
    /// v1.1.98 ★ 左右键统一入口。根因：KTK <c>SetNodeEventFlags</c> 对 MouseDown/MouseClick
    /// 会 <c>AddNodeFlags(EmitsEvents, RespondToMouse, HasCollision)</c>，注释明写
    /// 「set HasCollision to prevent events from propagating」。点击于是被本节点吞掉，
    /// <c>ButtonBase</c> 注册的 <c>AtkEventType.ButtonClick</c>（即 TextButtonNode.OnClick）
    /// 永远收不到；而挂在节点上的 ReceiveEvent 能收到 —— 所以只有右键独奏生效。
    /// 修复：左键也走这条已验证可用的路径；OnClick 置空避免双触发；
    /// MouseDown + MouseClick 可能各派发一次，用 (key|btn) 300ms 去重防 toggle 被抵消。
    /// </summary>
    private void WirePatchClick(TextButtonNode b, int idx)
    {
        AtkEventListener.Delegates.ReceiveEvent handler = (_, _, _, _, data) =>
        {
            if (data is null) return;
            var btn = data->MouseData.ButtonId;
            if (btn != 0 && btn != 1) return;
            var k = this.patchKeysArr[idx] ?? "";
            if (k.Length == 0) return;
            var tick = Environment.TickCount64;
            var sig = k + "|" + btn;
            if (sig == this.patchSoloKey && tick - this.patchSoloTick < 300) return;
            this.patchSoloTick = tick;
            this.patchSoloKey = sig;
            Log($"[SrankFlag.UI] 版本点击: {k}, btn={btn}");
            if (btn == 1) this.actions.SelectPatch(k);   // 右键：独奏
            else this.actions.TogglePatch(k);            // 左键：多选切换
        };
        b.AddEvent(AtkEventType.MouseDown, handler);
        b.AddEvent(AtkEventType.MouseClick, handler);
    }

    /// <summary>点同一表头切换升降序；点新表头默认正序。</summary>
    private void SetSort(SortBy by)
    {
        if (this.sortActive && this.sortBy == by) this.sortAsc = !this.sortAsc;
        else { this.sortBy = by; this.sortAsc = true; }
        this.sortActive = true;
    }

    /// <summary>版本号必须转数值再比较：'10.0' 要排在 '6.0' 之后（字符串序会跑到 '2.0' 前）。</summary>
    private static float PatchNum(string? key) => key switch
    {
        "ARR" => 2f, "HW" => 3f, "SB" => 4f, "ShB" => 5f, "EW" => 6f, "DT" => 7f,
        _ => float.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f,
    };

    /// <summary>v1.1.96：按表头对快照排序。sortActive=false 时保持数据层「开闸优先」原顺序。</summary>
    private void SortHunts(List<MainHuntRow> hunts)
    {
        if (!this.sortActive || hunts.Count < 2) return;
        var by = this.sortBy;
        var asc = this.sortAsc;
        hunts.Sort((a, b) =>
        {
            var c = by switch
            {
                SortBy.Server => string.CompareOrdinal(a.World, b.World),
                SortBy.Version => PatchNum(a.Patch).CompareTo(PatchNum(b.Patch)),
                _ => a.PctValue.CompareTo(b.PctValue),
            };
            if (c != 0) return asc ? c : -c;
            return string.CompareOrdinal(a.Key, b.Key);   // 同值按 key 兜底，顺序稳定
        });
    }

    private void RefreshHeaders()
    {
        for (var i = 0; i < 3; i++)
        {
            var b = this.sortHeaders[i];
            if (b is null) continue;
            var by = (SortBy)i;
            var active = this.sortActive && this.sortBy == by;
            var arrow = active ? (this.sortAsc ? "↑" : "↓") : "↕";
            try { NativeButtonStyle.SortHeader(b, HeaderNames[i] + " " + arrow, HeaderH, active); }
            catch (Exception ex) { Log("[原生UI] 表头文本失败：" + ex.GetType().Name); b.IsVisible = false; }
        }
    }

    private void WireRightClick(TextButtonNode hit, int idx)
    {
        AtkEventListener.Delegates.ReceiveEvent handler = (_, _, _, _, data) =>
        {
            if (data is null) return;
            var btn = data->MouseData.ButtonId;
            if (btn == 1) this.RightClick(idx);
            else if (btn == 0) this.Select(idx);
        };
        hit.AddEvent(AtkEventType.MouseDown, handler);
        hit.AddEvent(AtkEventType.MouseClick, handler);
    }

    private void RightClick(int idx)
    {
        if (idx < 0 || idx >= this.rowKeys.Count) return;
        var key = this.rowKeys[idx];
        if (key.Length == 0) return;
        var tick = Environment.TickCount64;
        if (key == this.lastRightKey && tick - this.lastRightTick < 300) return;
        this.lastRightTick = tick;
        this.lastRightKey = key;
        this.ToggleDetails(idx);
    }

    private void Select(int idx)
    {
        if (idx < 0 || idx >= this.rowKeys.Count) return;
        var key = this.rowKeys[idx];
        if (key.Length > 0) this.selectedKey = key;
    }

    private void ToggleDetails(int idx)
    {
        if (idx < 0 || idx >= this.rowKeys.Count) return;
        var key = this.rowKeys[idx];
        if (key.Length == 0) return;
        this.selectedKey = key;
        this.expandedKey = string.Equals(this.expandedKey, key, StringComparison.Ordinal) ? "" : key;
        this.actions.Expand(this.expandedKey);
    }

    private void CollapseDetails()
    {
        if (this.expandedKey.Length == 0) return;
        this.expandedKey = "";
        this.actions.Expand("");
    }

    /// <summary>v1.2.2：详情面板只建一个浅灰文本标签；横向按钮由 EnsureDetailBtns 懒增长。</summary>
    private void BuildDetail()
    {
        if (this.detailBuilt) return;
        this.detailBuilt = true;
        try
        {
            this.detailLabel = new TextNode
            {
                Position = this.ContentStartPosition,
                Size = new Vector2(120f, DetailBtnH),
                FontSize = 12,
                FontType = FontType.Axis,
                AlignmentType = AlignmentType.Left,
                TextColor = MutedText,
                String = "",
            };
            this.detailLabel.AddTextFlags(TextFlags.MultiLine);
            this.detailLabel.AttachNode(this.content);
            this.detailCond = new TextNode
            {
                Position = this.ContentStartPosition,
                Size = new Vector2(120f, DetailBtnH),
                FontSize = 12,
                FontType = FontType.Axis,
                AlignmentType = AlignmentType.Left,
                TextColor = CondNeutral,
                String = "",
            };
            this.detailCond.AddTextFlags(TextFlags.MultiLine);
            this.detailCond.AttachNode(this.content);
            this.detailOk = true;
        }
        catch (Exception ex)
        {
            this.detailOk = false;
            Log("[原生UI] 详情面板降级：" + ex.GetType().Name);
        }
    }

    /// <summary>v1.2.2：详情横向按钮池懒增长；每个按钮 onClick 统一走 detailActions[i]，每帧重绑。</summary>
    private void EnsureDetailBtns(int need)
    {
        var p = this.ContentStartPosition;
        while (this.detailBtns.Count < need)
        {
            var idx = this.detailBtns.Count;
            this.detailActions.Add(null);
            this.detailLayout.Add((0f, 0f, 0f));
            var b = this.B(p, 60f, DetailBtnH, "", () => this.detailActions[idx]?.Invoke());
            this.detailBtns.Add(b);
            b.IsVisible = false;
        }
    }

    // ───────────── 选中行强调框（ColorImageNode 纯色，零纹理依赖） ─────────────

    /// <summary>
    /// v1.1.84：不再用 BorderNineGridNode（九宫格纹理在国服客户端不一定存在）。
    /// v1.1.94：四条 2px 的 ColorImageNode 拼出琥珀色边框；旧版用 ResNode 从未渲染过。
    /// </summary>
    /// <summary>v1.1.95：选中行 = 整行半透明底（最底层）+ 左侧 4px 琥珀竖条；不再画四边框。</summary>
    private void EnsureSelFrame()
    {
        if (this.selFrameLeft is not null) return;
        var amber = new Vector4(0.94f, 0.72f, 0.28f, 0.95f);

        // 底色必须挂到 content 的第一个子节点：ATK 按 child 链表顺序绘制，
        // 默认 AsLastChild 会把它盖在所有行文字之上（半透明也会让文字发灰）。
        this.selRowBg = new ColorImageNode
        {
            Position = Vector2.Zero,
            Size = new Vector2(RowTotalW, RowH),
            Color = SelBg,
        };
        this.selRowBg.AttachNode(this.content, NodePosition.AsFirstChild);
        this.selRowBg.IsVisible = false;

        // 左侧竖条：4px 宽、撑满整行 28px；Top/Bottom/Right 三条已删除。
        this.selFrameLeft = new ColorImageNode
        {
            Position = Vector2.Zero,
            Size = new Vector2(4f, RowH),
            Color = amber,
        };
        this.selFrameLeft.AttachNode(this.content);
        this.selFrameLeft.IsVisible = false;
    }

    /// <summary>v1.2.2：坐标按钮多行排布（超宽换行），末行右侧预留 actionReserve 给动作按钮；
    /// 若预留导致末行坐标回流则重算，直到行数稳定（动作按钮恒在末行最右侧，不单独占一行）。</summary>
    private List<(float x, int line)> LayCoordLines(
        List<(string text, Vector4 idle, Vector4 hover, Action act)> coords,
        float startX, float rightEdge, float actionReserve)
    {
        var tmp = new List<(float x, int line)>(coords.Count);
        if (coords.Count == 0) return tmp;
        int reserveLine = -1;   // -1：不预留；否则预留该行的右侧
        int guard = 0;
        while (guard++ <= coords.Count + 2)
        {
            tmp.Clear();
            float x = startX;
            int line = 0;
            float edge = (reserveLine == 0) ? rightEdge - actionReserve : rightEdge;
            for (int i = 0; i < coords.Count; i++)
            {
                float w = NativeButtonStyle.Measure(coords[i].text) + 16f;
                if (x + w > edge && x > startX + 0.5f)
                {
                    line++;
                    x = startX;
                    edge = (line == reserveLine) ? rightEdge - actionReserve : rightEdge;
                }
                tmp.Add((x, line));
                x += w + DetailGap;
            }
            if (reserveLine < 0)
            {
                // 第一遍（不预留）先求总行数，下一遍再预留末行右侧
                reserveLine = tmp.Count > 0 ? tmp[tmp.Count - 1].line : 0;
                continue;
            }
            int maxLine = 0;
            foreach (var p in tmp) if (p.line > maxLine) maxLine = p.line;
            if (maxLine <= reserveLine) break;   // 无坐标被挤到新行，稳定
            reserveLine = maxLine;               // 有坐标回流到新行，预留新末行再算
        }
        return new List<(float x, int line)>(tmp);
    }

    /// <summary>详情面板：固定表头 + 目的地列表（v1.1.95 去掉与列表行重复的 meta 与迷你条）。</summary>
    /// <summary>外层 Apply：调用 ApplyInner 并兜底，任何异常都保留窗口（组件降级）而非回退 ImGui。</summary>
    internal void Apply(MainSnapshot s)
    {
        if (!this.setupDone || !this.onSetupOk) return;
        try
        {
            this.ApplyInner(s);
        }
        catch (Exception ex)
        {
            // 关键兜底：异常发生在 ApplyInner 的前半段（构建/刷新）时，Reflow 还没跑过，
            // 所有节点会停在默认坐标叠成一坨。这里强制补跑一次布局，保证窗口永远排得开。
            try { this.Reflow(s, null); } catch { /* 布局自身也失败时放弃，保留上面的提示 */ }

            if (!this.applyLogged)
            {
                this.applyLogged = true;
                Log("[原生UI] Apply 异常（窗口保留，详见 /xllog）：" + ex);
            }
            // 把异常类型 + 首个堆栈帧显示出来（截图即可定位），完整堆栈见 /xllog
            try
            {
                var frame = "";
                var st = ex.StackTrace;
                if (!string.IsNullOrEmpty(st))
                {
                    var nl = st!.IndexOf('\n');
                    frame = (nl > 0 ? st[..nl] : st).Trim();
                }
                var msg = ex.GetType().Name + (frame.Length > 0 ? " @ " + frame : "");
                if (msg.Length > 120) msg = msg[..120];
                SetT(this.listTitle, msg);
                var em = ex.Message;
                if (em.Length > 120) em = em[..120];
                SetT(this.status, em);
            }
            catch { }
        }
    }

    /// <summary>把快照刷到节点上。全部在游戏主线程（IFramework.Update）执行。</summary>
    private void ApplyInner(MainSnapshot s)
    {
        if (!this.searchInitialized)
        {
            this.searchInitialized = true;
            this.suppressSearch = true;
            this.searchInput!.String = s.Search;
            this.suppressSearch = false;
        }
        if (!this.syncedExpand)
        {
            this.syncedExpand = true;
            this.actions.Expand(this.expandedKey);
        }

        // v1.1.85：每一块文本刷新都单独兜底。KTK 的 TextNode.String 在个别情况下会抛
        // NullReferenceException（栈顶 KamiToolKit.Nodes.TextNode.set_String），
        // 一旦逃出去就会打断整段 Apply。这里按块隔离，坏一块不影响其余与布局。
        try
        {
        NativeButtonStyle.ClearBadge(this.bClearSearch!, 24f);   // v1.1.97：搜索框内小 ×
        NativeButtonStyle.Apply(this.bDc!, string.IsNullOrEmpty(s.DcNext) ? s.Dc : $"{s.Dc}→{s.DcNext}",
                                130f, 26f, false);
        NativeButtonStyle.Apply(this.bRefresh!, s.Fetching ? "…" : "刷新", 80f, 26f, false);
        NativeButtonStyle.Apply(this.bAuto!, "前往", 96f, 26f, false);
        // v1.2.7：「完整」按钮指向已封死的旧 ImGui 窗口，隐藏（Reflow 里不再占位）。
        // v1.2.7：「收起」按钮指向已封死的旧 ImGui 小窗，隐藏。
        NativeButtonStyle.Apply(this.bStop!, "停止", 80f, 26f, false);
        NativeButtonStyle.Apply(this.bOnlyOpen!, s.OnlyOpen ? "✓开闸" : "开闸", 110f, 26f, s.OnlyOpen);
        NativeButtonStyle.Apply(this.bWorldMenu!, (this.menuOpen ? "▾ " : "▸ ") + s.WorldMenuLabel,
                                230f, 26f, this.menuOpen);
        NativeButtonStyle.MiniAction(this.bWorldAll!, "全服", 22f, false);
        NativeButtonStyle.MiniAction(this.bWorldClear!, "清空", 22f, false);
        NativeButtonStyle.MiniAction(this.bWorldCurrent!, "本服", 22f, false);
        NativeButtonStyle.FitText(this.counts!, s.Counts.Replace(" ", ""), 220f);
        NativeButtonStyle.HintTag(this.hintTag!, "右键详情", 100f, 18f);
        NativeButtonStyle.GhostButton(this.bSettings!, "设置", 24f, this.settingsHover);
        // v1.2.10 铁律：Apply 必须有节点就绪守卫，关窗重开期间 bHistory 可能尚未 OnSetup。
        if (this.bHistory is not null)
            NativeButtonStyle.GhostButton(this.bHistory, "历史", 24f, false);
        NativeButtonStyle.FitText(this.status!, s.Status, CW);
        }
        catch (Exception ex)
        {
            Log("[原生UI] 工具栏文本刷新失败（其余照常）：" + ex.GetType().Name);
        }

        // ── 大区单选（圆点样式，一次只选一个；选中即切大区 + 过滤列表）──
        if (s.Dcs.Count > 0 && !this.dcBuilt)
        {
            this.dcBuilt = true;
            this.dcNames.Clear();
            for (var i = 0; i < s.Dcs.Count && i < MaxDcButtons; i++) this.dcNames.Add(s.Dcs[i]);
        }
        for (var i = 0; i < MaxDcButtons; i++)
        {
            var b = this.dcButtons[i];
            if (b is null) continue;
            var has = i < this.dcNames.Count;
            b.IsVisible = has;
            if (!has) continue;
            var name = this.dcNames[i];
            var on = string.Equals(name, s.DcSelected, StringComparison.Ordinal);
            try { NativeButtonStyle.CompactRadio(b, name, 22f, on); }
            catch (Exception ex) { Log("[原生UI] 大区按钮文本失败：" + ex.GetType().Name); b.IsVisible = false; }
        }

        // ── 版本多选（左键切换 / 右键独奏；s.PatchOn 已是多选布尔表）──
        if (s.PatchKeys.Count > 0)
        {
            this.patchKeys.Clear();
            this.patchLabels.Clear();
            for (var i = 0; i < s.PatchKeys.Count && i < PatchCount; i++)
            {
                this.patchKeys.Add(s.PatchKeys[i]);
                this.patchLabels.Add(i < s.PatchLabels.Count ? s.PatchLabels[i] : s.PatchKeys[i]);
                this.patchKeysArr[i] = s.PatchKeys[i];
            }
            for (var i = 0; i < PatchCount; i++)
            {
                var b = this.patchButtons[i];
                if (b is null) continue;
                var has = i < this.patchLabels.Count;
                b.IsVisible = has;
                if (!has) continue;
                var on = i < s.PatchOn.Count && s.PatchOn[i];
                try { NativeButtonStyle.CompactRadio(b, this.patchLabels[i], 22f, on); }
                catch (Exception ex) { Log("[原生UI] 版本按钮文本失败：" + ex.GetType().Name); b.IsVisible = false; }
            }
        }

        for (var i = 0; i < WorldMenuMax; i++)
        {
            var name = i < s.Worlds.Count ? s.Worlds[i] : "";
            this.worldNames[i] = name;
            var on = i < s.WorldOn.Count && s.WorldOn[i];
            var mark = s.CurrentWorld.Length > 0 &&
                       string.Equals(name, s.CurrentWorld, StringComparison.OrdinalIgnoreCase) ? "◎" : on ? "●" : "○";
            var wb = this.worldMenuItems[i]!;
            try { NativeButtonStyle.Radio(wb, $"{mark} {name}", 140f, 24f, on); }
            catch (Exception ex) { Log("[原生UI] 服务器按钮文本失败：" + ex.GetType().Name); wb.IsVisible = false; }
        }

        // 选中行强调框（四条 ColorImageNode，惰性构建一次）
        this.EnsureSelFrame();

        this.SortHunts(s.Hunts);              // v1.1.96：表头排序（未激活时保持数据层原顺序）
        this.RefreshHeaders();
        var total = s.Hunts.Count;
        this.EnsureRows(total);

        try
        {
            NativeButtonStyle.FitText(this.listTitle!,
                s.Ready ? $"S 怪 {total}" : "数据未加载", 520f);
        }
        catch (Exception ex) { Log("[原生UI] 列表标题文本失败：" + ex.GetType().Name); }

        string? selRowKey = null;
        for (var i = 0; i < this.rows.Count; i++)
        {
            var rn = this.rows[i];
            var r = i < total ? s.Hunts[i] : null;
            this.rowKeys[i] = r?.Key ?? "";
            var vis = r is not null;
            // 逐个节点单独设置可见性：任何一个节点异常都不会连坐其余节点（旧版链式赋值
            // 一旦首个 setter 抛异常，整行节点就永远保持不可见 → 列表空白）。
            ShowNode(rn.Hit, vis);
            ShowNode(rn.Icon, vis);
            ShowNode(rn.World, vis);
            ShowNode(rn.Name, vis);
            ShowNode(rn.BarBg, vis);
            ShowNode(rn.BarFg, vis);
            ShowNode(rn.Tail, vis);
            ShowNode(rn.Badge, vis && r is { Triggered: true });
            if (r is null) continue;

            var selected = string.Equals(r.Key, this.selectedKey, StringComparison.Ordinal);
            if (selected) selRowKey = r.Key;

            // 逐语句隔离：坏一条只丢那一项（并只在首次出现时打印完整堆栈到 /xllog）
            RowStep("底纹", () =>
            {
                // v1.1.95：九宫格底纹恒隐藏，选中行背景由 selRowBg(ColorImageNode) 统一承担
                rn.Hit.BackgroundNode.IsVisible = false;
            }, r.En);
            RowStep("图标", () =>
            {
                SetT(rn.Icon, "◆");
                rn.Icon.TextColor = IconColor(r.State);
            }, r.En);
            RowStep("服务器", () => SetT(rn.World, TrimWorld(r.World)), r.En);
            RowStep("怪名", () => SetT(rn.Name, r.RowLabel), r.En);
            RowStep("血条", () =>
            {
                var prog0 = r.State == "unknown" ? 0f : Math.Clamp((float)r.PctValue / 100f, 0f, 1f);
                rn.FillW = this.PaintBar(rn.BarBg, rn.BarFg, BarW, BarH, prog0);
            }, r.En);
            RowStep("百分比", () => SetT(rn.Tail, r.Pct), r.En);
            // v1.2.12：被抢触的行，怪名右侧挂一个红色「已触发」（文字在构造期已写好，这里只保色）
            RowStep("已触发红标", () => rn.Badge.TextColor = TrigRed, r.En);
        }

        // ── 详情（v1.2.2：单行横向按钮 —— 目的地与操作 → [坐标] → [大水晶] → [执行触发/自动前往]）──
        if (!this.detailBuilt) this.BuildDetail();
        var expanded = this.expandedKey.Length > 0
            ? s.Hunts.Find(x => string.Equals(x.Key, this.expandedKey, StringComparison.Ordinal))
            : null;
        var detailVis = expanded is not null && this.detailOk;
        try
        {
            if (this.detailLabel is not null) this.detailLabel.IsVisible = detailVis;
            if (this.detailCond is not null) this.detailCond.IsVisible = false;
            for (var i = 0; i < this.detailBtns.Count; i++) this.detailBtns[i].IsVisible = false;
            this.detailLineCount = 0;

            if (detailVis && expanded is not null)
            {
                // 收集坐标按钮（每颗绿点一个）与动作按钮（大水晶 + 执行触发/自动前往）
                var coordItems = new List<(string text, Vector4 idle, Vector4 hover, Action act)>();
                foreach (var pt in expanded.Points)
                {
                    if (pt.Aetheryte || pt.Special) continue;   // 坐标按钮：只放绿点坐标，不插大水晶/特殊
                    coordItems.Add((
                        $"({pt.X:0.0}, {pt.Y:0.0})",
                        CoordIdle, CoordHover,
                        (Action)(() => this.actions.Flag(expanded.Map, pt.X, pt.Y))));
                }
                var actionItems = new List<(string text, Vector4 idle, Vector4 hover, Action act)>();
                if (expanded.Points.Any(p => p.Aetheryte))
                    actionItems.Add(("大水晶", AetIdle, AetHover,
                        (Action)(() => this.actions.FlagAetheryte(expanded.Map, expanded.Cn))));
                // 执行触发（有脚本）/ 自动前往（纯寻路）：都走 MainPrimary，内部自动区分特殊触发与基础寻路
                // v1.2.12：已被他人触发 → 灰字「已被抢触」，点了也只会提示、不会动身
                // （MainPrimary 里还有最后一道拦截，防止从别的入口绕进来）。
                actionItems.Add(expanded.Triggered
                    ? ("已被抢触", ActBlocked, ActBlocked,
                        (Action)(() => this.actions.Primary(expanded.En, expanded.World)))
                    : (expanded.HasSpecial ? "执行触发" : "自动前往", ActIdle, ActHover,
                        (Action)(() => this.actions.Primary(expanded.En, expanded.World))));

                                // 先看本怪有没有特殊触发条件（天气/时间/月相）—— 有则条件标签钉最左，其余右移
                var condInfo = HuntConditionHelper.GetCondition(expanded.En);
                float condW = 0f;
                if (condInfo is not null && this.detailCond is not null)
                {
                    this.detailCond.IsVisible = true;
                    this.detailCond.String = condInfo.Text;
                    this.detailCond.TextColor = condInfo.Met switch { true => CondOk, false => CondBad, _ => CondNeutral };
                    condW = NativeButtonStyle.Measure(condInfo.Text) + 6f;
                    this.detailCondRelX = DetailIndent;
                    this.detailCondRelY = 0f;
                    this.detailCondW = condW;
                }

                // “目的地与操作”浅灰文本：条件存在时右移给条件让位，否则贴最左
                this.detailLabelW = NativeButtonStyle.Measure("目的地与操作") + 6f;
                this.detailLabelRelX = DetailIndent + (condW > 0f ? condW + DetailGap : 0f);
                this.detailLabelRelY = 0f;
                var startX = this.detailLabelRelX + this.detailLabelW + DetailGap;
                var rightEdge = CW - 8f;


                // 动作按钮总宽（含相互间距，末个无尾距）；末行右侧据此预留，避免动作按钮被挤到独立行
                float aw = 0f;
                foreach (var a in actionItems) aw += NativeButtonStyle.Measure(a.text) + 16f + DetailGap;
                if (aw > 0f) aw -= DetailGap;
                float actionReserve = aw > 0f ? aw + DetailGap : 0f;

                // 坐标按钮多行排布（超宽换行），末行右侧预留动作区；返回每个坐标的 (x, line)
                var coordPos = this.LayCoordLines(coordItems, startX, rightEdge, actionReserve);
                int lastLine = coordPos.Count > 0 ? coordPos.Max(p => p.line) : 0;
                int totalLines = lastLine + 1;

                // 动作按钮固定在“末行”最右侧（rightEdge - aw），与最后一个坐标同行，绝不单独掉到下一行
                float ax = Math.Max(rightEdge - aw, startX);

                this.EnsureDetailBtns(coordItems.Count + actionItems.Count);
                int bi = 0;
                foreach (var cp in coordPos)
                {
                    var it = coordItems[bi];
                    float w = NativeButtonStyle.Measure(it.text) + 16f;
                    var b = this.detailBtns[bi];
                    b.IsVisible = true;
                    NativeButtonStyle.SizePin(b, w, DetailBtnH);
                    NativeButtonStyle.HideBackground(b);
                    NativeButtonStyle.Paint(b, it.idle, it.hover);
                    NativeButtonStyle.SetLabel(b, it.text);
                    this.detailActions[bi] = it.act;
                    this.detailLayout[bi] = (cp.x, cp.line * DetailBtnH, w);
                    bi++;
                }
                foreach (var ait in actionItems)
                {
                    float w = NativeButtonStyle.Measure(ait.text) + 16f;
                    var b = this.detailBtns[bi];
                    b.IsVisible = true;
                    NativeButtonStyle.SizePin(b, w, DetailBtnH);
                    NativeButtonStyle.HideBackground(b);
                    NativeButtonStyle.Paint(b, ait.idle, ait.hover);
                    NativeButtonStyle.SetLabel(b, ait.text);
                    this.detailActions[bi] = ait.act;
                    this.detailLayout[bi] = (ax, lastLine * DetailBtnH, w);
                    ax += w + DetailGap;
                    bi++;
                }
                this.detailLineCount = totalLines;
            }
        }
        catch (Exception ex)
        {
            this.detailOk = false;
            Log("[原生UI] 详情内容更新失败：" + ex.GetType().Name);
        }

        this.Reflow(s, selRowKey);
    }

    private static string TrimWorld(string? w)
        => string.IsNullOrEmpty(w) ? "" : w!.Length <= 5 ? w : w[..5];

    /// <summary>水晶图标颜色承担状态区分，血条本身不再按状态变色。</summary>
    private static Vector4 IconColor(string st) => st switch
    {
        "open" => new Vector4(0.38f, 0.95f, 0.55f, 1f),
        "cd" => new Vector4(0.30f, 0.75f, 1.00f, 1f),
        "over" => new Vector4(0.52f, 0.56f, 0.62f, 1f),
        _ => new Vector4(0.42f, 0.45f, 0.50f, 1f),
    };

    private static string StateCn(string st) => st switch
    {
        "open" => "开闸",
        "cd" => "冷却",
        "over" => "过窗",
        _ => "无记录",
    };

    /// <summary>v1.1.96：固定层（工具区 + 排序表头）不滚动，只有列表在 ScrollingNode 内滚动。</summary>
    private void Reflow(MainSnapshot s, string? selRowKey)
    {
        var p = this.ContentStartPosition;
        var scrollW = W - p.X;                    // ★ 让滚动条贴窗口最右缘，而不是贴内容右缘
        float fy = 0f;                            // fixedRoot 内部 y（屏幕固定）
        float ly = 0f;                            // content 内部 y（随列表滚动）
        Vector2 At(float x, float dy) => new Vector2(x, dy);

        // ① v1.1.98：搜索框（左）+ × 内嵌 + 设置齿轮 + 刷新（右），统一 TopInset
        this.searchInput!.Position = At(TopInset, fy);
        this.bClearSearch!.Position = At(TopInset + SearchW - 26f, fy + 2f);
        var br = this.bRefresh!;
        br.Position = At(CW - br.Width, fy);
        var gear = this.bSettings!;
        gear.Position = At(CW - br.Width - gear.Width - 6f, fy + 1f);
        // v1.2.12：[历史] 紧贴齿轮左侧，右上顺序 → [历史] [设置] [刷新]
        if (this.bHistory is { } hist)
            hist.Position = At(CW - br.Width - gear.Width - hist.Width - 12f, fy + 1f);
        this.bDc!.IsVisible = false;
        // ★ 非豆豆柴大区：顶行右侧空白显示浅灰提示，数据源为公开熊站、只能手动刷新
        if (s.NeedsManualRefresh)
        {
            this.bManualHint!.IsVisible = true;
            this.bManualHint.String = s.Fetching ? "正在从熊站拉取…" : "当前大区数据需手动刷新";
            this.bManualHint.Position = At(262f, fy + 3f);
        }
        else
        {
            this.bManualHint!.IsVisible = false;
        }
        fy += 26f + RowGap;

        // ② v1.1.98：大区（靠左）+ 版本（靠右）合并同一行；空间不足时版本自动换下一行。
        //   CompactRadio 把按钮宽从「文字+36」压到「文字+8」，两组才放得进 576。
        {
            var dcW = 0f;
            for (var i = 0; i < MaxDcButtons; i++)
            {
                var b = this.dcButtons[i];
                if (b is null || !b.IsVisible) continue;
                dcW += b.Width + 6f;
            }
            if (dcW > 0f) dcW -= 6f;

            var patchW = 0f;
            for (var i = 0; i < PatchCount; i++)
            {
                var b = this.patchButtons[i];
                if (b is null || !b.IsVisible) continue;
                patchW += b.Width + 6f;
            }
            if (patchW > 0f) patchW -= 6f;

            var patchX = CW - patchW;
            var sameLine = patchX >= TopInset + dcW + 12f;

            var dx = TopInset;
            for (var i = 0; i < MaxDcButtons; i++)
            {
                var b = this.dcButtons[i];
                if (b is null || !b.IsVisible) continue;
                b.Position = At(dx, fy);
                dx += b.Width + 6f;
            }
            var py = sameLine ? fy : fy + 22f + RowGap;
            var px = sameLine ? patchX : TopInset;
            for (var i = 0; i < PatchCount; i++)
            {
                var b = this.patchButtons[i];
                if (b is null || !b.IsVisible) continue;
                b.Position = At(px, py);
                px += b.Width + 6f;
            }
            fy = py + 22f + RowGap;
        }

        // ④ ★ v1.1.97：[全服][清空][本服] 折叠态隐藏；展开态直接挤在「服务器 5/5」
        //   左侧同一行（合并成一个控件），空间不够才降级到下一行，绝不重叠。
        {
            var rowY = fy;
            var lx = TopInset;              // v1.1.98：与搜索框/大区同一左边距
            void Tool2(TextButtonNode b2)
            {
                b2.Position = At(lx, rowY);
                lx += b2.Width + 4f;
            }
            Tool2(this.bAuto!);
            // v1.2.7：「完整 / 收起」按钮已停用，既不参与布局也不绘制。
            this.bLegacy!.IsVisible = false;
            this.bMini!.IsVisible = false;
            Tool2(this.bStop!);
            Tool2(this.bOnlyOpen!);

            var wm = this.bWorldMenu!;
            var wmX = CW - wm.Width;                       // 服务器按钮靠右对齐
            wm.Position = At(wmX, rowY);

            var wa = this.bWorldAll!;
            var wc = this.bWorldClear!;
            var wcur = this.bWorldCurrent!;
            wa.IsVisible = wc.IsVisible = wcur.IsVisible = this.menuOpen;
            var actionW = wa.Width + wc.Width + wcur.Width + 10f;
            var ax = wmX - actionW - 10f;
            if (this.menuOpen && ax >= lx + 6f)
            {
                wa.Position = At(ax, rowY + 2f); ax += wa.Width + 5f;
                wc.Position = At(ax, rowY + 2f); ax += wc.Width + 5f;
                wcur.Position = At(ax, rowY + 2f);
                this.counts!.IsVisible = false;             // 展开时让位，收起后恢复
                fy = rowY + 26f + RowGap;
            }
            else if (this.menuOpen)
            {
                var ay = rowY + 26f + 7f;                   // 降级：独占一行，间距 7px
                ax = TopInset;
                wa.Position = At(ax, ay); ax += wa.Width + 6f;
                wc.Position = At(ax, ay); ax += wc.Width + 6f;
                wcur.Position = At(ax, ay);
                this.counts!.IsVisible = false;
                fy = ay + MathF.Max(wa.Height, MathF.Max(wc.Height, wcur.Height)) + 7f;
            }
            else
            {
                var countsX = wmX - this.counts!.Width - 10f;
                this.counts.Position = At(countsX, rowY + 4f);
                this.counts.IsVisible = countsX >= lx + 4f;
                fy = rowY + 26f + RowGap;
            }
        }

        // ⑤ 服务器圆点列表（仅展开时占高度）；与上方操作栏拉开 7~8px，行距 4px。
        {
            var mx = TopInset;
            var my = fy;
            var menuRowH = 0f;
            var menuVis = 0;
            for (var i = 0; i < WorldMenuMax; i++)
            {
                var b = this.worldMenuItems[i]!;
                var vis = this.menuOpen && i < s.Worlds.Count && s.Worlds[i].Length > 0;
                b.IsVisible = vis;
                if (!vis) continue;
                if (mx + b.Width > CW && mx > 0f) { my += menuRowH + 4f; mx = 0f; menuRowH = 0f; }
                b.Position = At(mx, my);
                mx += b.Width + 6f;
                menuRowH = MathF.Max(menuRowH, b.Height);
                menuVis++;
            }
            if (this.menuOpen && menuVis > 0) fy = my + menuRowH + 7f;
        }

        // ⑥ 列表标题 + 「右键详情」小标签
        this.listTitle!.Position = At(TopInset, fy);
        this.hintTag!.Position = At(TopInset + this.listTitle.Width + 10f, fy + 1f);
        fy += MathF.Max(22f, this.hintTag.Height + 2f);

        // ⑦ ★ 排序表头：x 与数据列严格对齐；挂在 fixedRoot，滚动列表时不会消失
        for (var i = 0; i < 3; i++)
        {
            var hx = i == 0 ? WorldX : i == 1 ? NameX : BarX;
            this.sortHeaders[i]!.Position = At(hx, fy);
        }
        fy += HeaderH + 4f;
        var fixedH = fy;

        // ── 以下全部在 content 内，随列表滚动 ──
        float selY = -1f;
        float firstBarY = -1f;
        var rowTotal = s.Hunts.Count;
        for (var i = 0; i < this.rows.Count; i++)
        {
            var rn = this.rows[i];
            if (i >= rowTotal)
            {
                ShowNode(rn.Hit, false);
                ShowNode(rn.Icon, false);
                ShowNode(rn.World, false);
                ShowNode(rn.Name, false);
                ShowNode(rn.BarBg, false);
                ShowNode(rn.BarFg, false);
                ShowNode(rn.Tail, false);
                ShowNode(rn.Badge, false);
                continue;
            }
            ShowNode(rn.Hit, true);
            ShowNode(rn.Icon, true);
            ShowNode(rn.World, true);
            ShowNode(rn.Name, true);
            ShowNode(rn.Tail, true);
            ShowNode(rn.Badge, s.Hunts[i].Triggered);
            // ★ v1.1.98：滚动区内所有行节点 Alpha 恒为 1，杜绝「越往下越淡」
            KeepOpaque(rn.Hit); KeepOpaque(rn.Icon); KeepOpaque(rn.World);
            KeepOpaque(rn.Name); KeepOpaque(rn.Tail);
            KeepOpaque(rn.BarBg); KeepOpaque(rn.BarFg);
            rn.Hit.Position = At(0f, ly);
            rn.Icon.Position = At(IconX, ly + 3f);
            rn.World.Position = At(WorldX, ly + 4f);
            rn.Name.Position = At(NameX, ly + 4f);
            rn.Badge.Position = At(NameX + NameW - BadgeW - 2f, ly + 4f);
            KeepOpaque(rn.Badge);
            var by = ly + (RowH - BarH) / 2f;
            if (firstBarY < 0f) firstBarY = by;

            if (CanaryBar)
            {
                ShowNode(rn.BarBg, true);
                ShowNode(rn.BarFg, false);
                rn.BarBg.Position = At(BarX, by);
                rn.BarFg.Position = At(BarX, by);
                rn.BarBg.Size = new Vector2(BarW, BarH);
                rn.BarBg.Color = CanaryRed;
                rn.BarFg.Size = new Vector2(0f, BarH);
            }
            else
            {
                ShowNode(rn.BarBg, true);
                ShowNode(rn.BarFg, true);
                rn.BarBg.Position = At(BarX + rn.FillW, by);
                rn.BarFg.Position = At(BarX, by);
            }
            rn.Tail.Position = At(TailX, ly + 5f);
            if (string.Equals(this.rowKeys[i], selRowKey, StringComparison.Ordinal)) selY = ly;
            ly += RowH;

            if (!string.Equals(this.rowKeys[i], this.expandedKey, StringComparison.Ordinal) ||
                this.expandedKey.Length == 0) continue;

            // v1.2.2：详情单行横向按钮（有展开怪时才占高）
            if (this.detailLineCount > 0 && this.detailLabel is not null)
            {
                this.detailLabel.Position = At(this.detailLabelRelX, ly + this.detailLabelRelY);
                this.detailLabel.Size = new Vector2(this.detailLabelW, DetailBtnH);
                if (this.detailCond is not null && this.detailCond.IsVisible)
                {
                    this.detailCond.Position = At(this.detailCondRelX, ly + this.detailCondRelY);
                    this.detailCond.Size = new Vector2(this.detailCondW, DetailBtnH);
                }
                for (var j = 0; j < this.detailBtns.Count; j++)
                {
                    var b = this.detailBtns[j];
                    if (!b.IsVisible || j >= this.detailLayout.Count) continue;
                    var lay = this.detailLayout[j];
                    b.Position = At(lay.rx, ly + lay.ry);
                }
                ly += this.detailLineCount * DetailBtnH + 4f;
            }
        }

        if (CanaryBar)
        {
            var cy = firstBarY >= 0f ? firstBarY : 0f;
            var co = this.canaryOut;
            if (co is not null)
            {
                co.Position = At(CanaryOutX, cy);
                co.Size = new Vector2(CanaryOutW, BarH);
                co.Color = CanaryRed;
                co.IsVisible = true;
            }
            if (!this.canaryLogged && rowTotal > 0)
            {
                this.canaryLogged = true;
                try
                {
                    var bg0 = this.rows.Count > 0 ? this.rows[0].BarBg : null;
                    Log("[原生UI] 金丝雀诊断 " +
                        $"主条=({(bg0 is not null ? bg0.X : -1f):F1},{(bg0 is not null ? bg0.Y : -1f):F1}," +
                        $"{(bg0 is not null ? bg0.Width : -1f):F0}x{(bg0 is not null ? bg0.Height : -1f):F0}," +
                        $"vis={(bg0 is not null && bg0.IsVisible)},color=红) " +
                        $"对照条=({(co is not null ? co.X : -1f):F1},{(co is not null ? co.Y : -1f):F1}," +
                        $"{(co is not null ? co.Width : -1f):F0}x{(co is not null ? co.Height : -1f):F0}," +
                        $"vis={(co is not null && co.IsVisible)}) " +
                        $"内容宽={this.content!.Width:F0} 内容可见={this.content!.IsVisible} " +
                        $"内容DrawFlags={this.content!.DrawFlags}");
                }
                catch { /* 诊断日志失败绝不打断布局 */ }
            }
        }

        // ⑨ 选中行：整行半透明底 + 左侧 4px 琥珀竖条
        if (this.selRowBg is not null && this.selFrameLeft is not null)
        {
            var frameVis = selY >= 0f;
            this.selRowBg.IsVisible = frameVis;
            this.selFrameLeft.IsVisible = frameVis;
            if (frameVis)
            {
                this.selRowBg.Position = At(0f, selY);
                this.selRowBg.Size = new Vector2(RowTotalW, RowH);
                this.selFrameLeft.Position = At(0f, selY);   // v1.1.97：0..4，不再压到 ◆ 上
                this.selFrameLeft.Size = new Vector2(4f, RowH);
            }
        }

        // ⑩ 底部状态（跟随列表）
        this.status!.Position = At(0f, ly);
        ly += 20f;

        var listContentH = ly + BottomPad;
        // ★ v1.1.98：窗口外框固定 FixedWindowH，viewH 只由固定头部高度决定，
        //   与列表行数完全无关 —— 1 只怪和 100 只怪的窗口一模一样，不再抖动/裁剪。
        // ★ v1.2.00：视口夹在 [160, 600]。上限 600 保证再长的列表也只在容器内滚动，
        //   顶部/底部永远留白，行节点绝不会延伸出窗口画到游戏背景上。
        var viewH = FixedWindowH - p.Y - fixedH - BottomPad;
        if (viewH > MaxListViewH) viewH = MaxListViewH;
        if (viewH < MinListViewH) viewH = MinListViewH;

        this.fixedRoot!.Size = new Vector2(CW, fixedH);
        this.scroll!.Position = new Vector2(p.X, p.Y + fixedH);
        if (MathF.Abs(viewH - this.lastViewH) >= 0.5f ||
            MathF.Abs(scrollW - this.lastScrollW) >= 0.5f)
        {
            this.lastViewH = viewH;
            this.lastScrollW = scrollW;
            this.scroll.Size = new Vector2(scrollW, viewH);
        }
        // ★ 垫底：ContentNode 至少撑满视口，行少时下半部也不会空白。
        this.content!.Height = MathF.Max(listContentH, viewH);

        // ★ v1.2.00：裁剪区显式对齐视口。KTK 只在 scroll.OnSizeChanged 时写一次
        //   ClippingContentNode.Size，视口没变化就会漏同步；这里每帧钉死，
        //   超出视口的行一律被切断（ClipRegion），不会漏到窗口外。
        var clipSize = new Vector2(scrollW, viewH);
        this.scroll.ClippingContentNode.Size = clipSize;
        this.scroll.ScrollingCollisionNode.Size = clipSize;

        // ★ 滚动条接管溢出：钉在 scroll 最右缘、撑满固定视口，并显式开启滚轮。
        var sb = this.scroll.ScrollBarNode;
        sb.Position = new Vector2(scrollW - ScrollBarTrackW, 0f);
        sb.Size = new Vector2(ScrollBarTrackW, viewH);
        sb.IsAcceptingMouseWheelEvents = true;
        sb.UpdateScrollParams();          // Size setter 会重设 Background/Foreground，须重算滑块

        // 自画轨道与 ScrollBarNode 同父（scroll）、同 x/y，一直画到窗口底部内缘。
        this.trackNode!.Position = new Vector2(scrollW - ScrollBarTrackW, 0f);
        this.trackNode.Size = new Vector2(ScrollBarTrackW, viewH + BottomPad);
        this.trackNode.IsVisible = sb.IsVisible;

        // ★ 绝不再写 this.Size：窗口尺寸只在 OnSetup 用 SetWindowSize(FixedWindowH) 设一次。
        //   WindowNode 背景板与外框始终同高，杜绝「下半部透出白底 / 文字看似渐隐」。
    }

    internal bool SuppressClosed;

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        base.OnHide(addon);
        // v1.2.9：过图 hide 如实回写开关（该关就关），落地后由窗口快照延迟恢复；
        // 程序内主动 Close() 时由 Runtime 置 SuppressClosed，避免误写。
        if (!this.SuppressClosed) this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        this.setupDone = false;
    }
}
