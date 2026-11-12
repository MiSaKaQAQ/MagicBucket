using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.2.7：极简原生 KTK 设置面板。
// v1.2.6 把旧版 ImGui 设置窗彻底封死后，主窗齿轮成了空操作；这里用纯原生节点补回来。
// 刻意不用任何带纹理 / 国服常缺失的组件（ProgressBar / Icon / BorderNineGrid / ListBox ...），
// 只有 TextNode + TextButtonNode，和 HudWindow 同一套已验证的安全节点集。
// 快照与委托里不含任何 KTK 类型，Plugin 侧不引用 KTK DLL。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>设置面板纯数据快照（每 200ms 同步一次）。</summary>
internal sealed class SettingsSnapshot
{
    internal bool ShowMain;
    internal bool ShowAuto;
    internal bool ShowHud;
    internal bool AutoSummon;
    internal bool AutoLeave;
    internal bool ConfirmReturn;
    internal bool LightTheme;
    internal string Status = "";
}

/// <summary>设置面板回调（行 key 见 SettingsWindow.Keys）。</summary>
internal sealed class SettingsActions
{
    internal Action<string> Toggle = _ => { };
    internal Action Refresh = () => { };
    internal Action Closed = () => { };
}

/// <summary>原生设置窗口：标题 + 7 行开关 + 立即刷新 + 关闭。</summary>
internal sealed class SettingsWindow : NativeAddon
{
    internal const float W = 420f;
    internal const float H = 430f;
    private const float CW = 396f;

    private static readonly Vector4 MutedText = new(0.52f, 0.56f, 0.62f, 1f);

    // 每行：(key, 标题)
    internal static readonly (string Key, string Label)[] Rows =
    {
        ("main",   "显示主窗口"),
        ("auto",   "显示自动前往窗口"),
        ("hud",    "显示触发悬浮窗"),
        ("summon", "自动召唤随从"),
        ("leave",  "独身自动退本"),
        ("return", "倒地自动确认返回"),
        ("theme",  "亮色主题"),
    };

    internal static Action<string>? Logger;
    private static void Log(string m)
    {
        try { Logger?.Invoke("[SrankFlag.UI] " + m); } catch { }
    }

    private readonly SettingsActions actions;
    private readonly TextButtonNode?[] rows = new TextButtonNode?[Rows.Length];
    private TextNode? title, hint, status;
    private TextButtonNode? bRefresh, bClose;

    internal SettingsWindow(SettingsActions actions) => this.actions = actions;

    internal bool SuppressClosed;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        SetWindowSize(new Vector2(W, H));
        var p = this.ContentStartPosition;

        this.title = this.T(p, CW, 22f, 14, "魔法水桶 · 设置");
        this.title.TextColor = MutedText;

        for (var i = 0; i < Rows.Length; i++)
        {
            var key = Rows[i].Key;      // 闭包只捕获 key 字符串，不捕获循环变量 i
            this.rows[i] = this.B(p, CW, 28f, Rows[i].Label, () => this.actions.Toggle(key));
        }

        this.status = this.T(p, CW, 18f, 11, "");
        this.status.TextColor = MutedText;

        this.bRefresh = this.B(p, 160f, 26f, "立即刷新数据", () => this.actions.Refresh());
        this.bClose = this.B(p, 96f, 26f, "关闭", () => this.actions.Closed());

        this.hint = this.T(p, CW, 18f, 11, "点击任意一行切换 · 改动立即生效");
        this.hint.TextColor = MutedText;
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
        n.AttachNode(this);
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
        b.AttachNode(this);
        return b;
    }

    /// <summary>把快照刷到节点上；由 IFramework.Update 每 200ms 在游戏主线程调用。</summary>
    internal void Apply(SettingsSnapshot s)
    {
        try
        {
            bool[] on =
            {
                s.ShowMain, s.ShowAuto, s.ShowHud, s.AutoSummon,
                s.AutoLeave, s.ConfirmReturn, s.LightTheme,
            };

            var p = this.ContentStartPosition;
            var y = 0f;
            if (this.title is not null)
            {
                this.title.Position = p + new Vector2(0f, y);
                y += 28f;
            }

            for (var i = 0; i < Rows.Length; i++)
            {
                var b = this.rows[i];
                if (b is null) continue;
                NativeButtonStyle.Apply(b, Rows[i].Label + (on[i] ? " · 开" : " · 关"), CW, 28f, on[i]);
                b.Position = p + new Vector2(0f, y);
                y += b.Height + 6f;
            }

            y += 4f;
            if (this.status is not null)
            {
                this.status.String = s.Status.Length > 0 ? s.Status : " ";
                this.status.Position = p + new Vector2(0f, y);
                y += 22f;
            }

            if (this.bRefresh is not null)
            {
                NativeButtonStyle.Apply(this.bRefresh, "立即刷新数据", 170f, 26f, false);
                this.bRefresh.Position = p + new Vector2(0f, y);
            }
            if (this.bClose is not null)
            {
                NativeButtonStyle.Apply(this.bClose, "关闭", 100f, 26f, false);
                this.bClose.Position = p + new Vector2(CW - this.bClose.Width, y);
            }
            y += 30f;

            if (this.hint is not null) this.hint.Position = p + new Vector2(0f, y);
        }
        catch (Exception ex)
        {
            Log("[原生UI] 设置面板 Apply 失败：" + ex.GetType().Name);
        }
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        base.OnHide(addon);
        // v1.2.8 强锁：会话中被过图 hide 时不回写，由 ForceReopenAll 复活；
        // 面板内「关闭」按钮走 actions.Closed() 直接置 settingsWindowOpen=false，仍可正常关闭。
        if (Plugin.IsAutoRunning)
        {
            Plugin.NativeReopenMask |= 8;   // bit3 = 设置面板
            return;
        }
        // 程序内主动 Close() 时由 Runtime 置 SuppressClosed，避免把开关回写成 false 后又重开。
        if (!this.SuppressClosed) this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        for (var i = 0; i < this.rows.Length; i++) this.rows[i] = null;
        this.title = this.hint = this.status = null;
        this.bRefresh = this.bClose = null;
    }
}
