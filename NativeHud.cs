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
// KTK 相关类型集中在 NativeHud.cs + NativeAutoWindow.cs。
// v1.2.5：自动前往 / 触发中两个窗口的 ImGui 兜底版已彻底删除（DrawAutoWindow / DrawAutoHud 不再存在），
// 只保留原生 KTK 窗口；若 KTK 初始化或运行失败，该窗口直接不显示，绝不再回退（无 ImGui 兜底） 避免串台。
// 主窗口（MagicBucket）不再保留 ImGui 兜底：KTK 主窗不可用时直接不显示（无 ImGui 回退），由 ActiveMain 控制；
// 若 KTK 初始化 / 运行失败，主窗将不出现，请检查插件目录的 KamiToolKit.dll / FFXIVClientStructs.Expansion.dll。
// KamiToolKitLibrary 是全局静态，只能初始化 / 释放一次，所以窗口共用本 Runtime。
// 运行期依赖：插件目录需有 KamiToolKit.dll + FFXIVClientStructs.Expansion.dll。
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
/// KTK 运行时桥（触发中 / 自动前往 / 主窗 / 设置面板 / 历史记录五个原生窗口共用一次 KTK 初始化）。生命周期：
///   BeginInit（插件构造，异步启动 InitializeAsync）
///   → Tick / TickAuto / TickMain / TickSettings / TickHistory（每帧，框架主线程）→ Shutdown。
/// v1.2.12：第 5 窗历史记录同样纳入 SetXxxWindowOpen / RestoreAfterTeleport / ReviveAfterError，
/// 遵守 v1.2.11「原来开着几个窗就保持几个、插件不得替用户点亮任何窗口」。
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
    private readonly SettingsActions settingsActions;
    private readonly HistoryActions historyActions;
    private readonly Action<string> log;
    private Task? initTask;
    private HudWindow? window;
    private AutoWindow? autoWindow;
    private MainWindow? mainWindow;
    private SettingsWindow? settingsWindow;
    private NativeHistoryWindow? historyWindow;
    private bool failed;
    private bool autoFailed;
    private bool mainFailed;
    private bool settingsFailed;
    private bool historyFailed;
    private bool disposed;
    private DateTime lastSyncUtc = DateTime.MinValue;
    private DateTime lastAutoSyncUtc = DateTime.MinValue;
    private DateTime lastMainSyncUtc = DateTime.MinValue;
    private DateTime lastSettingsSyncUtc = DateTime.MinValue;
    private DateTime lastHistorySyncUtc = DateTime.MinValue;
    private DateTime lastTickErrorLogUtc = DateTime.MinValue;   // v1.2.10：Tick* 异常日志限频

    internal NativeHudRuntime(HudActions actions, AutoWindowActions autoActions,
                              MainActions mainActions, SettingsActions settingsActions,
                              HistoryActions historyActions, Action<string> log)
    {
        this.actions = actions;
        this.autoActions = autoActions;
        this.mainActions = mainActions;
        this.settingsActions = settingsActions;
        this.historyActions = historyActions;
        this.log = log;
        MainWindow.Logger = log;   // 让主窗内部的异常/降级日志也进 /xllog
        SettingsWindow.Logger = log;   // v1.2.7 设置面板同理
        HudWindow.Logger = log;        // v1.2.10 触发悬浮窗节点初始化失败也要能定位
        NativeHistoryWindow.Logger = log;   // v1.2.12 历史窗同理
    }

    /// <summary>「触发中」原生 HUD 窗口已成功初始化并接管（旧版 ImGui 触发悬浮窗已彻底废弃，不再绘制）。</summary>
    internal bool Active => Enabled && !this.failed && !this.disposed && this.window is not null;

    /// <summary>「自动前往」原生窗口已成功初始化并接管（旧版 ImGui 自动前往窗已彻底废弃，不再绘制）。</summary>
    internal bool ActiveAuto => Enabled && EnabledAuto && !this.failed && !this.autoFailed &&
                                !this.disposed && this.autoWindow is not null;

    /// <summary>「主窗口」原生窗口已接管（此时 DrawMainWindow 跳过 ImGui 主窗）。</summary>
    internal bool ActiveMain => Enabled && EnabledMain && !this.failed && !this.mainFailed &&
                                !this.disposed && this.mainWindow is not null;

    /// <summary>KTK 初始化是否已经成功完成；失败时整体回退（无 ImGui 兜底）。</summary>
    private bool InitReady()
    {
        var t = this.initTask;
        if (t is null || !t.IsCompleted) return false;
        if (t.IsFaulted)
        {
            this.failed = true;
            this.log($"[原生UI] 初始化异常，全部回退（无 ImGui 兜底）：{t.Exception}");
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
            this.log($"[原生HUD] KTK 初始化失败，回退（无 ImGui 兜底）：{ex.Message}");
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
            // v1.2.10 急救：这里的 failed 是【全局】标志，四个 Tick* 和 /水桶 的 ForceOne 都检查它。
            // 触发悬浮窗一次 NRE 会把主窗 / 自动前往窗 / 设置面板 / 斜杠指令一次性全部停掉。
            // 改为只丢当前帧：日志 5 秒限频，下一帧按 200ms 节流自然重试，绝不永久停用。
            if ((DateTime.UtcNow - this.lastTickErrorLogUtc).TotalSeconds >= 5)
            {
                this.lastTickErrorLogUtc = DateTime.UtcNow;
                this.log($"[原生HUD] 本帧刷新异常，已跳过并重试（不影响其它窗口）：{ex}");
            }
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
            // v1.2.10：单窗异常不再永久停用，也不主动 Close（那会直接把窗口干掉）。
            if ((DateTime.UtcNow - this.lastTickErrorLogUtc).TotalSeconds >= 5)
            {
                this.lastTickErrorLogUtc = DateTime.UtcNow;
                this.log($"[原生UI] 自动前往窗本帧刷新异常，已跳过并重试：{ex}");
            }
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
            // v1.2.10：单窗异常不再永久停用，也不主动 Close（那会直接把窗口干掉）。
            if ((DateTime.UtcNow - this.lastTickErrorLogUtc).TotalSeconds >= 5)
            {
                this.lastTickErrorLogUtc = DateTime.UtcNow;
                this.log($"[原生UI] 主窗口本帧刷新异常，已跳过并重试：{ex}");
            }
        }
    }

    /// <summary>v1.1.98：ImGui 高级设置保存后，跳过主窗 200ms 节流，下一帧立即 Apply。</summary>
    internal void ForceMainRefresh() => this.lastMainSyncUtc = DateTime.MinValue;

    /// <summary>v1.2.10 急救：清除「单窗异常」软停用标志，让 /水桶 立刻恢复驱动能力。
    /// 只清软失败（autoFailed / mainFailed / settingsFailed），KTK 初始化失败的全局 failed 不动。</summary>
    internal void ReviveAfterError()
    {
        this.autoFailed = false;
        this.mainFailed = false;
        this.settingsFailed = false;
        this.historyFailed = false;
        this.lastSyncUtc = this.lastAutoSyncUtc = this.lastMainSyncUtc =
            this.lastSettingsSyncUtc = this.lastHistorySyncUtc = DateTime.MinValue;
    }

    // ───────── v1.2.9：窗口状态直驱（指令 / 落地恢复专用，绝无强锁） ─────────
    // 游戏过图会直接隐藏自定义 addon，但 KTK 的 IsOpen 缓存未必同步；
    // 此时 Tick* 里「wantOpen && !IsOpen」不会重新 Open，窗口就再也回不来，
    // 这正是 v1.2.6 封死 ImGui 之后「/水桶 敲了没反应」的根因。
    // 所以指令与落地恢复不能只切 bool，必须 Close→Open 强制重建显示状态。
    internal void SetMainWindowOpen(bool open) =>
        this.ForceOne(this.mainWindow, open, "主窗", () => { this.lastMainSyncUtc = DateTime.MinValue; });
    internal void SetHudWindowOpen(bool open) =>
        this.ForceOne(this.window, open, "触发悬浮窗", () => { this.lastSyncUtc = DateTime.MinValue; });
    internal void SetAutoWindowOpen(bool open) =>
        this.ForceOne(this.autoWindow, open, "自动前往窗", () => { this.lastAutoSyncUtc = DateTime.MinValue; });
    internal void SetSettingsWindowOpen(bool open) =>
        this.ForceOne(this.settingsWindow, open, "设置面板", () => { this.lastSettingsSyncUtc = DateTime.MinValue; });
    internal void SetHistoryWindowOpen(bool open) =>
        this.ForceOne(this.historyWindow, open, "历史窗口", () => { this.lastHistorySyncUtc = DateTime.MinValue; });

    /// <summary>落地恢复：只把快照里原本开着的窗口拉回来，原本关着的不动。
    /// v1.2.12：五个窗口（含历史）一律按用户原本的状态保持，插件不替用户点亮任何窗口。</summary>
    internal void RestoreAfterTeleport(bool main, bool hud, bool auto, bool settings, bool history)
    {
        if (this.disposed) return;
        if (main) this.SetMainWindowOpen(true);
        if (hud) this.SetHudWindowOpen(true);
        if (auto) this.SetAutoWindowOpen(true);
        if (settings) this.SetSettingsWindowOpen(true);
        if (history) this.SetHistoryWindowOpen(true);
    }

    private void ForceOne(NativeAddon? w, bool open, string name, Action resetThrottle)
    {
        // 窗口还没懒建出来 → 只切 bool 即可，交给 Tick* 按开关自然创建并 Open。
        // v1.2.10：指令优先。failed 只代表 KTK 初始化失败（那时 w 本来就是 null），
        // 不应挡住 /水桶 对已创建窗口的驱动。
        if (w is null || this.disposed) return;
        try
        {
            if (open)
            {
                // IsOpen 缓存说「开着」时先 Close 再 Open，强制清掉可能与游戏实际可见性
                // 脱节的缓存；已经关着的直接 Open，避免对未打开窗口调 Close。
                // SuppressClosed 防止 Close 触发 OnHide → Closed() 把开关写回 false。
                if (w.IsOpen) SuppressClose(w);
                w.Open();
            }
            else if (w.IsOpen) SuppressClose(w);
            resetThrottle();   // 下一帧立即 Apply，不等 200ms 节流
        }
        catch (Exception ex)
        {
            this.log($"[原生UI] {name} 状态同步失败，尝试释放重开：{ex.Message}");
            try { SuppressClose(w); w.Open(); }
            catch (Exception ex2) { this.log($"[原生UI] {name} 释放重开仍失败：{ex2.Message}"); }
        }
    }

    private static void SuppressClose(NativeAddon w)
    {
        // 四个窗口各有自己的 SuppressClosed；统一管理，避免 Close 回调污染开关。
        switch (w)
        {
            case MainWindow mw:
                mw.SuppressClosed = true;
                try { mw.Close(); } finally { mw.SuppressClosed = false; }
                break;
            case HudWindow hw:
                hw.SuppressClosed = true;
                try { hw.Close(); } finally { hw.SuppressClosed = false; }
                break;
            case AutoWindow aw:
                aw.SuppressClosed = true;
                try { aw.Close(); } finally { aw.SuppressClosed = false; }
                break;
            case SettingsWindow sw:
                sw.SuppressClosed = true;
                try { sw.Close(); } finally { sw.SuppressClosed = false; }
                break;
            case NativeHistoryWindow nw:
                nw.SuppressClosed = true;
                try { nw.Close(); } finally { nw.SuppressClosed = false; }
                break;
            default:
                w.Close();
                break;
        }
    }

    /// <summary>v1.2.7：原生设置面板。与主窗一样共用一次 KTK 初始化，独立 200ms 节流；
    /// 点齿轮 / 切换开关后 ForceSettingsRefresh() 让下一帧立即重画。</summary>
    internal void TickSettings(bool wantOpen, SettingsSnapshot s)
    {
        if (!Enabled || this.failed || this.settingsFailed || this.disposed) return;
        try
        {
            if (this.settingsWindow is null)
            {
                if (!this.InitReady()) return;
                this.settingsWindow = new SettingsWindow(this.settingsActions)
                {
                    InternalName = "MagicBucketNativeSettings",
                    Title = "魔法水桶 · 设置",
                    Size = new Vector2(SettingsWindow.W, SettingsWindow.H),
                };
                this.log("[原生UI] 设置面板原生窗口已就绪");
            }

            if (!wantOpen)
            {
                if (this.settingsWindow.IsOpen)
                {
                    this.settingsWindow.SuppressClosed = true;
                    try { this.settingsWindow.Close(); }
                    finally { this.settingsWindow.SuppressClosed = false; }
                }
                return;
            }

            if (!this.settingsWindow.IsOpen)
            {
                this.settingsWindow.Open();
                this.lastSettingsSyncUtc = DateTime.MinValue;   // 刚打开必须立刻刷一次，别等节流
            }
            if ((DateTime.UtcNow - this.lastSettingsSyncUtc).TotalMilliseconds < 200) return;
            this.lastSettingsSyncUtc = DateTime.UtcNow;
            if (this.settingsWindow.IsOpen) this.settingsWindow.Apply(s);
        }
        catch (Exception ex)
        {
            // v1.2.10：单窗异常不再永久停用，也不主动 Close。
            if ((DateTime.UtcNow - this.lastTickErrorLogUtc).TotalSeconds >= 5)
            {
                this.lastTickErrorLogUtc = DateTime.UtcNow;
                this.log($"[原生UI] 设置面板本帧刷新异常，已跳过并重试：{ex}");
            }
        }
    }

    /// <summary>v1.2.7：跳过设置面板 200ms 节流，下一帧立即重画。</summary>
    internal void ForceSettingsRefresh() => this.lastSettingsSyncUtc = DateTime.MinValue;

    /// <summary>
    /// v1.2.12：S 怪历史窗口（第 5 窗）。与其余四窗共用一次 KTK 初始化，独立 200ms 节流。
    /// 单窗异常只记限频日志、下一帧重试，绝不设共享 failed、绝不主动 Close（v1.2.10 铁律）。
    /// </summary>
    internal void TickHistory(bool wantOpen, HistorySnapshot s)
    {
        if (!Enabled || this.failed || this.historyFailed || this.disposed) return;
        try
        {
            if (this.historyWindow is null)
            {
                if (!this.InitReady()) return;
                this.historyWindow = new NativeHistoryWindow(this.historyActions)
                {
                    InternalName = "MagicBucketNativeHistory",
                    Title = "魔法水桶 · S 怪历史",
                    Size = new Vector2(NativeHistoryWindow.W, NativeHistoryWindow.H),
                };
                this.log("[原生UI] 历史窗口已就绪");
            }

            if (!wantOpen)
            {
                if (this.historyWindow.IsOpen)
                {
                    this.historyWindow.SuppressClosed = true;
                    try { this.historyWindow.Close(); }
                    finally { this.historyWindow.SuppressClosed = false; }
                }
                return;
            }

            if (!this.historyWindow.IsOpen)
            {
                this.historyWindow.Open();
                this.lastHistorySyncUtc = DateTime.MinValue;   // 刚打开立刻刷一次，别等节流
            }
            if ((DateTime.UtcNow - this.lastHistorySyncUtc).TotalMilliseconds < 200) return;
            this.lastHistorySyncUtc = DateTime.UtcNow;
            if (this.historyWindow.IsOpen) this.historyWindow.Apply(s);
        }
        catch (Exception ex)
        {
            if ((DateTime.UtcNow - this.lastTickErrorLogUtc).TotalSeconds >= 5)
            {
                this.lastTickErrorLogUtc = DateTime.UtcNow;
                this.log($"[原生UI] 历史窗口本帧刷新异常，已跳过并重试：{ex}");
            }
        }
    }

    /// <summary>新记录写入 / 筛选 / 翻页 / 清空后，跳过 200ms 节流下一帧立即重画。</summary>
    internal void ForceHistoryRefresh() => this.lastHistorySyncUtc = DateTime.MinValue;

    internal void Shutdown()
    {
        if (this.disposed) return;
        this.disposed = true;
        try { this.window?.Close(); } catch { /* 卸载时别拖垮游戏 */ }
        try { this.autoWindow?.Close(); } catch { /* 同上 */ }
        try
        {
            if (this.settingsWindow is { } sw)
            {
                sw.SuppressClosed = true;
                sw.Close();
            }
        }
        catch { /* 同上 */ }
        try
        {
            if (this.mainWindow is { } mw)
            {
                mw.SuppressClosed = true;
                mw.Close();
            }
        }
        catch { /* 同上 */ }
        try
        {
            if (this.historyWindow is { } hw)
            {
                hw.SuppressClosed = true;
                hw.Close();
            }
        }
        catch { /* 同上 */ }
        this.window = null;
        this.autoWindow = null;
        this.mainWindow = null;
        this.settingsWindow = null;
        this.historyWindow = null;
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
    // v1.2.10 急救：KTK 的 Open() 与 OnSetup 不保证同一帧完成，OnFinalize 也会清空节点引用。
    // 缺这个守卫时，首次点「开始自动前往」会在按钮尚未创建时立刻 Apply → NRE →
    // Tick 的 catch 把【全局】failed 置 true，四个原生窗口与 /水桶 一次性全部停摆。
    private bool setupDone;
    internal static Action<string>? Logger;
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

    internal bool SuppressClosed;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        this.setupDone = false;
        try
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
            this.setupDone = true;
        }
        catch (Exception ex)
        {
            // v1.2.10：KTK 带纹理组件在国服环境可能构造失败；吞掉并保持 setupDone=false，
            // Apply 逐帧跳过，绝不让异常冒泡到 Tick 把整个原生 UI 停用。
            this.setupDone = false;
            try { Logger?.Invoke("[原生UI] 触发悬浮窗节点初始化失败，本窗暂不刷新：" + ex.Message); }
            catch { /* 日志本身失败也不影响 */ }
        }
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
        // v1.2.10 急救：节点尚未 OnSetup / 已被 OnFinalize 释放时跳过本帧，下一帧重试。
        // 绝不让 null 按钮流到 NativeButtonStyle.Apply 触发 NRE。
        if (!this.setupDone || this.primary is null || this.skip is null ||
            this.stop is null || this.restart is null) return;
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
        // v1.2.9：过图 hide 属于正常生命周期，该关就关，开关如实回写；
        // 落地后由 Plugin 的窗口快照 + 延迟恢复重新拉回来。
        // 程序内主动 Close()（指令关闭 / 强制重开）用 SuppressClosed 屏蔽回写。
        if (!this.SuppressClosed) this.actions.Closed();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        // 节点由 KTK 统一管理，这里只清托管引用，避免悬空指针。
        this.badge = this.progress = this.job = this.map = this.next = this.status = this.hint = null;
        this.primary = this.skip = this.stop = this.restart = null;
        this.setupDone = false;   // v1.2.10：节点已释放，下一帧 Apply 必须等重新 OnSetup
    }
}
