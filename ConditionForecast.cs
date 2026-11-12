using System;
using System.Collections.Generic;
using System.Linq;

namespace SrankFlag;

/// <summary>
/// S 怪的「条件时间」预测：天气连续 / 天气即时 / ET 时段 / 月相。
///
/// 算法和规则移植自：
///   · asvel/ffxiv-weather（SaintCoinach 的天气算法，MIT）
///   · 本机 srank_trigger.py 的 TRIGGER_SPEC（与 eorzea-weather.com 逐秒校验过）
/// 熊站（tracker.beartoolkit.com）的「Condition Timer」用的是同一套判据，
/// 所以这里算出来的窗口和熊站那一列一致；纯本地计算，不联网、不依赖站点存活。
///
/// 关键常数：1 ET 小时 = 175 现实秒，1 ET 天 = 4200 秒，天气每 8 ET 小时（1400 秒）切一次。
/// 天气种子：bell = t/175，increment = (bell+8-bell%8)%24，base = days*100+increment，
///           (base<<11 ^ base) 再 (>>8 ^) 取 %100 —— 左移 11 位是重点，写成右移就全错。
/// </summary>
internal static class ConditionForecast
{
    public const double EtHour = 175.0;          // 1 艾欧泽亚小时 = 175 现实秒
    public const double EtDay = 4200.0;          // 1 艾欧泽亚天 = 24*175
    public const double WeatherPeriod = 1400.0;  // 一个天气周期 = 8 ET 小时

    private const double HorizonDefault = 57600;   // 日常时段型：16 现实小时
    private const double HorizonMoon = 1300000;    // 月相型：约 15 天（要翻完整个 32 天月相）
    private const double HorizonWeather = 700000;  // 天气型：约 8 天（小雨/薄雾这种低概率天气要等）
    private const int DaysAhead = 320;             // ET 日枚举上限：必须 > 32，否则漏整个月相周期

    // 月相基准偏移（单位：艾欧泽亚日）。真实游戏的新月(dom 1) 未必落在 Unix 纪元；
    // 设为使真实新月对齐的 ET 日数（正数 = 把新月提前）。实测 11-13 应为新月日 2-4
    // （夺心魔 ET17:00~次日03:00 = 10 ET 小时 ≈ 29 真实分钟），但旧基准把 11-13 算成新月日1
    // （窗口 27 ET 小时 ≈ 79 分钟，错误）。偏移 +2 使 11-13 落到新月日3，套用日2-4 规则 →
    // 窗口 03:29~03:58（与玩家实测吻合）。与 bot trigger_forecast.py 的 MOON_EPOCH_OFFSET=2 保持一致。
    private const int MoonEpochOffset = 2;

    public readonly record struct Window(double Start, double End, string Name);

    private sealed class Spec
    {
        public string Kind = "";                                  // weather_hold / weather / et_window
        public string Zone = "";                                  // 英文区名（天气表键）
        public string ZoneCn = "";                                // 中文区名，只用于文案
        public double Hold;                                       // 连续天气要维持的秒数
        public string[] Include = Array.Empty<string>();
        public string[] Exclude = Array.Empty<string>();
        public (double From, double To)[] Hours = Array.Empty<(double, double)>();  // ET 小时，To 可 >24
        public (double From, double To)[] FirstDayHours = Array.Empty<(double, double)>();  // 新月日1 专用 ET 时段（其余日用 Hours）
        public Func<int, bool> DayFilter = _ => true;
        public string[] Weather = Array.Empty<string>();          // ET 窗口内还要同时命中的天气
        public string Label = "";                                 // 悬浮提示里用的原始条件描述
        public double Horizon = HorizonDefault;
    }

