using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SrankFlag;

// ─────────────────────────────────────────────────────────────────────────────
// v1.2.12：S 怪历史记录（双源：主站 HTTP / Sonar 聊天栏）。
//
// 【设计约束】
//   ① 独立文件 srank_history.json，绝不触碰 5 个用户状态文件
//      （prefs.json / theme.txt / dc.txt / srank_live.json / srank_data.json）；
//      部署脚本只覆盖构建产物，本文件天然不受影响。
//   ② 内存列表恒按 EventTime 降序（最新在前），上限 500 条，超出从尾部裁掉。
//   ③ 全部读写都在框架主线程：Sonar 的 ChatMessage 只解析文本，写入经 RunOnTick 回主线程；
//      主站写入发生在 ApplyBearResult（framework tick）；UI 快照也在主线程。仍加一把锁兜底。
//   ④ 同一 (怪|服|事件|来源) 在 15 秒内的重复播报合并成一条，避免重播 / HTTP 重复拉取刷屏。
//      不同 Source 不去重 —— 同一次击杀同时被 Sonar 与主站记下，正是「概率核对」需要的对照。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>事件类型：出生（含主站「进闸」）/ 击杀。</summary>
public enum HistoryEventType
{
    Spawn,
    Kill,
}

/// <summary>数据来源：主站 HTTP 拉取 / Sonar 聊天栏播报。</summary>
public enum HistorySource
{
    MainSite,
    Sonar,
}

/// <summary>历史窗口筛选档位（互斥单选）。</summary>
public enum HistoryFilter
{
    All,
    KillOnly,
    SpawnOnly,
    SonarOnly,
    MainSiteOnly,
}

/// <summary>一条历史记录。字段名与需求一一对应，JSON 里枚举写成可读字符串。</summary>
public sealed class HistoryRecord
{
    /// <summary>怪名（中文名，来自 srank_data.json 的 cn）。</summary>
    public string MonsterName { get; set; } = "";

    /// <summary>服务器名。</summary>
    public string ServerName { get; set; } = "";

    /// <summary>出生 / 击杀。</summary>
    public HistoryEventType EventType { get; set; }

    /// <summary>主站 / Sonar。</summary>
    public HistorySource Source { get; set; }

    /// <summary>事件发生时刻（UTC Unix 秒）。Sonar 用捕获时刻，主站用站点返回的 lastDeath / 进闸时刻。</summary>
    public double EventTime { get; set; }

    /// <summary>地图名。</summary>
    public string MapName { get; set; } = "";
}

/// <summary>历史窗口纯数据快照（不含任何 KTK 类型，Plugin 侧不依赖 KTK DLL）。</summary>
internal sealed class HistorySnapshot
{
    internal readonly List<HistoryRecord> Rows = new();
    internal int AllTotal;
    internal int Total;
    internal int Page;
    internal int PageCount = 1;
    internal HistoryFilter Filter;
    internal string Status = "";
}

/// <summary>历史窗口回调（同样不含 KTK 类型）。</summary>
internal sealed class HistoryActions
{
    internal Action<HistoryFilter> SetFilter = _ => { };
    internal Action<int> PageBy = _ => { };
    internal Action Clear = () => { };
    internal Action Closed = () => { };
}

/// <summary>内存历史库 + 独立文件持久化。</summary>
internal sealed class SrankHistory
{
    internal const int MaxRecords = 500;
    internal const int PageSize = 24;
    private const string FileName = "srank_history.json";
    private const double DedupeSeconds = 15;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly List<HistoryRecord> records = new();
    /// <summary>主站「进闸」状态跟踪：key = 大区|怪.en|服，只在 false→true 时记一条 Spawn。</summary>
    private readonly Dictionary<string, bool> openStates = new(StringComparer.Ordinal);
    private readonly string path;
    private readonly Action<string> log;

    internal SrankHistory(string pluginDirectory, Action<string> log)
    {
        this.path = Path.Combine(pluginDirectory, FileName);
        this.log = log;
        this.Load();
    }

    // ───────────────────── 写入 ─────────────────────

