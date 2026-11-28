using Lens = BrimDeck.Core.WeatherMetric;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using City = BrimDeck.Core.City;
using State = BrimDeck.WeatherData.State;
namespace BrimDeck;

internal sealed partial class WeatherView : UserControl
{
    // 分钟点的 Time 是区间起点。来源写明跨度时直接用；老快照按相邻时间戳推导，最后才按来源兜底。
    private static int MinuteSpan(WeatherSnapshot s, MinutePoint[] points, int index, DateTimeOffset start)
    {
        if (points[index].SpanMinutes > 0) return points[index].SpanMinutes;
        if (index + 1 < points.Length && WeatherClock.MinuteAt(s, points[index + 1].Time) is { } next)
        {
            int gap = (int)Math.Round((next - start).TotalMinutes);
            if (gap is > 0 and <= 60) return gap;
        }
        if (index > 0 && WeatherClock.MinuteAt(s, points[index - 1].Time) is { } previous)
        {
            int gap = (int)Math.Round((start - previous).TotalMinutes);
            if (gap is > 0 and <= 60) return gap;
        }
        return s.Sources.TryGetValue("Minutely", out var source) && source.Contains("Open-Meteo", StringComparison.OrdinalIgnoreCase) ? 15 : 5;
    }

    private void ScheduleBoundary(WeatherSnapshot s, DateTimeOffset now)
    {
        var midnight = now.Date.AddDays(1);
        var next = WeatherClock.MinuteAt(s, midnight.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture))
            ?? now.AddDays(1);
        foreach (var warning in s.Warnings)
            if (warning.ExpireTime is { } expiry && expiry > now && expiry < next) next = expiry;
        for (int i = 0; i < s.Minutes.Length; i++)
            if (WeatherClock.MinuteAt(s, s.Minutes[i].Time) is { } at)
            {
                // 起点要重画「开始下雨」，终点要重画正在进行的区间结束。
                if (at >= now && at < next) next = at.AddMilliseconds(1);
                var end = at.AddMinutes(MinuteSpan(s, s.Minutes, i, at));
                if (end > now && end < next) next = end.AddMilliseconds(1);
            }
        _nextBoundary = next;
        _timeBoundary.Stop();
        _timeBoundary.Interval = next > now ? next - now : TimeSpan.FromSeconds(1);
        if (IsLoaded && IsVisible) _timeBoundary.Start();
    }


    // 把任意粒度的分钟点（和风 5min×24、Open-Meteo 15min×8）重采样成 24 格 × 5 分钟，
    // 每格取区间峰值，两种来源画出来形状一致。
    private void DrawMinute(WeatherSnapshot s)
    {
        minuteBars.Children.Clear();
        // 按真实时间戳剔除已经结束的区间；正在进行的区间要保留，否则下雨时会说无降水。
        var nowAt = WeatherClock.Now(s);
        var ordered = s.Minutes;
        var live = new List<MinutePoint>();
        for (int i = 0; i < ordered.Length; i++)
            if (WeatherClock.MinuteAt(s, ordered[i].Time) is { } at && at.AddMinutes(MinuteSpan(s, ordered, i, at)) > nowAt)
                live.Add(ordered[i]);
        var points = live.Select(point => point.Precip).ToArray();
        if (points.Length == 0)
        {
            _minuteHasRain = false;
            minuteNote.Text = "";
            UpdateMinuteVisibility();
            return;
        }
        const int slots = 24;
        var values = new double[slots];
        for (int i = 0; i < slots; i++)
        {
            int from = i * points.Length / slots;
            int to = Math.Max(from + 1, (i + 1) * points.Length / slots);
            double peak = 0;
            for (int j = from; j < Math.Min(to, points.Length); j++)
                // 缺失点是 null，跳过。
                if (points[j] is { } value) peak = Math.Max(peak, value);
            values[i] = peak;
        }
        _minuteHasRain = values.Any(v => v > 0);
        // 和风原文摘要带「X 分钟后」这类相对时间，只在本次抓取的缓存周期内照抄；跨周期继续显示会越来越不准。
        // Open-Meteo 没有摘要，统一按第一个有效雨点的真实时间戳减城市当前时刻算倒计时。
        bool summaryFresh = DateTimeOffset.UtcNow - s.FetchedAt < TimeSpan.FromMinutes(10);
        minuteNote.Text = summaryFresh && s.MinuteSummary is { Length: > 0 } summary && points.Any(point => point is > 0)
            ? summary : RainStartNote(s, live.ToArray());
        double peakValue = Math.Max(0.2, values.Max());
        // 无雨时整行本来就隐藏，不重建 24 个柱子。
        if (!_minuteHasRain) { UpdateMinuteVisibility(); return; }
        foreach (double v in values)
        {
            double ratio = Math.Min(1, v / peakValue);
            minuteBars.Children.Add(new Border
            {
                Width = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Height = v <= 0 ? 2 : 2 + ratio * 20,
                Background = new SolidColorBrush(v <= 0
                    ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                    : ratio > 0.66 ? Color.FromRgb(0x4F, 0x93, 0xFF) : ratio > 0.33 ? Color.FromRgb(0x7F, 0xB4, 0xFF) : Color.FromRgb(0xA9, 0xCD, 0xFF))
            });
        }
        UpdateMinuteVisibility();
    }
    // 没有来源总结时的倒计时：取第一个有雨点的时间戳（与 Minutes 同序），减城市当前时刻。
    private static string RainStartNote(WeatherSnapshot s, MinutePoint[] points)
    {
        int first = Array.FindIndex(points, p => p.Precip is > 0);
        if (first < 0) return Loc.T("未来 2 小时无降水", "No rain in the next 2 hours");
        // 调用方 DrawMinute 已按时间戳过滤，这里每个点都有可解析的时间戳。
        var start = WeatherClock.MinuteAt(s, points[first].Time)!.Value;
        int minutes = Math.Max(0, (int)Math.Ceiling((start - WeatherClock.Now(s)).TotalMinutes));
        return minutes == 0 ? Loc.T("正在下雨", "Raining now")
            : Loc.T($"{minutes} 分钟后开始下雨", $"Rain in {minutes} min");
    }
    // 无数据或无降水整行归零（不占位）；只在逐时模式、真会下雨且高度够时展开；
    // 逐日模式优先保证 24 天预报的可用高度，不显示窄条；窄窗口只留文字不画柱。
    private void UpdateMinuteVisibility()
    {
        bool show = !_indicesMode && _hourlyMode && _last is { Minutes.Length: > 0 } && _minuteHasRain && Height >= 200;
        // 窄条按 34px 从预报区里直接扣走：扣完如果连档 2（有图标、有温度）都装不下，就先不显示窄条。
        // 旧版只看「有雨且高度 ≥ 200」，这 34px 常把整块预报压到整行折叠，摘要被迫居中、上下空出一大片。
        if (show)
        {
            double measured = summaryMinHeight;
            // 与预报升档同一阈值：窄条要占 34px，只有升到档 2 后才有空间容纳图标行。
            show = Height - 16 - 34 - measured >= StateHeight(2) + UpgradeMargin;
        }
        minuteRow.Height = show ? new GridLength(34) : new GridLength(0);
        minuteStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        minuteBars.Visibility = Width >= 420 ? Visibility.Visible : Visibility.Collapsed;
    }
}