    // 用到的 5 张图（累计概率表：target < Bound 即命中该天气）
    private static readonly Dictionary<string, (int Bound, string Name)[]> Zones = new(StringComparer.Ordinal)
    {
        ["Central Shroud"] = new[]
        {
            (5, "Thunder"), (20, "Rain"), (30, "Fog"), (40, "Clouds"),
            (55, "Fair Skies"), (85, "Clear Skies"), (100, "Fair Skies"),
        },
        ["Eastern La Noscea"] = new[]
        {
            (5, "Fog"), (50, "Clear Skies"), (80, "Fair Skies"),
            (90, "Clouds"), (95, "Rain"), (100, "Showers"),
        },
        ["Western Thanalan"] = new[]
        {
            (40, "Clear Skies"), (60, "Fair Skies"), (85, "Clouds"), (95, "Fog"), (100, "Rain"),
        },
        ["Labyrinthos"] = new[]
        {
            (15, "Clear Skies"), (60, "Fair Skies"), (85, "Clouds"), (100, "Rain"),
        },
        ["Urqopacha"] = new[]
        {
            (20, "Clear Skies"), (50, "Fair Skies"), (70, "Clouds"),
            (80, "Fog"), (90, "Wind"), (100, "Snow"),
        },
    };

    private static readonly Dictionary<string, string> WeatherCn = new(StringComparer.Ordinal)
    {
        ["Clear Skies"] = "碧空",
        ["Fair Skies"] = "晴朗",
        ["Clouds"] = "阴云",
        ["Fog"] = "薄雾",
        ["Rain"] = "小雨",
        ["Showers"] = "暴雨",
        ["Thunder"] = "打雷",
        ["Wind"] = "微风",
        ["Snow"] = "小雪",
    };

    // ET 月名（一年 12 个月、每月 32 天）
    private static readonly string[] MonthNames =
    {
        "星1月", "灵1月", "星2月", "灵2月", "星3月", "灵3月",
        "星4月", "灵4月", "星5月", "灵5月", "星6月", "灵6月",
    };

    // 带月相基准偏移的 ET 月内日（1..32）。offset 处理负数 dayCount（d0-1 回看一天）。
    private static int MoonDay(int dayCount)
        => (((dayCount + MoonEpochOffset) % 32) + 32) % 32 + 1;

    private static bool NewMoon(int dayCount) => MoonDay(dayCount) is >= 1 and <= 4;
    private static bool FullMoon16to19(int dayCount) => MoonDay(dayCount) is >= 16 and <= 19;
    private static bool FullMoon16(int dayCount) => MoonDay(dayCount) == 16;

    /* 13 只有条件窗口的 S（键是 srank_data.json 的 en，和熊站 case 一一对应）。
       改规则和 srank_trigger.py 的 TRIGGER_SPEC 保持同步，两边算出来的窗口必须一样。 */
    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        // ── 天气连续型 ──
        ["Laideronnette"] = new()   // 雷德罗巨蛇：连续 30 分钟小雨（中央林区没有暴雨，等价于「下雨」）
        {
            Kind = "weather_hold", Zone = "Central Shroud", ZoneCn = "黑衣森林中央林区",
            Hold = 30 * 60, Include = new[] { "Rain" }, Horizon = HorizonWeather,
            Label = "连续 30 分钟小雨",
        },
        ["The Garlok"] = new()      // 伽洛克：连续 200 分钟不下雨（小雨/暴雨都重置）
        {
            Kind = "weather_hold", Zone = "Eastern La Noscea", ZoneCn = "东拉诺西亚",
            Hold = 200 * 60, Exclude = new[] { "Rain", "Showers" }, Horizon = HorizonWeather,
            Label = "连续 200 分钟无雨",
        },

        // ── 天气即时型 ──
        ["Zona Seeker"] = new()     // 虚无探索者：西萨纳兰碧空/晴朗时钓上铜镜
        {
            Kind = "weather", Zone = "Western Thanalan", ZoneCn = "西萨纳兰",
            Include = new[] { "Clear Skies", "Fair Skies" }, Horizon = HorizonWeather,
            Label = "西萨纳兰碧空/晴朗",
        },

