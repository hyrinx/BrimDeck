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
    // 摘要区构型：指标卡列数 2–6、行数 2–3，Hero 按宽度分三档、余量档再放大一号。Fit（判要不要重建）
    // 与 DrawSummary（建控件）共用这一处，两侧永远不会漂移。指标区拿 Star 列，列数只决定「分几格」，
    // 格子本身永远铺满余宽。
    private readonly record struct SummaryPlan(int Columns, int Rows, double Icon, double Temp, double HeroWidth);
    private SummaryPlan PlanSummary(double width, int rows)
    {
        double inner = Math.Max(160, width - 28);       // 去掉 root 左右各 14 的外边距
        var (icon, temp, heroWidth) = rows == 3
            ? (3 * SummaryTileRow - 4, 54d, 276d)
            : (56d, 44d, 164d);
        int columns = Math.Clamp((int)((inner - heroWidth) / TilePitch), 1, 6);
        if (rows == 3)
        {
            int visible = _app.Settings.Weather.Chips.Count(chip => chip.Visible);
            columns = Math.Min(columns, Math.Max(1, (visible + rows - 1) / rows));
        }
        return new SummaryPlan(columns, rows, icon, temp, heroWidth);
    }

    private static int WarnRank(string level) => level.Trim().ToLowerInvariant() switch
    {
        "红色" or "red" or "purple" or "black" or "extreme" => 4,
        "橙色" or "orange" or "amber" or "severe" => 3,
        "黄色" or "yellow" or "moderate" => 2,
        "蓝色" or "blue" or "green" or "minor" => 1,
        "white" or "gray" or "unknown" => 0,
        _ => 2
    };
    private static string WarnLevelShort(string level) => level.Trim().ToLowerInvariant() switch
    {
        "红色" or "red" or "purple" or "black" or "extreme" => "红",
        "橙色" or "orange" or "amber" or "severe" => "橙",
        "黄色" or "yellow" or "moderate" => "黄",
        "蓝色" or "blue" or "green" or "minor" => "蓝",
        _ => level
    };
    // 摘要卡里的预警胶囊：只留「类型 · 等级」，点开可读全部预警；天气图标仍负责切生活指数。
    private Border WarningBadge(WarningItem w, IReadOnlyList<WarningItem> warnings, WeatherSnapshot s)
    {
        (Color bg, Color fg) = WarnRank(w.Level) switch
        {
            4 => (Color.FromArgb(0x38, 0xFF, 0x6B, 0x6B), Color.FromRgb(255, 176, 176)),
            3 => (Color.FromArgb(0x38, 0xFF, 0xA9, 0x4D), Color.FromRgb(255, 208, 156)),
            2 => (Color.FromArgb(0x34, 0xFF, 0xD6, 0x5E), Color.FromRgb(255, 230, 158)),
            _ => (Color.FromArgb(0x34, 0x7F, 0xB8, 0xFF), Color.FromRgb(184, 216, 255))
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "⚠", FontSize = 10, Foreground = Ink(fg), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        string name = w.Type.Length > 0
            ? (w.Level.Length > 0 ? $"{w.Type} · {WarnLevelShort(w.Level)}" : w.Type)
            : (w.Title.Length > 0 ? w.Title : w.Level);
        row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Ink(fg), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (warnings.Count > 1) row.Children.Add(new TextBlock { Text = $"+{warnings.Count - 1}", FontSize = 10, Foreground = Ink(fg), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border
        {
            Child = row, Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = w.Text.Length > 0
                ? (w.Title.Length > 0 ? w.Title + "\n" + w.Text : w.Text)
                : (w.Title.Length > 0 ? w.Title : null)
        };
        AutomationProperties.SetName(badge, Loc.T("气象预警", "Weather warning") + " " + name);
        badge.Focusable = true;
        badge.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { e.Handled = true; ShowWarnings(badge, warnings, s.Name); } };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowWarnings(badge, warnings, s.Name); };
        return badge;
    }

    // 预警详情：胶囊（含 +N）打开一个可聚焦的浮层，按等级列出全部有效预警的标题与正文；Esc 或点外部关闭。
    private Popup? _warningPopup;
    private void ShowWarnings(Border target, IReadOnlyList<WarningItem> warnings, string city)
    {
        var body = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T($"{city} · 全部预警", $"{city} · All warnings"),
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary),
            Margin = new Thickness(0, 0, 0, 6)
        });
        foreach (var item in warnings.OrderByDescending(w => WarnRank(w.Level)))
        {
            string heading = item.Type.Length > 0
                ? (item.Level.Length > 0 ? $"{item.Type} · {WarnLevelShort(item.Level)}" : item.Type)
                : item.Title;
            body.Children.Add(new TextBlock { Text = heading, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2) });
            string detail = string.Join("\n", new[] { item.Title, item.Text }.Where(part => part.Length > 0 && part != heading).Distinct(StringComparer.Ordinal));
            if (detail.Length > 0)
                body.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = ColText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        }
        var frame = new Border
        {
            Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320 },
            Background = UI.Brush(_look.Hex("popup")), BorderBrush = UI.Brush(_look.Hex("popupBorder")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 340, Focusable = true
        };
        var popup = new Popup
        {
            Child = frame, PlacementTarget = target, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true
        };
        frame.KeyDown += (_, key) => { if (key.Key == System.Windows.Input.Key.Escape) { popup.IsOpen = false; target.Focus(); key.Handled = true; } };
        AutomationProperties.SetName(frame, Loc.T("全部气象预警", "All weather warnings"));
        popup.IsOpen = true;
        _warningPopup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(_warningPopup, popup)) _warningPopup = null; };

        frame.Focus();
    }
    // 风速卡的标题直接带上 8 方位风向与风级，省一张卡；没有风向文字（如 Open-Meteo）时按度数归一。
    private string WindTitle(WeatherSnapshot s)
    {
        string dir = s.WindDirection;
        if (string.IsNullOrWhiteSpace(dir) && s.WindDirectionDeg is { } deg && double.IsFinite(deg))
            dir = WeatherCodes.DegreeToCompass(deg) + Loc.T("风", "");
        if (string.IsNullOrWhiteSpace(dir)) return Loc.T("风速", "Wind");
        return s.WindScale is { } scale && scale >= 0 ? $"{dir} {scale}{Loc.T("级", " Bft")}" : dir;
    }

    // 降水类型只显示类型名本身，图标按类型区分；未知/空保留通用“降水”，无降水用云朵而非水滴。
    private static string PrecipName(string type) => type switch
    {
        "rain" => Loc.T("雨", "Rain"),
        "snow" => Loc.T("雪", "Snow"),
        "ice" => Loc.T("冰粒", "Ice pellets"),
        "mixed" => Loc.T("雨夹雪", "Mixed"),
        "none" => Loc.T("无降水", "Dry"),
        _ => Loc.T("降水", "Precip")
    };
    private static WeatherMetaKind PrecipIcon(string type) => type switch
    {
        "rain" => WeatherMetaKind.Rain,
        "snow" => WeatherMetaKind.Snow,
        "ice" => WeatherMetaKind.Ice,
        "mixed" => WeatherMetaKind.Mixed,
        "none" => WeatherMetaKind.NoPrecip,
        _ => WeatherMetaKind.Precipitation
    };
    private static string PrecipTitle(WeatherSnapshot s) => PrecipName(s.PrecipType);

    // 空气卡：标题固定「空气质量」，主值显示「等级 数值」（如「优 35」），空格分割。
    // 和风给了 category 就直接用，否则按国标 HJ 633 由 AQI 换算。
    private static string AirLevel(WeatherSnapshot s)
    {
        if (s.Aqi is not { } aqi) return "--";
        string level = s.AqiText.Length > 0 ? s.AqiText : WeatherCodes.AqiText(aqi);
        return $"{level} {aqi}";
    }

    private string? PrecipTip(WeatherSnapshot s)
        => s.PrecipRateMmH is null && string.IsNullOrWhiteSpace(s.PrecipType)
            ? null
            : Loc.T($"强度 {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}", $"Rate {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}");
    // 左上：大图标 + 大字温度 + 高低温/预警；右上：随宽度扩展的实况读数卡（体感/湿度/风速…天文入口）。来源统一放右下角页脚。
    private void DrawSummary(WeatherSnapshot s, SummaryPlan? layout = null)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        summary.RowDefinitions.Clear();
        int today = WeatherClock.LiveIndex(s);    // 按城市当前日期在正序数组中定位“今天”；-1 = 跨日缓存对不上今天
        var plan = layout ?? PlanSummary(Width, _summaryPlan?.Rows ?? 2);
        _summaryPlan = plan;
        int cols = plan.Columns;
        // 左列 Hero 取自然宽（图标 + 温度 + 高低温），右列指标区吃掉全部余宽：
        // 卡片随面板连续伸缩，宽窗口不再在右侧留固定空档，窄窗口也不会把 Hero 挤成一条缝。
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(plan.HeroWidth) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.RowDefinitions.Add(new RowDefinition { Height = _compactSummary ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        // 左上角大图标是视图切换入口：悬浮播放天气动效，点击在「逐时/逐日预报」与「生活指数」间互换。
        var heroGlyph = new WeatherGlyph(s.WeatherCode ?? -1, s.IsDay, animated: true)
        {
            Width = plan.Icon, Height = plan.Icon, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T("点击切换生活指数 / 天气预报", "Click to toggle lifestyle indices / forecast")
        };
        AutomationProperties.SetName(heroGlyph, Loc.T("切换生活指数与天气预报", "Toggle lifestyle indices and forecast"));
        WeatherMotion.Attach(heroGlyph);
        heroGlyph.Focusable = true;
        heroGlyph.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { e.Handled = true; ToggleView(); } };
        heroGlyph.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleView(); };
        hero.Children.Add(heroGlyph);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tempRow = new StackPanel { Orientation = Orientation.Horizontal };
        tempRow.Children.Add(new TextBlock { Text = Degree(s.Temperature), FontSize = plan.Temp, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(tempRow);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        // 今日高低温取自 daily 数组（当天域），与实况温度分属两个数据域：实况来源成功而当天来源
        // 失败时数组可能为空，直接索引会越界异常、被 Render 的 catch 吞成整页空白。缺当天就显示
        // 占位符，实况大温度/图标/四张卡照常渲染——两个域互不牵连。
        bool hasDay = today >= 0 && today < s.Days.Length;   // 日期轴过期时不拿旧日期冒充今天的高低温
        string range = hasDay ? $"{Degree(s.Days[today].HighC)} / {Degree(s.Days[today].LowC)}" : "-- / --";
        // 有预警：高低温缩成小字挪到温度右侧，把温度下方这行让给最高等级的预警；无预警维持原样。
        // 按有效期过滤：到点的预警不需要等解除消息就不该再显示。
        var activeWarnings = s.Warnings.Where(w => w.ExpireTime is null || w.ExpireTime > DateTimeOffset.Now).ToArray();
        var warn = activeWarnings.OrderByDescending(w => WarnRank(w.Level)).FirstOrDefault();
        if (warn is not null)
        {
            tempRow.Children.Add(new TextBlock { Text = range, FontSize = 11, Margin = new Thickness(8, 0, 0, 6), Foreground = ColText, VerticalAlignment = VerticalAlignment.Bottom });
            bottom.Children.Add(WarningBadge(warn, activeWarnings, s));
        }
        else bottom.Children.Add(new TextBlock { Text = range, FontSize = 13, Foreground = ColText, VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(bottom);
        // 来源与刷新时刻统一放在右下角页脚，摘要卡不再单占一行，左侧因此更紧凑。
        hero.Children.Add(side);
        summary.Children.Add(new Viewbox {
            Child = hero, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = _compactSummary ? HorizontalAlignment.Center : HorizontalAlignment.Left
        });

        stats.Width = cols * TilePitch;
        stats.HorizontalAlignment = HorizontalAlignment.Right;
        // 读数卡与左侧大图标同一基准垂直居中，多列时上下沿才会齐平；行数随余量档两行或三行。
        stats.Columns = cols;
        stats.Children.Clear(); stats.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(stats, 1); summary.Children.Add(stats);
        // 卡片密度按实际格宽微调：格宽不足 98 时图标与间距各收一档，把宽度让给文字，
        // 84–120 的弹性列宽下标签才不至于整段被省略号吃掉。余量档 Hero 实际比登记宽 8px，一并扣掉。
        const double metaGlyph = 18, metaGap = 5, valueFont = 12;
        string rise = Clock(s.Days, today, d => d.Sunrise), set = Clock(s.Days, today, d => d.Sunset);
        // 同一列上下放相关读数：风速/阵风、体感/露点、湿度/降水、气压/能见度、空气/紫外线、云量/日月。
        // 风向/风级并入风速卡，降水类型/强度并入降水卡；日升/日落是镜头入口，始终放在最右下角。
        // UniformGrid 按行填充：先写满上排再写下排，同一列的上下两张才会成对。
        (WeatherMetaKind kind, Color ink, string label, string value, Lens? lens, string? tip)[] upper =
        [
            (WeatherVisuals.MetricKind(Lens.Wind), WeatherVisuals.MetricInk(Lens.Wind), WindTitle(s), Num(s.WindKmh, " km/h"), Lens.Wind, null),
            (WeatherVisuals.MetricKind(Lens.Feels), WeatherVisuals.MetricInk(Lens.Feels), Loc.T("体感", "Feels like"), Num(s.FeelsLike, "°"), Lens.Feels, null),
            (WeatherVisuals.MetricKind(Lens.Humidity), WeatherVisuals.MetricInk(Lens.Humidity), Loc.T("湿度", "Humidity"), s.HumidityPercent is { } hum && hum >= 0 ? $"{hum}%" : "--", Lens.Humidity, null),
            (WeatherVisuals.MetricKind(Lens.Pressure), WeatherVisuals.MetricInk(Lens.Pressure), Loc.T("气压", "Pressure"), Num(s.PressureHpa, " hPa"), Lens.Pressure, null),
            (WeatherVisuals.MetricKind(Lens.Aqi), WeatherVisuals.AqiInk(s.Aqi ?? -1), Loc.T("空气质量", "Air quality"), AirLevel(s), Lens.Aqi,
                WeatherCodes.AqiStandardLabel(s.AqiStandard) is { Length: > 0 } standard ? standard : null),
            (WeatherVisuals.MetricKind(Lens.Cloud), WeatherVisuals.MetricInk(Lens.Cloud), Loc.T("云量", "Cloud"), Num(s.CloudPercent, "%"), Lens.Cloud, null)
        ];
        (WeatherMetaKind kind, Color ink, string label, string value, Lens? lens, string? tip)[] lower =
        [
            (WeatherVisuals.MetricKind(Lens.Gust), WeatherVisuals.MetricInk(Lens.Gust), Loc.T("阵风", "Gusts"), Num(s.WindGustKmh, " km/h"), Lens.Gust, null),
            (WeatherVisuals.MetricKind(Lens.DewPoint), WeatherVisuals.MetricInk(Lens.DewPoint), Loc.T("露点", "Dew point"), Num(s.DewPointC, "°"), Lens.DewPoint, null),
            (PrecipIcon(s.PrecipType), WeatherVisuals.MetricInk(Lens.Precip), PrecipTitle(s), Num(s.PrecipitationMm, " mm", 1), Lens.Precip, PrecipTip(s)),
            (WeatherVisuals.MetricKind(Lens.Visibility), WeatherVisuals.MetricInk(Lens.Visibility), Loc.T("能见度", "Visibility"), Num(s.VisibilityKm, " km", 1), Lens.Visibility, null),
            (WeatherVisuals.MetricKind(Lens.Uv), WeatherVisuals.MetricInk(Lens.Uv), Loc.T("紫外线", "UV"), Num(s.UvIndex, ""), Lens.Uv, null),
            (WeatherVisuals.MetricKind(Lens.Celestial), WeatherVisuals.MetricInk(Lens.Celestial), Loc.T($"日升 {rise}", $"Sunrise {rise}"), Loc.T($"日落 {set}", $"Sunset {set}"), Lens.Celestial, null)
        ];
        _drawnChips = [.. _app.Settings.Weather.Chips];
        var available = upper.Concat(lower).ToDictionary(tile => tile.lens!.Value);
        var rows = _app.Settings.Weather.ChipRows(cols);
        var shown = new HashSet<WeatherMetric>();
        foreach (var row in rows)
        {
            var chips = row.Where(chip => chip.Visible).Take(cols).ToArray();
            AddRow(chips);
            foreach (var chip in chips) shown.Add(chip.Metric);
        }
        stats.Rows = 2;
        if (plan.Rows > 2)
        {
            // 续行按设置中的列取值：同一列的上下两项先于下一列。
            var extra = rows[0].Zip(rows[1])
                .SelectMany(pair => new[] { pair.First, pair.Second })
                .Where(chip => chip.Visible && !shown.Contains(chip.Metric))
                .Take(cols).ToArray();
            if (extra.Length > 0)
            {
                stats.Rows++;
                AddRow(extra);
            }
        }

        void AddRow(WeatherChip[] chips)
        {
            for (int i = chips.Length; i < cols; i++)
                stats.Children.Add(new Border { IsHitTestVisible = false });
            foreach (var chip in chips) AddTile(chip.Metric);
        }

        void AddTile(Lens metric)
        {
            var (kind, ink, label, value, lens, tip) = available[metric];
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 字号按实测宽度定：格宽 84–120 浮动，减内边距 14、描边 2、图标 16–18 + 间距 4–5 后，文字可用约 40–75。
            text.Children.Add(new TextBlock { Text = label, FontFamily = UI.PanelFont, FontSize = 9.5, Foreground = ColText, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = value, FontFamily = UI.PanelFont, FontSize = valueFont, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            var body = new Grid { VerticalAlignment = VerticalAlignment.Center };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            // 风速卡用 8 方位箭头指示风向；箭头优先跟标题文字同档，文字缺失时才按角度分档。
            // 其余卡用各自的元信息图标。
            double windAngle = WeatherCodes.Wind8Angle(s.WindDirection, s.WindDirectionDeg ?? double.NaN);
            if (lens == Lens.Wind && !double.IsNaN(windAngle))
            {
                var windArrow = WeatherVisuals.WindArrowIcon(windAngle, ink);
                windArrow.Margin = new Thickness(0, 0, metaGap, 0);
                body.Children.Add(windArrow);
            }
            else
                body.Children.Add(new WeatherMetaIcon(kind, ink) { Width = metaGlyph, Height = metaGlyph, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, metaGap, 0) });
            Grid.SetColumn(text, 1);
            body.Children.Add(text);
            bool active = lens is { } chosen && _lens == chosen;
            // 这些卡是同一组读数，底色统一用「白色低透明」叠在天空渐变上：哪一张都不抢眼，
            // 天空换季（晴/雨/夜）时也不用为每张卡单独配深色底。选中态才是唯一的例外——蓝色淡底 + 蓝描边。
            // 圆角 14 在只有 44px 高的格子里几乎成了胶囊，收到 10 才像卡片而不是药丸。
            Color fillIdle = active ? Color.FromArgb(44, 116, 172, 255) : Color.FromArgb(20, 255, 255, 255);
            Color fillHot = active ? Color.FromArgb(62, 128, 186, 255) : Color.FromArgb(36, 255, 255, 255);
            Color edgeIdle = active ? Color.FromArgb(140, 120, 178, 255) : Color.FromArgb(22, 255, 255, 255);
            Color edgeHot = active ? Color.FromArgb(196, 142, 196, 255) : Color.FromArgb(54, 255, 255, 255);
            var fill = new SolidColorBrush(fillIdle); var edge = new SolidColorBrush(edgeIdle);
            var tile = new Border
            {
                Background = fill, BorderBrush = edge,
                CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Padding = new Thickness(7, 4, 7, 4), Child = body,
                BorderThickness = new Thickness(1)
            };
            tile.ToolTip = tip is null ? value : $"{value} · {tip}";
            // 悬浮只换底色与描边，就是普通按钮的反馈：不缩放、不位移。
            // 卡片在窄窗口 2×2 里彼此只隔 6px，整体放大 1.035 会顶到邻卡、看着像在互相推挤；
            // 里面的图标也被同一个放大带着抖，所以这里连 WeatherMotion 都不挂。
            void Ink(Color toFill, Color toEdge)
            {
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toFill, TimeSpan.FromMilliseconds(110)));
                edge.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toEdge, TimeSpan.FromMilliseconds(110)));
            }
            // 只有绑定镜头的卡可点；其余卡保持静态，避免“能点却没反应”的错觉。
            // 可聚焦 + Enter/空格触发，读数卡对键盘用户同样可达。
            if (lens is { } pick)
            {
                tile.Cursor = System.Windows.Input.Cursors.Hand;
                tile.Focusable = true;
                tile.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { PickLens(pick); e.Handled = true; } };
                AutomationProperties.SetName(tile, label);
                tile.MouseEnter += (_, _) => Ink(fillHot, edgeHot);
                tile.MouseLeave += (_, _) => Ink(fillIdle, edgeIdle);
                tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; PickLens(pick); };
            }
            stats.Children.Add(tile);
        }
        // 对齐规则集中在 SyncSummaryAlignment：有预报条时顶对齐，收起时居中。
        SyncSummaryAlignment();
    }

    // 卡片镜头：同一张卡再点一次收回气温。逐日只有高低温，其余指标只有逐时数据，所以选非气温镜头时一并切到逐时。
    private void PickLens(Lens lens)
    {
        if (_last is not { } snap) return;
        _lens = _lens == lens ? Lens.Temp : lens;
        if (_lens != Lens.Temp) _hourlyMode = true;
        // 指数视图里点读数卡：镜头已经选好，顺手切回逐时预报——曲线画在下方的 days 里，
        // 当前显示的却是指数视图，不切回来就只是「点了没反应」。ToggleView 已含一次 force Fit。
        if (_indicesMode) { ToggleView(); DrawSummary(snap); return; }
        Fit(Width, Height, true);
        DrawSummary(snap);
    }

    // 结构化日期轴上第 index 天的时刻字段取 HH:mm；过期缓存（index=-1）或缺时刻都给 --:--。
    private static string Clock(DayPoint[] days, int index, Func<DayPoint, string?> pick)
        => index >= 0 && index < days.Length && pick(days[index]) is { Length: >= 16 } text ? text[11..16] : "--:--";
    // 右下角页脚：主来源与本次刷新时刻，用底部原有的 16px 空边，不向上挤占摘要卡与预报区。
    // 跨日缓存（日期轴对不上城市的今天）标注缓存时刻与「缓存」字样，不冒充刚更新。
    private void DrawFooter(WeatherSnapshot s)
    {
        bool stale = WeatherClock.LiveIndex(s) < 0;
        string time = s.FetchedAt.ToLocalTime().ToString(stale ? "MM/dd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
        string verb = stale ? Loc.T("缓存", "Cached") : Loc.T("更新", "Updated");
        string source = SourceName(s);
        footerNote.Text = source.Length > 0
            ? Loc.T($"{time} {verb} · {source}", $"{verb} {time} · {source}")
            : Loc.T($"{time} {verb}", $"{verb} {time}");
    }
    // 页脚主来源：优先每日域，其次实况域；完整分域来源由快照的 Sources 字典保存。
    private static string SourceName(WeatherSnapshot s)
        => s.Sources.TryGetValue("Daily", out var daily) && daily.Length > 0 ? daily
         : s.Sources.TryGetValue("Current", out var current) && current.Length > 0 ? current : "";
}
