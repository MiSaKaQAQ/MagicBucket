using System;
using System.Collections.Generic;

namespace SrankFlag;

/// <summary>
/// S 怪「特殊触发条件」通用条件系统（右键详情单行展示用）。
///
/// 设计要点：
///   1. 单一入口 GetCondition(id) → HuntCondition?（null 表示该怪无特殊条件）。
///   2. 实时判定（Met）直接复用 ConditionForecast 已校验过的天气 / ET / 月相引擎
///      （与熊站 Condition Timer 同判据，不重复造天气算法）。
///      只有「纯前置动作类」(Prereq) 才把 En 留空，仅显示文字、不实时判定。
///   3. 加新怪只需在 Conditions 里加一行（或改 switch）；CN 别名在 Aliases 里补。
///
/// 颜色语义（供 UI）：Met == true 亮绿；Met == false 暗红；Met == null 中性灰金（仅提示）。
/// </summary>
internal static class HuntConditionHelper
{
    public enum Kind
    {
        None,        // 无特殊条件
        Weather,     // 天气即时型：当前区天气命中（如碧空/晴朗）
        WeatherHold, // 天气连续型：连续 N 分钟 有/无 某天气（如连续小雨 / 连续无雨）
        Time,        // ET 时段型：每天固定 ET 小时窗口
        Moon,        // 月相 + ET 时段（满月 / 新月等，可能叠加天气）
        Prereq,      // 纯前置动作类：无法实时判定，只给文字
    }

    public sealed record HuntCondition(string Text, bool? Met, bool Live, Kind Type);

    private sealed record Entry(string En, string Text, Kind Type);

    // ── 条件表：以 EN 键为准（必须和 ConditionForecast.Specs 的键一致，否则实时判定失效）──
    private static readonly Dictionary<string, Entry> Conditions = new(StringComparer.Ordinal)
    {
        ["Okina"]                   = new("Okina",                   "月相：满月",       Kind.Moon),
        ["Gamma"]                   = new("Gamma",                   "时间：ET17-次日08", Kind.Time),
        ["The Garlok"]              = new("The Garlok",              "天气：无雨≥200分", Kind.WeatherHold),
        ["Croque-Mitaine"]          = new("Croque-Mitaine",          "时间：ET19-22",    Kind.Time),
        ["Thousand-cast Theda"]     = new("Thousand-cast Theda",     "时间：ET17-21",    Kind.Time),
        ["Zona Seeker"]             = new("Zona Seeker",             "天气：碧空/晴朗",  Kind.Weather),
        ["Bonnacon"]                = new("Bonnacon",                "时间：ET8-11",     Kind.Time),
        ["Croakadile"]              = new("Croakadile",              "月相：满月",       Kind.Moon),
        ["Laideronnette"]           = new("Laideronnette",           "天气：连续小雨",   Kind.WeatherHold),
        ["Kirlirger the Abhorrent"] = new("Kirlirger the Abhorrent", "天气：薄雾(新月)", Kind.Moon),
        ["Burfurlur the Canny"]     = new("Burfurlur the Canny",     "天气：碧空/晴朗",  Kind.Weather),
        // 想加新怪：照上面加一行。纯前置提示（如“需先击杀 XX”）把 En 留 "" 且 Type=Prereq。
    };

    // CN 名 → EN 键（数据里的 Cn 才是展示名；用户口述的“奥阔帕恰山S”对应 厌忌之人 奇里格）
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["巨大鳐"]        = "Okina",
        ["伽马"]          = "Gamma",
        ["伽洛克"]        = "The Garlok",
        ["护土精灵"]      = "Croque-Mitaine",
        ["千竿口花希达"]  = "Thousand-cast Theda",
        ["虚无探索者"]    = "Zona Seeker",
        ["火愤牛"]        = "Bonnacon",
        ["咕尔呱洛斯"]    = "Croakadile",
        ["雷德罗巨蛇"]    = "Laideronnette",
        ["布弗鲁"]        = "Burfurlur the Canny",
        ["奥阔帕恰山S"]   = "Kirlirger the Abhorrent",
        ["厌忌之人 奇里格"] = "Kirlirger the Abhorrent",
    };

    // 轻量缓存：ConditionForecast.NextWindow 本身不带缓存，避免每帧重算 ET/天气窗口
    private static readonly Dictionary<string, (long Ts, HuntCondition Val)> Cache = new(StringComparer.Ordinal);
    private const long CacheMs = 4000;

    /// <summary>
    /// 取某怪的特殊条件。id 可以是 EN 键或 CN 名（含用户口述别名）。
    /// 无特殊条件返回 null。返回的 Text 形如 “[天气：碧空/晴朗 ✔]”，Met 为实时判定（null=不判定）。
    /// </summary>
    public static HuntCondition? GetCondition(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var en = Conditions.ContainsKey(id!)
            ? id!
            : Aliases.TryGetValue(id!, out var a) ? a : "";
        if (en.Length == 0 || !Conditions.TryGetValue(en, out var e)) return null;

        bool? met = null;
        if (e.Type != Kind.Prereq && ConditionForecast.HasSpec(en))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var w = ConditionForecast.NextWindow(en, now);
            met = w.HasValue && w.Value.Start <= now && now < w.Value.End;
        }

        var mark = met switch { true => " ✔", false => " ✘", _ => "" };
        var text = "[" + e.Text + mark + "]";
        return new HuntCondition(text, met, met.HasValue, e.Type);
    }

    /// <summary>便捷版：只拿展示文案（含判定标记）；无特殊条件返回 null。</summary>
    public static string? GetConditionText(string? id) => GetCondition(id)?.Text;
}