        // ── ET 时段型 ──
        ["Croque-Mitaine"] = new() { Kind = "et_window", Hours = new[] { (19.0, 22.0) }, Label = "ET 19:00–22:00" },
        ["Bonnacon"] = new() { Kind = "et_window", Hours = new[] { (8.0, 11.0) }, Label = "ET 8:00–11:00" },
        ["Thousand-cast Theda"] = new() { Kind = "et_window", Hours = new[] { (17.0, 21.0) }, Label = "ET 17:00–21:00" },
        ["Gamma"] = new() { Kind = "et_window", Hours = new[] { (17.0, 32.0) }, Label = "ET 17:00–次日 08:00" },

        // ── ET 日期（月相）型 ──
        ["Mindflayer"] = new()      // 夺心魔：新月日 1 是 00:00 起，日 2–4 是 17:00 起
        {
            Kind = "et_window", Hours = new[] { (17.0, 27.0) }, FirstDayHours = new[] { (0.0, 27.0) },
            DayFilter = NewMoon, Horizon = HorizonMoon,
            Label = "新月：日1 00:00–次日03:00；日2–4 17:00–次日03:00",
        },
        ["Croakadile"] = new()      // 咕尔呱洛斯：满月 ET 日 16–19 的 17:00 起
        {
            Kind = "et_window", Hours = new[] { (17.0, 27.0) },
            DayFilter = FullMoon16to19, Horizon = HorizonMoon,
            Label = "满月（ET 日 16–19）",
        },
        ["Okina"] = new()           // 巨大鳐：满月 ET 日 16 的 12:00 起，持续 4 天
        {
            Kind = "et_window", Hours = new[] { (12.0, 108.0) },
            DayFilter = FullMoon16, Horizon = HorizonMoon,
            Label = "满月（ET 日 16）",
        },

        // ── ET 时段 + 天气 ──
        ["Burfurlur the Canny"] = new()   // 布弗鲁：迷津 ET 9:00–17:00 且碧空/晴朗
        {
            Kind = "et_window", Hours = new[] { (9.0, 17.0) },
            Zone = "Labyrinthos", ZoneCn = "迷津", Weather = new[] { "Clear Skies", "Fair Skies" },
            Label = "ET 9:00–17:00 且迷津碧空/晴朗",
        },
        ["Kirlirger the Abhorrent"] = new()   // 厌忌之人 奇里格：新月 ET 日 1–4 的 00:00–08:00 且薄雾
        {
            Kind = "et_window", Hours = new[] { (0.0, 8.0) }, DayFilter = NewMoon,
            Zone = "Urqopacha", ZoneCn = "奥阔帕恰山", Weather = new[] { "Fog" },
            Horizon = HorizonMoon,
            Label = "新月 ET 日 1–4 的 00:00–08:00 且薄雾",
        },

