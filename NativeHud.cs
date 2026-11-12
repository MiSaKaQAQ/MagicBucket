using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.1.71 试点 / v1.1.72 扩展：把「触发中」与「自动前往」两个窗口换成游戏原生窗口。
// KTK 相关类型集中在 NativeHud.cs + NativeAutoWindow.cs；整体撤销：
//   把 NativeHudRuntime.Enabled 改成 false → 两个窗口都回退 ImGui；
//   只想撤「自动前往」→ 把 NativeHudRuntime.EnabledAuto 改成 false。
// KamiToolKitLibrary 是全局静态，只能初始化 / 释放一次，所以两个窗口共用本 Runtime。
// 运行期依赖：插件目录需有 KamiToolKit.dll + FFXIVClientStructs.Expansion.dll。
// 任一窗口初始化 / 运行失败只停用那一个（failed / autoFailed），另一个照常工作。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>「触发中」悬浮窗的纯数据快照。刻意不含任何 KTK 类型。</summary>
internal sealed class HudSnapshot
{
    internal string Badge = "";
    internal string Progress = "";
    internal string Job = "";
    internal string Map = "";
    internal string Next = "";
    internal string Status = "";
    internal string Primary = "";
}

/// <summary>原生窗口按钮回调用的委托集合，同样不含 KTK 类型。</summary>
internal sealed class HudActions
{
    internal Action Primary = () => { };
    internal Action Skip = () => { };
    internal Action Stop = () => { };
    internal Action Restart = () => { };
    internal Action Closed = () => { };
}

/// <summary>
/// KTK 运行时桥（三个原生窗口共用一次 KTK 初始化）。生命周期：
///   BeginInit（插件构造，异步启动 InitializeAsync）
///   → Tick / TickAuto / TickMain（每帧，框架主线程）→ Shutdown。
/// HUD 异常置 failed，自动前往置 autoFailed，主窗置 mainFailed，各自回退对应 ImGui 窗口。
/// </summary>
internal sealed class NativeHudRuntime
{
    /// <summary>一键停用全部原生窗口，回到原来的 ImGui 版。</summary>
    internal const bool Enabled = true;
    /// <summary>只停用「自动前往」原生窗口。</summary>
    internal const bool EnabledAuto = true;
    /// <summary>只停用「主窗口」原生窗口（回到原 ImGui 完整版）。</summary>
    internal const bool EnabledMain = true;

    private readonly HudActions actions;
    private readonly AutoWindowActions autoActions;
    private readonly MainActions mainActions;
    private readonly Action<string> log;
    private Task? initTask;
    private HudWindow? window;
    private AutoWindow? autoWindow;
    private MainWindow? mainWindow;
    private bool failed;
    private bool autoFailed;
    private bool mainFailed;
    private bool disposed;
    private DateTime lastSyncUtc = DateTime.MinValue;
    private DateTime lastAutoSyncUtc = DateTime.MinValue;
    private DateTime lastMainSyncUtc = DateTime.MinValue;

    internal NativeHudRuntime(HudActions actions, AutoWindowActions autoActions,
                              MainActions mainActions, Action<string> log)
    {
        this.actions = actions;
        this.autoActions = autoActions;
        this.mainActions = mainActions;
        this.log = log;
        MainWindow.Logger = log;   // 让主窗内部的异常/降级日志也进 /xllog
    }

    /// <summary>「触发中」原生窗口已接管（此时 DrawAutoHud 不再画 ImGui）。</summary>
    internal bool Active => Enabled && !this.failed && !this.disposed && this.window is not null;

    /// <summary>「自动前往」原生窗口已接管（此时 DrawAutoWindow 不再画 ImGui）。</summary>
    internal bool ActiveAuto => Enabled && EnabledAuto && !this.failed && !this.autoFailed &&
                                !this.disposed && this.autoWindow is not null;

    /// <summary>「主窗口」原生窗口已接管（此时 DrawMainWindow 跳过 ImGui 主窗）。</summary>
    internal bool ActiveMain => Enabled && EnabledMain && !this.failed && !this.mainFailed &&
                                !this.disposed && this.mainWindow is not null;

    /// <summary>KTK 初始化是否已经成功完成；失败时整体回退 ImGui。</summary>
    private bool InitReady()
    {
        var t = this.initTask;
        if (t is null || !t.IsCompleted) return false;
        if (t.IsFaulted)
        {
            this.failed = true;
            this.log($"[原生UI] 初始化异常，全部回退 ImGui：{t.Exception}");
            return false;
        }
        return true;
    }

    internal void BeginInit(IDalamudPluginInterface pi)
    {
        if (!Enabled || this.disposed) return;
        try
        {
            // 必须在使用任何 KTK 功能前调用：注册服务、共享节点表、安装全局 close callback。
            this.initTask = KamiToolKitLibrary.InitializeAsync(pi);
            this.log("[原生HUD] KamiToolKit 初始化中…");
        }
        catch (Exception ex)
        {
            this.failed = true;
            this.log($"[原生HUD] KTK 初始化失败，回退 ImGui：{ex.Message}");
        }
    }