    /// <summary>推一条历史。返回是否真的写入（被去重时返回 false）。</summary>
    internal bool Add(string monster, string server, string map,
                      HistoryEventType type, HistorySource source, double eventTime)
    {
        if (monster.Length == 0 || server.Length == 0 || eventTime <= 0) return false;
        try
        {
            lock (this.gate) return this.AddLocked(monster, server, map, type, source, eventTime);
        }
        catch (Exception ex)
        {
            try { this.log("[SrankFlag.History] 写入失败：" + ex.Message); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 主站「新 isOpen」检测：HTTP 数据更新后逐 (怪, 服) 调一次。
    /// 首次见到 key 只记基线、不写历史（否则插件一启动就把当前所有开闸怪刷成几百条）；
    /// 之后只有 false→true 才推一条 Spawn/MainSite。状态表不随 Clear() 清空，
    /// 避免清空历史后下一次刷新把同一个开闸当成新事件重复记录。
    /// </summary>
    internal void NoteOpenState(string key, bool openNow, string monster, string server,
                                string map, double eventTime)
    {
        if (key.Length == 0) return;
        try
        {
            lock (this.gate)
            {
                if (!this.openStates.TryGetValue(key, out var prev))
                {
                    this.openStates[key] = openNow;
                    return;
                }
                if (prev == openNow) return;
                this.openStates[key] = openNow;
                if (!openNow) return;
                // 注意：这里已经在 gate 内，直接走内部写入，避免嵌套 lock 虽可重入但多一层判断。
                this.AddLocked(monster, server, map, HistoryEventType.Spawn,
                               HistorySource.MainSite, eventTime);
            }
        }
        catch (Exception ex)
        {
            try { this.log("[SrankFlag.History] 进闸状态跟踪失败：" + ex.Message); } catch { }
        }
    }

    /// <summary>gate 已持有时的写入实现（不重复加锁）。</summary>
    private bool AddLocked(string monster, string server, string map,
                           HistoryEventType type, HistorySource source, double eventTime)
    {
        if (monster.Length == 0 || server.Length == 0 || eventTime <= 0) return false;
        foreach (var r in this.records)
        {
            if (Math.Abs(r.EventTime - eventTime) > DedupeSeconds) continue;
            if (r.EventType == type && r.Source == source &&
                string.Equals(r.MonsterName, monster, StringComparison.Ordinal) &&
                string.Equals(r.ServerName, server, StringComparison.Ordinal))
                return false;
        }
        this.records.Add(new HistoryRecord
        {
            MonsterName = monster, ServerName = server, MapName = map ?? "",
            EventType = type, Source = source, EventTime = eventTime,
        });
        this.records.Sort((a, b) => b.EventTime.CompareTo(a.EventTime));
        if (this.records.Count > MaxRecords)
            this.records.RemoveRange(MaxRecords, this.records.Count - MaxRecords);
        this.Save();
        return true;
    }

    // ───────────────────── 读取 / 交互 ─────────────────────

    /// <summary>按筛选 + 分页生成 UI 快照。只把当前页的 24 条交给窗口，节点池固定大小。</summary>
    internal HistorySnapshot Snapshot(HistoryFilter filter, int page)
    {
        var s = new HistorySnapshot { Filter = filter };
        lock (this.gate)
        {
            s.AllTotal = this.records.Count;
            IEnumerable<HistoryRecord> q = this.records;
            switch (filter)
            {
                case HistoryFilter.KillOnly: q = q.Where(r => r.EventType == HistoryEventType.Kill); break;
                case HistoryFilter.SpawnOnly: q = q.Where(r => r.EventType == HistoryEventType.Spawn); break;
                case HistoryFilter.SonarOnly: q = q.Where(r => r.Source == HistorySource.Sonar); break;
                case HistoryFilter.MainSiteOnly: q = q.Where(r => r.Source == HistorySource.MainSite); break;
            }
            var list = q.ToList();
            s.Total = list.Count;
            s.PageCount = Math.Max(1, (s.Total + PageSize - 1) / PageSize);
            s.Page = Math.Clamp(page, 0, s.PageCount - 1);
            foreach (var r in list.Skip(s.Page * PageSize).Take(PageSize)) s.Rows.Add(r);
            s.Status = s.Total == 0
                ? $"共 {s.AllTotal} 条 · 当前筛选无记录 · 上限 {MaxRecords} 条"
                : $"共 {s.AllTotal} 条 · 筛选后 {s.Total} 条 · 第 {s.Page + 1}/{s.PageCount} 页 · 上限 {MaxRecords} 条";
        }
        return s;
    }

    /// <summary>清空历史（保留主站进闸状态表，避免重复播报）。</summary>
    internal void Clear()
    {
        lock (this.gate)
        {
            this.records.Clear();
            this.Save();
        }
    }

    // ───────────────────── 持久化（独立文件） ─────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(this.path)) return;
            var list = JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(this.path), JsonOpts);
            if (list is null) return;
            foreach (var r in list)
            {
                if (r is null) continue;
                r.MonsterName ??= "";
                r.ServerName ??= "";
                r.MapName ??= "";
                if (r.EventTime <= 0) continue;
                this.records.Add(r);
            }
            this.records.Sort((a, b) => b.EventTime.CompareTo(a.EventTime));
            if (this.records.Count > MaxRecords)
                this.records.RemoveRange(MaxRecords, this.records.Count - MaxRecords);
        }
        catch (Exception ex)
        {
            try { this.log("[SrankFlag.History] 读历史失败（忽略，从空列表开始）：" + ex.Message); } catch { }
            this.records.Clear();
        }
    }

    private void Save()
    {
        try { File.WriteAllText(this.path, JsonSerializer.Serialize(this.records, JsonOpts)); }
        catch (Exception ex)
        {
            try { this.log("[SrankFlag.History] 写历史失败：" + ex.Message); } catch { }
        }
    }
}