        // ── 采集点开放时段 ──
        ["Gandarewa"] = new()
        {
            Kind = "et_window", Hours = new[] { (2.0, 6.0), (14.0, 18.0) },
            Label = "翻云雾海采集点 ET 2:00–6:00 / 14:00–18:00",
        },
    };

    public static bool HasSpec(string en) => Specs.ContainsKey(en);

    // 20 秒一桶的缓存：窗口是分钟级的，没必要每帧把 320 个 ET 日算一遍
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static long cacheBucket = long.MinValue;

    /// <summary>一句话的条件窗口说明；没有条件规则的怪返回空串。</summary>
    public static string Describe(string en, double now)
    {
        if (!Specs.TryGetValue(en, out var sp)) return "";

        var bucket = (long)now / 20;
        if (bucket != cacheBucket)
        {
            Cache.Clear();
            cacheBucket = bucket;
        }
        if (Cache.TryGetValue(en, out var hit)) return hit;

        var text = Build(sp, now);
        Cache[en] = text;
        return text;
    }

    /// <summary>悬浮提示用：原始条件描述。</summary>
    public static string LabelOf(string en) => Specs.TryGetValue(en, out var sp) ? sp.Label : "";

    /// <summary>当前正在进行、否则最近的下一个窗口；没有规则或没有窗口时返回 null。</summary>
    public static Window? NextWindow(string en, double now)
    {
        if (!Specs.TryGetValue(en, out var sp)) return null;
        var ws = Windows(sp, now);
        foreach (var w in ws)
            if (w.Start <= now && now < w.End) return w;
        return ws.Count > 0 ? ws[0] : null;
    }

    private static string Build(Spec sp, double now)
    {
        var ws = Windows(sp, now);
        var mark = sp.Label.Length > 0 ? "（" + sp.Label + "）" : "";

        if (ws.Count == 0)
            return $"条件窗口：{HorizonText(sp.Horizon)}内没有{mark}";

        // 正在窗口里（含未开始但已经在候选里的第一条）
        var cur = -1;
        for (var i = 0; i < ws.Count; i++)
            if (ws[i].Start <= now && now < ws[i].End) { cur = i; break; }

        if (cur >= 0)
        {
            var w = ws[cur];
            return $"条件窗口 进行中 · 剩 {FmtDur(w.End - now)} · {Range(w.Start, w.End)}";
        }

        var next = ws[0];
        return $"条件窗口 {FmtDur(next.Start - now)}后 · {Range(next.Start, next.End)}";
    }

    // ───────────────────────── 推算 ─────────────────────────

    private static List<Window> Windows(Spec sp, double now) => sp.Kind switch
    {
        "weather_hold" or "weather" => WeatherWindows(sp, now),
        "et_window" => EtWindows(sp, now),
        _ => new List<Window>(),
    };

    /// <summary>天气连续/即时型：把天气序列按判据合并成段，够长（hold）且没过去的才算窗口。</summary>
    private static List<Window> WeatherWindows(Spec sp, double now)
    {
        if (!Zones.TryGetValue(sp.Zone, out var rate)) return new List<Window>();

        var periods = (int)(sp.Horizon / WeatherPeriod) + 8;
        var n = periods * 2;
        var starts = new double[n];
        var ok = new bool[n];
        var baseStart = CurrentPeriodStart(now);
        for (var i = 0; i < n; i++)
        {
            var t = baseStart + (i - periods) * WeatherPeriod;
            starts[i] = t;
            var w = WeatherAt(rate, t);
            ok[i] = sp.Include.Length > 0
                ? Array.IndexOf(sp.Include, w) >= 0
                : sp.Exclude.Length > 0 ? Array.IndexOf(sp.Exclude, w) < 0 : true;
        }

        var outList = new List<Window>();
        var k = 0;
        while (k < n)
        {
            if (!ok[k]) { k++; continue; }
            var j = k;
            while (j + 1 < n && ok[j + 1]) j++;
            var runStart = starts[k];
            var runEnd = starts[j] + WeatherPeriod;
            var wStart = runStart + sp.Hold;
            if (runEnd > wStart && runEnd > now)
                outList.Add(new Window(wStart, runEnd, sp.ZoneCn));
            k = j + 1;
        }
        outList.Sort((a, b) => a.Start.CompareTo(b.Start));
        return outList;
    }

    /// <summary>ET 时段型：逐 ET 日铺窗口，日期过滤，必要时和天气求交。</summary>
    private static List<Window> EtWindows(Spec sp, double now)
    {
        var d0 = (long)(now / EtDay);
        var list = new List<Window>();
        for (var d = d0 - 1; d < d0 + DaysAhead; d++)
        {
            if (!sp.DayFilter((int)d)) continue;
            var dom = MoonDay((int)d);
            // 新月日1 用 FirstDayHours（如夺心魔 00:00–次日03:00），其余日用 Hours（日2-4 的 17:00–次日03:00）
            var hours = (sp.FirstDayHours.Length > 0 && dom == 1) ? sp.FirstDayHours : sp.Hours;
            foreach (var (from, to) in hours)
            {
                var s = d * EtDay + from * EtHour;
                var e = d * EtDay + to * EtHour;
                if (e <= now) continue;
                if (sp.Zone.Length > 0 && sp.Weather.Length > 0 && Zones.TryGetValue(sp.Zone, out var rate))
                    list.AddRange(IntersectWeather(s, e, rate, sp.Weather));
                else
                    list.Add(new Window(s, e, sp.ZoneCn));
            }
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list.Where(w => w.Start < now + sp.Horizon).ToList();
    }

    /// <summary>把 [s,e) 按天气周期切开，只留天气命中的子段。</summary>
    private static IEnumerable<Window> IntersectWeather(double s, double e, (int Bound, string Name)[] rate, string[] weather)
    {
        var p = CurrentPeriodStart(s);
        while (p < e)
        {
            var w = WeatherAt(rate, p);
            if (Array.IndexOf(weather, w) >= 0)
            {
                var a = Math.Max(s, p);
                var b = Math.Min(e, p + WeatherPeriod);
                if (b > a) yield return new Window(a, b, WeatherCn.GetValueOrDefault(w, w));
            }
            p += WeatherPeriod;
        }
    }

    private static string WeatherAt((int Bound, string Name)[] rate, double t)
    {
        var target = ForecastSeed(t);
        foreach (var (bound, name) in rate)
            if (target < bound) return name;
        return rate[^1].Name;
    }

    /// <summary>0..99 的确定性天气种子（同一周期内全地图相同）。</summary>
    public static int ForecastSeed(double unixSeconds)
    {
        var unix = (long)unixSeconds;
        var bell = unix / 175;
        var increment = (bell + 8 - bell % 8) % 24;
        var totalDays = unix / 4200;
        var calcBase = (uint)(totalDays * 100 + increment);
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>最近一次 ET 0/8/16 点对应的现实秒。</summary>
    public static double CurrentPeriodStart(double unixSeconds)
    {
        var bell = (long)(unixSeconds / EtHour);
        var periodIndex = bell / 8;
        return periodIndex * 8 * EtHour;
    }

    /// <summary>(ET 日序号, 时, 分)；日序号从 0 起，界面上的 1..32 要再 %32+1。</summary>
    public static (long DayCount, int Hour, int Minute) EtParts(double unixSeconds)
    {
        var totalHours = unixSeconds / EtHour;
        var dayCount = (long)(totalHours / 24);
        var hourInDay = totalHours % 24;
        var hour = (int)hourInDay;
        var minute = (int)Math.Round((hourInDay - hour) * 60) % 60;
        return (dayCount, hour, minute);
    }

    private static string Range(double s, double e)
    {
        var (ds, hs, ms) = EtParts(s);
        var (de, he, me) = EtParts(e);
        var head = $"{MonthNames[(int)(ds / 32 % 12)]}{ds % 32 + 1}日 {hs:00}:{ms:00}";
        var tail = (ds == de)
            ? $"{he:00}:{me:00}"
            : $"{MonthNames[(int)(de / 32 % 12)]}{de % 32 + 1}日 {he:00}:{me:00}";
        return $"ET {head}–{tail}";
    }

    private static string FmtDur(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        if (s < 60) return s + " 秒";
        var m = s / 60;
        if (m < 60) return m + " 分" + (s % 60 > 0 ? " " + s % 60 + " 秒" : "");
        var h = m / 60;
        if (h < 24) return h + " 小时" + (m % 60 > 0 ? " " + m % 60 + " 分" : "");
        return h / 24 + " 天" + (h % 24 > 0 ? " " + h % 24 + " 小时" : "");
    }

    private static string HorizonText(double horizon) =>
        horizon >= 86400 ? (int)(horizon / 86400) + " 天" : (int)(horizon / 3600) + " 小时";
}