    /// <summary>必须每帧从游戏主线程调用；内部 200ms 节流，避免每帧重写 SeString。</summary>
    internal void Tick(bool wantOpen, HudSnapshot s)
    {
        if (!Enabled || this.failed || this.disposed) return;
        try
        {
            if (this.window is null)
            {
                if (!this.InitReady()) return;   // 初始化未完成期间仍由 ImGui 顶着

                this.window = new HudWindow(this.actions)
                {
                    // required 成员必须在对象初始化器里赋值（构造函数里会 CS9035）。
                    InternalName = "MagicBucketNativeHud",
                    Title = "触发中 · 魔法水桶",
                    Size = new Vector2(360f, 300f),
                };
                this.log("[原生HUD] 原生窗口已就绪");
            }

            if ((DateTime.UtcNow - this.lastSyncUtc).TotalMilliseconds < 200) return;
            this.lastSyncUtc = DateTime.UtcNow;

            // Open/Close/SetText 都要求游戏主线程；本方法由 IFramework.Update 驱动。
            if (wantOpen && !this.window.IsOpen) this.window.Open();
            else if (!wantOpen && this.window.IsOpen) this.window.Close();
            if (this.window.IsOpen) this.window.Apply(s);
        }
        catch (Exception ex)
        {
            this.failed = true;
            this.log($"[原生HUD] 运行异常，已停用并回退 ImGui：{ex}");
        }
    }

    /// <summary>「自动前往」窗口：与 HUD 共用一个 KTK 初始化，独立 200ms 节流与失败开关。</summary>
    internal void TickAuto(bool wantOpen, AutoWindowSnapshot s)
    {
        if (!Enabled || !EnabledAuto || this.failed || this.autoFailed || this.disposed) return;
        try
        {
            if (this.autoWindow is null)
            {
                if (!this.InitReady()) return;
                this.autoWindow = new AutoWindow(this.autoActions)
                {
                    InternalName = "MagicBucketNativeAuto",
                    Title = "自动前往 · 魔法水桶",
                    Size = new Vector2(AutoWindow.W, AutoWindow.DefaultH),
                };
                this.log("[原生UI] 自动前往原生窗口已就绪");
            }

            if ((DateTime.UtcNow - this.lastAutoSyncUtc).TotalMilliseconds < 200) return;
            this.lastAutoSyncUtc = DateTime.UtcNow;

            if (wantOpen && !this.autoWindow.IsOpen) this.autoWindow.Open();
            else if (!wantOpen && this.autoWindow.IsOpen) this.autoWindow.Close();
            if (this.autoWindow.IsOpen) this.autoWindow.Apply(s);
        }
        catch (Exception ex)
        {
            this.autoFailed = true;
            this.log($"[原生UI] 自动前往窗口异常，已停用并回退 ImGui：{ex}");
            try { this.autoWindow?.Close(); } catch { /* 忽略清理异常 */ }
        }
    }

    /// <summary>主窗口：与另外两个共用一次 KTK 初始化，独立 200ms 节流与失败开关。</summary>
    internal void TickMain(bool wantOpen, MainSnapshot s)
    {
        if (!Enabled || !EnabledMain || this.failed || this.mainFailed || this.disposed) return;
        try
        {
            if (this.mainWindow is null)
            {
                if (!this.InitReady()) return;
                this.mainWindow = new MainWindow(this.mainActions)
                {
                    InternalName = "MagicBucketNativeMain",
                    Title = "魔法水桶 · S 怪速查",
                    Size = new Vector2(MainWindow.W, MainWindow.DefaultH),
                };
                this.log("[原生UI] 主窗口原生窗口已就绪");
            }

            if ((DateTime.UtcNow - this.lastMainSyncUtc).TotalMilliseconds < 200) return;
            this.lastMainSyncUtc = DateTime.UtcNow;

            if (wantOpen && !this.mainWindow.IsOpen) this.mainWindow.Open();
            else if (!wantOpen && this.mainWindow.IsOpen)
            {
                this.mainWindow.SuppressClosed = true;
                try { this.mainWindow.Close(); }
                finally { this.mainWindow.SuppressClosed = false; }
            }
            if (this.mainWindow.IsOpen) this.mainWindow.Apply(s);
        }
        catch (Exception ex)
        {
            this.mainFailed = true;
            this.log($"[原生UI] 主窗口异常，已停用并回退 ImGui：{ex}");
            try { this.mainWindow?.Close(); } catch { /* 忽略清理异常 */ }
        }
    }

    /// <summary>v1.1.98：ImGui 高级设置保存后，跳过主窗 200ms 节流，下一帧立即 Apply。</summary>
    internal void ForceMainRefresh() => this.lastMainSyncUtc = DateTime.MinValue;

