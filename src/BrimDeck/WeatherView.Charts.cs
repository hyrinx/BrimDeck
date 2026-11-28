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
    private void DrawChart(WeatherSnapshot s)
    {
        chart.Children.Clear();
        // 真正按当前宽度画过了：撤掉拖宽期间的横向缩放，并记下这版几何的基准宽度。
        chart.RenderTransform = null;
        _chartDrawnWidth = Math.Max(30, chart.Width);
        // 镜头优先：非气温镜头只存在于逐时（逐日只有高低温两列数），所以这里直接按指标画。
        // 对应数组整体缺失时退回气温曲线，宁可少一个镜头也不要给一张空图。
        // 镜头只在真正要画时才从结构化 HourPoint 取一次序列；缺值 null → NaN 供通用折线/柱状函数跳过。
        double[] Series(Func<HourPoint, double?> pick) => s.Hours.Select(h => pick(h) ?? double.NaN).ToArray();
        static bool Has(double[] a) => a.Length > 0 && a.Any(v => !double.IsNaN(v));
        switch (_lens)
        {
            case Lens.Feels when Has(Series(h => h.FeelsLike)): DrawHourlyLine(Series(h => h.FeelsLike), new SolidColorBrush(Color.FromRgb(255, 176, 102)), true); return;
            case Lens.Humidity when Series(h => h.Humidity).Any(v => !double.IsNaN(v)): DrawBars(Series(h => h.Humidity), 100, "%"); return;
            case Lens.Wind when Has(Series(h => h.WindKmh)): DrawBars(Series(h => h.WindKmh), double.NaN, ""); return;
            case Lens.Gust when Has(Series(h => h.WindGustKmh)): DrawBars(Series(h => h.WindGustKmh), double.NaN, "", 0, Color.FromRgb(126, 212, 226)); return;
            case Lens.Precip when Has(Series(h => h.PrecipMm)): DrawBars(Series(h => h.PrecipMm), double.NaN, "", 1, Color.FromRgb(96, 168, 246)); return;
            case Lens.Pressure when Has(Series(h => h.PressureHpa)): DrawHourlyLine(Series(h => h.PressureHpa), new SolidColorBrush(Color.FromRgb(168, 186, 208)), false); return;
            case Lens.DewPoint when Has(Series(h => h.DewPointC)): DrawHourlyLine(Series(h => h.DewPointC), new SolidColorBrush(Color.FromRgb(88, 146, 220)), true); return;
            case Lens.Uv when Has(Series(h => h.Uv)): DrawBars(Series(h => h.Uv), double.NaN, "", 1, Color.FromRgb(255, 206, 112)); return;
            case Lens.Cloud when Has(Series(h => h.CloudPercent)): DrawBars(Series(h => h.CloudPercent), 100, "%", 0, Color.FromRgb(196, 214, 232)); return;
            case Lens.Visibility when Has(Series(h => h.VisibilityKm)): DrawHourlyLine(Series(h => h.VisibilityKm), new SolidColorBrush(Color.FromRgb(150, 204, 232)), false, 1); return;
            // 空气质量逐时预报：和风 /airquality/v1/hourly 的 AQI，按小时对齐；该小时无 AQI（null）跳过。
            case Lens.Aqi when Series(h => h.Aqi).Any(v => !double.IsNaN(v)):
                DrawBars(Series(h => h.Aqi), double.NaN, "", 0, Color.FromRgb(126, 208, 154)); return;
            case Lens.Celestial: DrawCelestialView(s); return;
        }
        if (_lens != Lens.Temp) { chart.Children.Add(Note(Loc.T("暂无该指标数据", "No data for this metric"))); return; }
        if (_hourlyMode) { DrawHourly(s); return; }
        var high = s.Days.Select(d => d.HighC ?? double.NaN).ToArray();
        var low = s.Days.Select(d => d.LowC ?? double.NaN).ToArray();
        var order = Order(s, forecast.Columns);
        var valid = order.Where(i => !double.IsNaN(high[i]) && !double.IsNaN(low[i])).ToArray();
        if (valid.Length < 2) return;                                     // 来源缺失温度时不出折线，避免 NaN 坐标
        double min = valid.Min(i => low[i]), range = Math.Max(1, valid.Max(i => high[i]) - min);
        int n = order.Length;
        double w = Math.Max(30, chart.Width), col = forecast.Columns > 0 ? Math.Max(2, w / forecast.Columns) : 0;   // 与预报网格同源：列宽按列数分，点数不足时右侧留空
        double X(int p) => (p + 0.5) * col;                              // 列中心：与 UniformGrid 每列几何一致
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩，上留标签、下留圆点半径
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        foreach (var (series, brush) in new[] { (high, (Brush)Brushes.Goldenrod), (low, (Brush)Brushes.DodgerBlue) })
        {
            DrawTrend(Enumerable.Range(0, n).Where(p => !double.IsNaN(series[order[p]])).Select(p => new Point(X(p), Y(series[order[p]]))).ToArray(), brush);
            for (int p = 0; p < n; p++)
            {
                int i = order[p]; if (double.IsNaN(series[i])) continue; double x = X(p), yy = Y(series[i]);
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush }; Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, yy - 3); chart.Children.Add(dot);
            }
        }
    }
    // 当日 24 小时逐时温度曲线：弹性高度带（随画布实际高度伸缩）、列中心取点，横轴按逐时列数均匀分列，
    private void DrawHourly(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        double[] temps = s.Hours.Select(h => h.Temperature ?? double.NaN).ToArray();
        var valid = Enumerable.Range(0, Math.Min(n, temps.Length)).Where(i => !double.IsNaN(temps[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => temps[i]), range = Math.Max(1, valid.Max(i => temps[i]) - min);
        var brush = ChartTemp;
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        DrawTrend(valid.Select(p => new Point(X(p), Y(temps[p]))).ToArray(), brush);
        foreach (int p in valid)
        {
            double yy = Y(temps[p]);
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush }; Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            var probe = new TextBlock { Text = $"{temps[p]:0}°", FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(probe, X(p) - probe.DesiredSize.Width / 2); Canvas.SetTop(probe, yy - 14); chart.Children.Add(probe);
        }
    }
    // 通用逐时折线：与 DrawHourly 同一套几何（弹性高度带、列中心取点），供体感镜头复用。
    // 与气温曲线一致，每个点都标数值（体感只在逐时里有逐点数据）。
    private void DrawHourlyLine(double[] data, Brush brush, bool degree, int digits = 0)
    {
        string fmt = "0" + (digits > 0 ? "." + new string('0', digits) : "");
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => data[i]), range = Math.Max(1, valid.Max(i => data[i]) - min);
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        DrawTrend(valid.Select(p => new Point(X(p), Y(data[p]))).ToArray(), brush);
        foreach (var p in valid)
        {
            double yy = Y(data[p]);
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush };
            Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            var label = new TextBlock { Text = degree ? data[p].ToString(fmt, CultureInfo.InvariantCulture) + "°" : data[p].ToString(fmt, CultureInfo.InvariantCulture), FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, yy - 14); chart.Children.Add(label);
        }
    }
    private void DrawTrend(Point[] points, Brush brush)
    {
        if (points.Length < 2) return;
        double Slope(int i) => (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        double Tangent(int i)
        {
            if (i == 0) return Slope(0);
            if (i == points.Length - 1) return Slope(i - 1);
            double before = Slope(i - 1), after = Slope(i);
            // Flatten at extrema and limit the slope so interpolation stays between samples.
            return Math.Sign(before) == Math.Sign(after)
                ? Math.CopySign(Math.Min(Math.Abs(before), Math.Abs(after)), after) : 0;
        }

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(points[0], false, false);
            double startSlope = Tangent(0);
            for (int i = 1; i < points.Length; i++)
            {
                Point start = points[i - 1], end = points[i];
                double dx = (end.X - start.X) / 3, endSlope = Tangent(i);
                path.BezierTo(new Point(start.X + dx, start.Y + dx * startSlope),
                    new Point(end.X - dx, end.Y - dx * endSlope), end, true, true);
                startSlope = endSlope;
            }
        }
        curve.Freeze();
        chart.Children.Add(new System.Windows.Shapes.Path { Data = curve, Stroke = brush, StrokeThickness = 2 });
    }

    // 逐时柱状：湿度/云量用固定量程，风、阵风、降水、紫外线按最大值自适应，柱底压在 50 这条基线上。
    private void DrawBars(double[] data, double max, string suffix, int digits = 0, Color? barColor = null)
    {
        string fmt = "0" + (digits > 0 ? "." + new string('0', digits) : "");
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length == 0) return;
        if (double.IsNaN(max) || max <= 0) max = Math.Max(1, valid.Max(i => data[i]));
        var brush = new SolidColorBrush(barColor ?? Color.FromRgb(112, 178, 255));
        // 固定「每 4 格一个」在宽窗口太空、窄窗口又太挤：量一次最长标签，按列宽决定间隔。
        string labelText(int p) => data[p].ToString(fmt, CultureInfo.InvariantCulture) + suffix;
        var probe = new TextBlock { Text = valid.Select(labelText).OrderByDescending(t => t.Length).First(), FontSize = 9 };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int step = Math.Max(1, (int)Math.Ceiling((probe.DesiredSize.Width + 2) / col));
        // 弹性柱状：柱底贴近画布下沿，柱高随画布实际高度伸缩；标签画在柱顶上方。
        double baseY = ChartBand() - 8, span = Math.Max(12, ChartBand() - 30);
        int ordinal = 0;
        foreach (var p in valid)
        {
            double h = Math.Max(2, data[p] / max * span);
            var bar = new System.Windows.Shapes.Rectangle { Width = Math.Max(3, col * 0.42), Height = h, Fill = brush, RadiusX = 2, RadiusY = 2, Opacity = 0.85 };
            Canvas.SetLeft(bar, X(p) - bar.Width / 2); Canvas.SetTop(bar, baseY - h); chart.Children.Add(bar);
            if (ordinal++ % step != 0) continue;
            var label = new TextBlock { Text = labelText(p), FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, baseY - h - 13); chart.Children.Add(label);
        }
    }
    // 天文镜头视口窗口左缘。窗口宽 24h，规则：当前时段所处天体弧以其中天为窗口中心——
    // 日弧以正午居中、月弧以月中天居中；两弧之间的晨昏间隙以「现在」居中。
    // 中天近似弧线时间中点，弧长 < 24h 时两侧余量各 (24−弧长)/2，当前曲线必落在视口内、不被 0/24 边界拆开；
    // 中天极端偏移时 clamp 进 [落端−12, 升端+12] 强制满足不跨条件，clamp 区间退化（弧长>24h）则退回弧线中点。
}
