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
    private void DrawCelestialView(WeatherSnapshot s)
    {
        todayGlow.Visibility = Visibility.Collapsed;
        DateTime now = WeatherClock.Now(s).DateTime;
        double width = Math.Max(30, chart.Width), height = ChartBand();
        bool showArcs = ChartHeightFor(_forecastLevel) >= CelestialArcHeight;
        double band = Math.Max(26, height - 26);
        double horizon = showArcs ? Math.Max(26, height - 72) : Math.Max(26, band - 34);
        Color gold = Color.FromRgb(255, 201, 77), silver = Color.FromRgb(226, 236, 248);
        var marks = new List<(DateTime at, string name, bool sun)>();
        var arcs = new List<(DateTime rise, DateTime set, DateTime peak, bool sun)>();
        var estimatedRises = new HashSet<(DateTime rise, bool sun)>();
        void Collect(string[] rises, string[] sets, string[] peaks, bool sun)
        {
            DateTime[] Times(string[] raw, string name)
            {
                var dates = new SortedSet<DateTime>();
                foreach (var value in raw)
                    if (value?.Length >= 16 && DateTime.TryParseExact(value[..16], "yyyy-MM-dd'T'HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && dates.Add(at))
                        marks.Add((at, name, sun));
                return dates.ToArray();
            }
            var up = Times(rises, sun ? Loc.T("日升", "Sunrise") : Loc.T("月升", "Moonrise"));
            var down = Times(sets, sun ? Loc.T("日落", "Sunset") : Loc.T("月落", "Moonset"));
            var high = Times(peaks, sun ? Loc.T("正午", "Noon") : Loc.T("月中天", "Moon peak"));
            for (int i = 0; i < up.Length; i++)
            {
                var rise = up[i];
                var set = down.FirstOrDefault(t => t > rise);
                if (set == default || (i + 1 < up.Length && up[i + 1] <= set)) continue;
                var peak = high.FirstOrDefault(t => t > rise && t < set);
                arcs.Add((rise, set, peak == default ? rise + (set - rise) / 2 : peak, sun));
            }
            // Some forecasts begin with a moonset and omit the preceding evening.
            // Use the following complete cycle only to shape the partial track.
            if (!sun && down.Length > 0 && up.Length > 0 && down[0] < up[0])
            {
                var set = down[0];
                var next = arcs.FirstOrDefault(a => !a.sun && a.rise == up[0]);
                if (next != default && next.rise - set < TimeSpan.FromDays(1)
                    && next.set - next.rise < TimeSpan.FromDays(1))
                {
                    var rise = set - (next.set - next.rise);
                    var peak = high.FirstOrDefault(t => t > rise && t < set);
                    arcs.Add((rise, set, peak == default ? rise + (set - rise) / 2 : peak, false));
                    estimatedRises.Add((rise, false));
                }
            }
        }
        Collect(s.Days.Select(d => d.Sunrise ?? "").ToArray(), s.Days.Select(d => d.Sunset ?? "").ToArray(), s.Days.Select(d => d.SolarNoon ?? "").ToArray(), true);
        Collect(s.Days.Select(d => d.Moonrise ?? "").ToArray(), s.Days.Select(d => d.Moonset ?? "").ToArray(), s.Days.Select(d => d.MoonTransit ?? "").ToArray(), false);
        var active = arcs.Where(a => a.rise <= now && now < a.set).OrderByDescending(a => a.sun).FirstOrDefault();
        var center = active == default ? now : active.peak;
        var start = center.AddHours(-12);
        if (now < start || now >= start.AddHours(24)) start = now.AddHours(-12);
        var end = start.AddHours(24);
        double X(DateTime at) => (at - start).TotalHours * width / 24;
        void Line(double x1, double y1, double x2, double y2, Brush ink, double thickness)
            => chart.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = ink, StrokeThickness = thickness });
        var muted = new SolidColorBrush(Color.FromArgb(55, 155, 172, 194));
        Line(0, horizon, width, horizon, muted, 1);
        Line(0, band, width, band, muted, 5);
        var visible = arcs.Where(a => a.rise < end && a.set > start).ToArray();
        var boundaries = visible.SelectMany(a => new[] { a.rise, a.set })
            .Where(t => t > start && t < end).Append(start).Append(end).Distinct().Order().ToArray();
        for (int i = 1; i < boundaries.Length; i++)
        {
            var from = boundaries[i - 1];
            var to = boundaries[i];
            var sun = visible.FirstOrDefault(a => a.sun && a.rise < to && a.set > from);
            var moon = visible.FirstOrDefault(a => !a.sun && a.rise < to && a.set > from);
            if (sun == default && moon == default) continue;
            Brush fill;
            if (sun != default && moon != default)
            {
                var overlapStart = sun.rise > moon.rise ? sun.rise : moon.rise;
                var overlapEnd = sun.set < moon.set ? sun.set : moon.set;
                Color first = sun.rise <= moon.rise ? gold : silver;
                Color last = sun.set >= moon.set ? gold : silver;
                var gradient = new LinearGradientBrush(first, last, 0)
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(X(overlapStart), band),
                    EndPoint = new Point(X(overlapEnd), band)
                };
                if (first == last)
                    gradient.GradientStops.Add(new GradientStop(first == gold ? silver : gold, .5));
                fill = gradient;
            }
            else fill = new SolidColorBrush(sun != default ? gold : silver);
            Line(X(from), band, X(to), band, fill, 5);
        }
        foreach (var arc in showArcs ? visible : [])
        {
            var left = arc.rise < start ? start : arc.rise;
            var right = arc.set > end ? end : arc.set;
            var ink = new SolidColorBrush(arc.sun ? gold : silver);
            double Y(DateTime at)
            {
                double f = at <= arc.peak ? (at - arc.rise) / (arc.peak - arc.rise) : (arc.set - at) / (arc.set - arc.peak);
                return horizon - Math.Sin(f * Math.PI / 2) * (horizon - 14);
            }
            var curve = new System.Windows.Shapes.Polyline { Stroke = ink, StrokeThickness = 2 };
            if (estimatedRises.Contains((arc.rise, arc.sun)))
            {
                curve.StrokeDashArray = new DoubleCollection { 4, 2 };
                curve.ToolTip = Loc.T("缺少前一晚月升记录，轨迹按相邻周期估算", "Estimated track: previous moonrise unavailable");
            }
            for (int i = 0; i <= 80; i++)
            {
                var at = left + (right - left) * (i / 80.0);
                curve.Points.Add(new Point(X(at), Y(at)));
            }
            chart.Children.Add(curve);
            if (now < arc.rise || now >= arc.set) continue;
            var glyph = new WeatherGlyph(0, arc.sun) { Width = 24, Height = 24 };
            Canvas.SetLeft(glyph, X(now) - 12); Canvas.SetTop(glyph, Y(now) - 12); chart.Children.Add(glyph);
        }
        int today = Array.FindIndex(s.Days, d => d.Date == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        bool daylight = arcs.Any(a => a.sun && a.rise.Date == now.Date) ? active.sun : s.IsDay;
        FrameworkElement? centerGlyph = !showArcs ? null
            : daylight ? new WeatherGlyph(0, true)
            : today >= 0 && today < s.Days.Length && !string.IsNullOrWhiteSpace(s.Days[today].MoonPhase)
                ? new MoonPhaseIcon(s.Days[today].MoonPhase!) : null;
        if (centerGlyph is not null)
        {
            centerGlyph.Width = centerGlyph.Height = 52;
            centerGlyph.IsHitTestVisible = false;
            Canvas.SetLeft(centerGlyph, (width - centerGlyph.Width) / 2);
            Canvas.SetTop(centerGlyph, (14 + horizon - centerGlyph.Height) / 2);
            chart.Children.Add(centerGlyph);
        }
        var todayMoonrise = marks.FirstOrDefault(m => !m.sun && m.name == Loc.T("月升", "Moonrise") && m.at.Date == now.Date);
        if (todayMoonrise != default && (todayMoonrise.at < start || todayMoonrise.at >= end))
        {
            var caption = new TextBlock
            {
                Text = Loc.T("今日月升 ", "Moonrise today ") + todayMoonrise.at.ToString("HH:mm"),
                FontSize = 11, Foreground = Ink(silver)
            };
            Canvas.SetLeft(caption, 6); Canvas.SetTop(caption, showArcs ? 0 : Math.Max(0, horizon - 14)); chart.Children.Add(caption);
        }
        var captions = new List<(TextBlock label, double x, double left)>();
        double nextLeft = 0;
        foreach (var m in marks.Where(m => m.at >= start && m.at < end).OrderBy(m => m.at))
        {
            var label = new TextBlock
            {
                Text = m.name + "\n" + m.at.ToString("HH:mm"), TextAlignment = TextAlignment.Center,
                FontSize = 9, Foreground = Ink(m.sun ? gold : silver)
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = X(m.at), left = Math.Max(nextLeft, x - label.DesiredSize.Width / 2);
            captions.Add((label, x, left));
            nextLeft = left + label.DesiredSize.Width + 4;
        }
        double captionRight = width;
        for (int i = captions.Count - 1; i >= 0; i--)
        {
            var (label, x, preferredLeft) = captions[i];
            double left = Math.Min(preferredLeft, captionRight - label.DesiredSize.Width);
            captionRight = left - 4;
            double top = horizon + 2;
            Canvas.SetLeft(label, left); Canvas.SetTop(label, top); chart.Children.Add(label);
            chart.Children.Add(new Line
            {
                X1 = left + label.DesiredSize.Width / 2, Y1 = top + label.DesiredSize.Height,
                X2 = x, Y2 = band - 2.5, Stroke = label.Foreground, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            });
        }
        int step = width >= 816 ? 1 : width >= 576 ? 2 : width >= 384 ? 3 : 4;
        for (var at = start.Date; at < end; at = at.AddHours(step))
        {
            if (at < start) continue;
            var tick = new TextBlock { Text = at.ToString(at.Hour == 0 ? "M/d" : "HH:mm"), FontSize = 8.5,
                Foreground = ColMuted };
            tick.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tick, Math.Clamp(X(at) - tick.DesiredSize.Width / 2, 0, Math.Max(0, width - tick.DesiredSize.Width)));
            Canvas.SetTop(tick, height - 12); chart.Children.Add(tick);
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
        Canvas.SetLeft(dot, X(now) - 3.5); Canvas.SetTop(dot, band - 3.5); chart.Children.Add(dot);
    }
    // 风向箭头：几何按「指向上」画，直接旋到风的来向；气象风向就是来向，北风指北（向上）。
    // 角度先归到 8 方位，逐时箭头只有 0/45/…/315 八种朝向，和风向文字同一档。
}