    internal void Shutdown()
    {
        if (this.disposed) return;
        this.disposed = true;
        try { this.window?.Close(); } catch { /* 卸载时别拖垮游戏 */ }
        try { this.autoWindow?.Close(); } catch { /* 同上 */ }
        try
        {
            if (this.mainWindow is { } mw)
            {
                mw.SuppressClosed = true;
                mw.Close();
            }
        }
        catch { /* 同上 */ }
        this.window = null;
        this.autoWindow = null;
        this.mainWindow = null;
        if (this.initTask is { IsCompletedSuccessfully: true })
        {
            try { KamiToolKitLibrary.Dispose(); }
            catch (Exception ex) { this.log($"[原生HUD] 卸载清理异常：{ex.Message}"); }
        }
    }
}

/// <summary>真正的原生窗口：标准游戏窗口框 + 5 行文本 + 4 个原生按钮。</summary>
internal sealed class HudWindow : NativeAddon
{
    private readonly HudActions actions;
    private TextNode? badge;
    private TextNode? progress;
    private TextNode? job;
    private TextNode? map;
    private TextNode? next;
    private TextNode? status;
    private TextNode? hint;
    private TextButtonNode? primary;
    private TextButtonNode? skip;
    private TextButtonNode? stop;
    private TextButtonNode? restart;

    internal HudWindow(HudActions actions) => this.actions = actions;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        SetWindowSize(new Vector2(360f, 300f));
        var p = ContentStartPosition;

        this.badge = Text(p + new Vector2(0f, 0f), 340f, 20f, 14, "● 空闲");
        this.progress = Text(p + new Vector2(150f, 0f), 190f, 20f, 12, "0/0");
        this.job = Text(p + new Vector2(0f, 26f), 340f, 26f, 18, "还没开始触发");
        this.map = Text(p + new Vector2(0f, 56f), 340f, 20f, 12, "");
        this.next = Text(p + new Vector2(0f, 78f), 340f, 20f, 12, "");
        this.status = Text(p + new Vector2(0f, 100f), 340f, 52f, 12, "");
        this.hint = Text(p + new Vector2(0f, 158f), 340f, 18f, 11,
            "关掉后：主窗「触发悬浮窗」按钮 或 /水桶 hud");

        this.primary = Button(p + new Vector2(0f, 182f), 166f, 28f, "继续队列",
            () => this.actions.Primary());
        this.skip = Button(p + new Vector2(174f, 182f), 166f, 28f, "停止这只，去下一个",
            () => this.actions.Skip());
        this.stop = Button(p + new Vector2(0f, 214f), 166f, 28f, "停止整队",
            () => this.actions.Stop());
        this.restart = Button(p + new Vector2(174f, 214f), 166f, 28f, "重新开始",
            () => this.actions.Restart());
    }

    private TextNode Text(Vector2 pos, float w, float h, uint fontSize, string value)
    {
        var n = new TextNode
        {
            Position = pos,
            Size = new Vector2(w, h),
            FontSize = fontSize,
            String = value,
        };
        n.AttachNode(this);
        return n;
    }

    private TextButtonNode Button(Vector2 pos, float w, float h, string label, Action onClick)
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
        b.AttachNode(this);
        return b;
    }

    internal void Apply(HudSnapshot s)
    {
        if (this.badge is not null) this.badge.String = s.Badge;
        if (this.progress is not null) this.progress.String = s.Progress;
        if (this.job is not null) this.job.String = s.Job;
        if (this.map is not null) this.map.String = s.Map;
        if (this.next is not null) this.next.String = s.Next;
        if (this.status is not null) this.status.String = s.Status;

        var pr = this.primary!;
        var sk = this.skip!;
        var st = this.stop!;
        var re = this.restart!;
        NativeButtonStyle.Apply(pr, s.Primary, 170f, 28f, false);
        NativeButtonStyle.Apply(sk, "停止这只，去下一个", 170f, 28f, false);
        NativeButtonStyle.Apply(st, "停止整队", 170f, 28f, false);
        NativeButtonStyle.Apply(re, "重新开始", 170f, 28f, false);

        // 两列按实际宽度定位：左列不再各占 166，短按钮收窄，长文字折行时行高跟着加高
        var p = this.ContentStartPosition;
        var col1 = MathF.Max(pr.Width, st.Width) + 8f;
        var row1H = MathF.Max(pr.Height, sk.Height) + 4f;
        pr.Position = p + new Vector2(0f, 182f);
        sk.Position = p + new Vector2(col1, 182f);
        st.Position = p + new Vector2(0f, 182f + row1H);
        re.Position = p + new Vector2(col1, 182f + row1H);
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        base.OnHide(addon);
        // 用户点原生窗口的 X / 游戏自行隐藏时，把开关同步回去。
        // 主动 Close() 也会走这里，但那时 autoHudOpen 本来就是 false，不会造成循环。
        this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        // 节点由 KTK 统一管理，这里只清托管引用，避免悬空指针。
        this.badge = this.progress = this.job = this.map = this.next = this.status = this.hint = null;
        this.primary = this.skip = this.stop = this.restart = null;
    }
}
