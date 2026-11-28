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
    // 视图切换：点天气图标时，底部 days（逐时/逐日预报）与 indicesView（生活指数）互换。
    // 两者共用同一行；指数视图有数据时才值得切过去，因此这里只翻转状态，内容由 DrawIndices 负责。
    private void ToggleView()
    {
        if (_last is not { } snap) return;
        _indicesMode = !_indicesMode;
        if (_indicesMode) DrawIndices(snap);
        indicesView.Visibility = _indicesMode ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = !_indicesMode && _forecastLevel >= 1 ? Visibility.Visible : Visibility.Collapsed;
        SyncSummaryAlignment();
        UpdateMinuteVisibility();
        Fit(Width, Height, true);
    }

    // 生活指数视图：清空后重建卡片，只展示指数本身，无数据时给一行轻提示。
    // 高频项排前、冷门项在后；等级语义色见 IndexTone。
    private LifestyleSlot[] _drawnIndices = [];
    private void DrawIndices(WeatherSnapshot s)
    {
        var body = indicesBody;
        body.Children.Clear();
        _drawnIndices = [.. _app.Settings.Weather.LifestyleIndices];
        var available = s.Indices.DistinctBy(item => item.Type.Length > 0 ? item.Type : item.Name).ToArray();
        var byType = available.ToLookup(item => int.TryParse(item.Type, out int type) ? type : 0);
        var items = _drawnIndices.Where(slot => slot.Visible).SelectMany(slot => byType[(int)slot.Type])
            .Concat(available.Where(item => !int.TryParse(item.Type, out int type) || !Enum.IsDefined((LifestyleIndex)type))).ToArray();
        if (items.Length == 0)
            body.Children.Add(Note(available.Length == 0 ? Loc.T("暂无生活指数", "No lifestyle indices")
                : Loc.T("可在天气设置中开启生活指数", "Enable lifestyle indices in weather settings")));
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            body.Children.Add(grid);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                string name = item.Name.EndsWith("指数") && item.Name.Length > 2 ? item.Name[..^2] : item.Name;
                string value = item.Category.Length > 0 ? item.Category : item.Level;
                string icon = WeatherVisuals.LifestyleIcon(item.Type);
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                header.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                    Foreground = ColText, FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Center });
                var title = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = UI.Brush(_look.Primary), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1);
                header.Children.Add(title);
                var level = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = Ink(IndexTone(item)), TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(level, 2);
                header.Children.Add(level);

                var card = new StackPanel();
                card.Children.Add(header);
                if (item.Text.Length > 0)
                    card.Children.Add(new TextBlock { Text = item.Text, FontSize = 11.5, Foreground = ColText,
                        Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
                var tile = new Border { Child = card, Margin = new Thickness(2, 0, 2, 5), Padding = new Thickness(10, 8, 10, 8),
                    Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), CornerRadius = new CornerRadius(7) };
                if (i % 2 == 0)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(tile, i / 2);
                Grid.SetColumn(tile, i % 2);
                grid.Children.Add(tile);
            }
        }
    }

    // 指数等级文字色：绿=适合、黄=中性/过渡、红=不宜/极端、灰=纯建议或无等级。
    // 和风的 level 数字是语言无关判据，但方向按 type 而定：多数「数字越大越差」，
    // 穿衣(3) 反而是 5=舒适最好、两端寒冷/炎热差；空调(11)/太阳镜(12)/化妆(13) 纯建议无好坏，一律中性灰。
    private static Color IndexTone(IndexItem item)
    {
        int level = int.TryParse(item.Level, out var n) ? n : 0;
        bool neutral = item.Type is "11" or "12" or "13";
        bool bad, warn;
        if (neutral) { bad = warn = false; }
        else if (item.Type == "3") { bad = level is 1 or 2 or 7; warn = level is 3 or 6; }
        else if (level > 0)
        {
            bad = level >= 4 || item.Category.Contains("不宜") || item.Category.Contains("很差") || item.Category.Contains("极易") || item.Category.Contains("不舒适");
            warn = !bad && (level == 3 || item.Category.Contains("较") || item.Category.Contains("一般") || item.Category.Contains("中"));
        }
        else
        {
            bad = item.Category.Contains("不宜") || item.Category.Contains("很差") || item.Category.Contains("极易") || item.Category.Contains("不舒适");
            warn = !bad && (item.Category.Contains("较") || item.Category.Contains("一般") || item.Category.Contains("中"));
        }
        Color tone = bad ? Color.FromRgb(255, 148, 148) : warn ? Color.FromRgb(255, 202, 112) : Color.FromRgb(138, 216, 158);
        return neutral || (level == 0 && !bad && !warn) ? Color.FromRgb(152, 169, 187) : tone;
    }

    private TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = ColMuted, Margin = new Thickness(2, 0, 0, 6) };
    // 宽度自适应列数，按时间正序（过去→今天→未来）取窗口：最小 7 列=今天+未来6天，最大 24 列=过去10+今天+未来13，左右按同比例同步扩展。

    private static int[] Order(WeatherSnapshot s, int columns)
    {
        int len = s.Days.Length;
        int today = WeatherClock.LiveIndex(s);          // 按城市当前日期定位“今天”
        // 缓存跨日、城市的今天不在轴上时退回快照自带锚点选窗口：窗口只是决定取哪些天，
        // 「今天」标签与高亮由调用方按日期轴是否过期另行处理。
        if (today < 0) today = WeatherClock.SnapshotIndex(s);
        int maxPast = Math.Min(10, today), maxFuture = Math.Min(13, len - today - 1);
        columns = Math.Max(1, Math.Min(columns, len));
        int basic = Math.Min(7, 1 + maxFuture);  // 基础 7 列 = 今天 + 未来 6 天（不足时用全部）
        int extra = Math.Max(0, columns - basic);
        int leftMax = maxPast, rightMax = Math.Max(0, maxFuture - (basic - 1)); // 左右各自可扩展的上限
        int pool = Math.Min(extra, leftMax + rightMax);
        int left = 0, right = 0;
        for (int k = 0; k < pool; k++) { if (left < leftMax && (right >= rightMax || left <= right)) left++; else if (right < rightMax) right++; } // 左右交替同步扩展
        var order = new List<int>();
        for (int i = today - left; i < today; i++) order.Add(i);                                  // 过去最接近今天的 left 天
        for (int i = today; i < len && i <= today + basic - 1 + right; i++) order.Add(i);          // 今天 + 未来
        return order.ToArray();
    }
    private void DrawForecast(WeatherSnapshot s)
    {
        forecast.Children.Clear(); lowRow.Children.Clear(); lowNote.Children.Clear(); dayInfoRow.Children.Clear(); tempLabels.Clear(); conditionLabels.Clear(); conditionRows.Clear(); columnGlyphs.Clear(); lowerGlyphs.Clear(); dayInfoGrids.Clear();
        nowRule.Visibility = Visibility.Collapsed; nowText.Visibility = Visibility.Collapsed;   // 「现在」分界线只属于逐时
        // 天文镜头：预报列收起，弧线、色带与底部时刻行全部画进弹性的 chart 画布（由 DrawChart 统一绘制）。
        if (_lens == Lens.Celestial) { forecast.Visibility = Visibility.Collapsed; todayGlow.Visibility = Visibility.Collapsed; return; }
        forecast.Visibility = Visibility.Visible;
        // 锚点只依赖列宽，宽度变化时可以单独重贴，不必重建列。
        UpdateAnchors(s);
        // 列刚重建，行高与可见性回到默认（天气名称全显示）；立刻贴回当前档位，避免延迟重绘后闪回完整档。
        if (_hourlyMode) DrawHourlyForecast(s); else DrawDailyForecast(s);
        ApplyForecastLevel();
    }
    // 逐日列骨架：5 行固定高度（星期 / 日期 / 图标 / 天气名 / 温度），合计 98px。
    // 逐时列骨架：6 行（风向风级 / 降水概率 / 空气等级 / 图标 / 天气名 / 温度），合计 112px——
    // 降水概率与空气等级是逐时独有的小时级信息，只进逐时列、逐日不加行。
    // 两种模式行数因此不同，切换时上段高度差 14px，由弹性的折线行吸收（days 总高不变，只是折线带矮一截）；
    // 图标、天气名、温度三行在两套骨架里等高，切换时这三行像素级对齐。
    private static readonly double[] ColumnRowHeights = { 20, 14, 30, 14, 20 };
    private static readonly double[] HourlyRowHeights = { 20, 14, 14, 30, 14, 20 };
    // 天气名所在行：收纳档 3 收起的就是这一行；两套骨架行号不同，估算与贴回都必须按模式取。
    private const int DailyConditionRow = 3, HourlyConditionRow = 4;
    // 图标所在行：收纳档 1 连图标一起收起（只留数值与时刻），两套骨架都是第 3 行之前那一行。
    private const int DailyGlyphRow = 2, HourlyGlyphRow = 3;
    // 列内尺寸的唯一来源：逐时与逐日共用同一套骨架，就必须共用同一套字号与图标尺寸，
    // 否则切换模式时同样位置的图标/数字会大小跳变。任一处要改，只改这里。
    private const double ColGlyph = 28;    // 天气图标（行高 30，留 1px 上下呼吸）
    private const double ColNote = 10;     // 辅注小字：星期/日期、风级、降水概率、空气等级、天气名
    private const double ColValue = 15;    // 第 5 行主数值：逐日高温 / 逐时温度
    private const double ColFoot = 12;     // 底行：逐日低温 / 逐时时刻
    private static Grid Column(double[] heights)
    {
        var grid = new Grid();
        foreach (var h in heights) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        return grid;
    }
    private static void CellAt(Grid grid, UIElement child, int row)
    {
        Grid.SetRow(child, row); grid.Children.Add(child);
    }
    // 列第 3 行风级：快照没有逐时/逐日风级数组，统一由风速走蒲福换算；无风速返回空串，不留「–级」。
    private static string Bft(double speed)
        => !double.IsNaN(speed) && WeatherCodes.FromWindSpeed(speed) is { } bft && bft >= 0
            ? $"{bft}{Loc.T("级", "")}" : "";
    // 逐日昼夜：白天是「转前」，夜间是「转后」。折线两侧各画一个图标、各配自己的天气名，两个图标本身表达转折，文字不写「转」。
    private static int DayCodeAt(WeatherSnapshot s, int i)
        => i >= 0 && i < s.Days.Length && s.Days[i].Code is { } code && code >= 0 ? code : -1;
    private static int NightCodeAt(WeatherSnapshot s, int i)
        => i >= 0 && i < s.Days.Length && s.Days[i].NightCode is { } code && code >= 0 ? code : -1;
    // 上方画「转后」（夜间），夜间缺失或与白天相同就退回白天；下方固定画「转前」（白天）。同天气时两个图标一样也照画。
    private static int DayTopCode(WeatherSnapshot s, int i)
    {
        int night = NightCodeAt(s, i);
        return night >= 0 ? night : DayCodeAt(s, i);
    }
    // 逐时模式：列里放「风向风级 / 降水概率 / 空气等级 / 图标 / 天气名称 / 温度」，时刻放在最底部那一行，与折线共用同一个列几何。
    // 0 点那一列改标日期（如「11月20日」）。「现在」不占任何一列：它落在「当前整点」列内、当前与下一整点
    // 标签之间——竖线贯穿列区到底行、线顶写「现在」，当前这一格照常显示自己的时刻。
    private void DrawHourlyForecast(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns);
        for (int p = 0; p < n && p < s.Hours.Length; p++)
        {
            var hour = s.Hours[p];
            bool day = int.TryParse(hour.Time.Length >= 13 ? hour.Time[11..13] : "", out int hourOfDay) && hourOfDay is >= 6 and < 18;
            int code = hour.Code ?? -1;
            var column = Column(HourlyRowHeights);
            // 第 1 行：风向箭头 + 蒲福风级。气象学的风向是「来向」，箭头直接指向来向，所以北风朝上、东风朝右；
            // 逐日只能给一个聚合风向，小时级的风向变化正是逐时独有的信息。风速数值不进列（与逐日底部同款取舍：
            // 箭头+等级足够，km/h 属低频细节，逐时风速曲线已由风镜头承载）；箭头与等级都缺就留空。
            double deg = hour.WindDirDeg ?? double.NaN;
            string bft = hour.WindKmh is { } kmh ? Bft(kmh) : "";
            bool arrow = !double.IsNaN(deg);
            if (arrow || bft.Length > 0)
            {
                var mark = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (arrow) mark.Children.Add(WindMark(deg));
                if (bft.Length > 0) mark.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, Foreground = ColWind, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(arrow ? 2 : 0, 0, 0, 0) });
                CellAt(column, mark, 0);
            }
            // 第 2 行：降水概率。行高在骨架里常驻预留，空白并不省空间——有概率数据就显示（含 0%），
            // 「0%」本身是有效信息（该整点确实无降水）；概率缺失的来源退回降水量 ≥0.1mm 显示数值，两者都没有才留空。
            // 颜色与降水镜头的柱色同源，悬停 tooltip 给概率与降水量全文。
            int prob = hour.RainProb ?? -1;
            double pmm = hour.PrecipMm ?? double.NaN;
            if (prob >= 0 || pmm >= 0.1)
            {
                var rain = new TextBlock
                {
                    Text = prob >= 0 ? $"{prob}%" : $"{pmm:0.#}", FontSize = ColNote, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                    Foreground = Ink(Color.FromRgb(96, 168, 246))
                };
                var tip = new List<string>();
                if (prob >= 0) tip.Add(Loc.T("降水概率 ", "PoP ") + prob + "%");
                if (!double.IsNaN(pmm)) tip.Add(Loc.T("降水量 ", "Precip ") + pmm.ToString("0.#") + "mm");
                rain.ToolTip = string.Join(" · ", tip);
                CellAt(column, rain, 1);
            }
            // 第 3 行：空气质量等级。文字与颜色由该小时 AQI 按国标 HJ 633 换算（复用 AqiText/AqiInk，与空气卡同一套标准），
            // 列窄只写短名（去掉「污染」后缀：优/良/轻度/中度/重度/严重），悬停 tooltip 给全文与数值。
            // 逐时 AQI 仅和风提供，其他来源/城市该行留空——0 信息不占位。
            int aqi = hour.Aqi ?? -1;
            if (aqi >= 0)
            {
                var level = new TextBlock
                {
                    Text = AqiLevelShort(aqi), FontSize = ColNote, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                    Foreground = Ink(WeatherVisuals.AqiInk(aqi))
                };
                level.ToolTip = $"{Loc.T("空气质量", "Air quality")} {WeatherCodes.AqiText(aqi)} · AQI {aqi}";
                CellAt(column, level, 2);
            }
            if (code >= 0)
            {
                var hourGlyph = new WeatherGlyph(code, day) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                CellAt(column, hourGlyph, HourlyGlyphRow);
                columnGlyphs.Add(hourGlyph);
            }
            // 第 5 行：天气名称。61/63/65 这几档在图标上几乎长得一样，文字补的正是图标说不出的那一级；缺数据的格子连文字一起置空。
            var condition = new TextBlock { Text = code >= 0 ? HourCondition(code) : "", FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
            CellAt(column, condition, 4);
            conditionLabels.Add(condition); conditionRows.Add(column.RowDefinitions[HourlyConditionRow]);
            // 第 6 行固定显示当时的气温，不跟着镜头变——切镜头后那个指标已由上方折线/柱状图体现。
            // NoWrap：App.xaml 默认 Wrap，窄列里数值会折成两行。
            string hourValue = Degree(hour.Temperature);
            CellAt(column, new TextBlock
            {
                Text = hourValue, FontSize = hourValue.Length > 4 ? ColValue - 2 : ColValue,
                FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Width = 64, TextWrapping = TextWrapping.NoWrap
            }, 5);
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            // 时刻行：与列同宽同序，逐列对齐，统一压暗一档不与温度抢视线；「现在」由上方那条分界线表达，不占这一行。
            // 必须显式 NoWrap：App.xaml 里 TextBlock 全局默认 Wrap，列宽卡在 48px 上下时（7 列的小窗、24 列的大窗）
            // 「11月20日」比格子宽几个像素就会折成两行，把整行时刻撑高、连带上方的列一起错位。
            // 布局槽固定 64px：TextBlock 会把超出自身布局宽度的文字直接裁掉，而 24 列时格子只有约 48px、
            // 「11月20日」在 12px 下约 50px，按格子宽必然被截成「1月20」。槽放宽后按文字自身宽度居中，
            // 多出来的部分对称溢出到两侧空格里（邻列是居中的短时刻，两侧各留 8px 以上），看不见也不压字。
            lowRow.Children.Add(new TextBlock
            {
                Text = HourLabel(s, p), FontSize = ColFoot, TextAlignment = TextAlignment.Center,
                Width = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = ColMuted
            });
        }
    }
    // 当前时刻的「现在」位置（列左沿计，可带小数）：先按整点选列——最后一个不晚于当前整点的列
    // （22:40 归 22:00，不会标到还没到的 23:00），再把分钟在列内插值（01:20 → 列号 1 + 20/60）。
    // 时钟按城市时区算；标签中心的对齐（+0.5 列）由 PlaceNowRule 负责。
    private static double HourNow(WeatherSnapshot s, int n)
    {
        var now = WeatherClock.Now(s);
        string hourKey = now.ToString("yyyy-MM-ddTHH:00", CultureInfo.InvariantCulture);
        int best = 0;
        for (int p = 0; p < Math.Min(n, s.Hours.Length); p++)
        {
            string time = s.Hours[p].Time;
            if (time.Length < 16) continue;
            if (string.CompareOrdinal(time, hourKey) <= 0) best = p;   // 最后一个不晚于当前整点的列
            else break;
        }
        double fraction = (now.Minute + now.Second / 60.0) / 60.0;
        return Math.Min(best + fraction, n);   // 末列尾段封到列区右沿，不越界
    }
    // 底部时刻行文字：0 点标日期（如「11月20日」）/ 其余原样 HH:mm。直接从 ISO 串切，不依赖 DateTime.Parse 的区域设置。
    private static string HourLabel(WeatherSnapshot s, int p)
    {
        string time = p >= 0 && p < s.Hours.Length ? s.Hours[p].Time : "";
        if (time.Length < 13 || !int.TryParse(time[11..13], out int hour)) return "--";
        // 非 0 点照原样显示时刻（HH:mm，与逐时数组一致，不另造格式）；0 点改标日期。
        if (hour != 0) return time.Length >= 16 ? time[11..16] : $"{hour:00}:00";
        int month = int.TryParse(time[5..7], out int m) ? m : 0;
        int day = int.TryParse(time[8..10], out int d) ? d : 0;
        return $"{month}月{day}日";
    }
    // 逐时「现在」分界线：时刻标签画在列中心，整点即标签位置——竖线 = 当前整点标签 + 分钟占比 × 列宽
    // 分钟步进由 _nowTick 只重贴这一处，不重建列。

    private void PlaceNowRule(WeatherSnapshot s)
    {
        double col = forecast.Columns > 0 ? Math.Max(2, _chartWidth / forecast.Columns) : 0;
        double now = HourNow(s, Math.Max(1, forecast.Columns));
        double edge = 6 + Math.Min(now + 0.5, forecast.Columns) * col;   // +0.5：从列左沿换算到标签中心
        nowRule.Visibility = Visibility.Visible;
        nowRule.Width = 2;
        nowRule.Margin = new Thickness(Math.Max(0, edge - 1), 14, 0, 0);   // 顶部 14px 让给「现在」两个字
        nowRule.Background = NowRuleBrush;
        nowText.Visibility = Visibility.Visible;
        nowText.Text = Loc.T("现在", "Now");
        nowText.Foreground = NowTextBrush;
        nowText.Margin = new Thickness(Math.Max(0, edge - nowText.Width / 2), 0, 0, 0);
    }

    // 只依赖列宽的两块覆盖层：逐日的「今天」高亮、逐时的「现在」分界线。
    // 列内容与列宽无关，拖动改宽时只重贴这两块，不重建整列。
    private void UpdateAnchors(WeatherSnapshot s)
    {
        if (_lens == Lens.Celestial) { todayGlow.Visibility = nowRule.Visibility = nowText.Visibility = Visibility.Collapsed; return; }
        double col = forecast.Columns > 0 ? Math.Max(2, _chartWidth / forecast.Columns) : 0;
        if (_hourlyMode)
        {
            todayGlow.Visibility = Visibility.Collapsed;
            PlaceNowRule(s);
            return;
        }
        // 「今天」列高亮：跨五行覆盖上段+折线+低温+辅注+逐日信息行，与列内左右各 3px 留白对齐。找不到今天（跨日缓存）就收起。
        int today = WeatherClock.LiveIndex(s);
        var order = Order(s, forecast.Columns);
        int todayPos = -1;
        for (int p = 0; p < order.Length && todayPos < 0; p++) if (order[p] == today) todayPos = p;
        nowRule.Visibility = nowText.Visibility = Visibility.Collapsed;
        if (todayPos < 0) { todayGlow.Visibility = Visibility.Collapsed; return; }
        todayGlow.Visibility = Visibility.Visible;
        todayGlow.Width = Math.Max(2, col - 6);
        todayGlow.Margin = new Thickness(6 + todayPos * col + 3, 2, 0, 0);
        todayGlow.Background = TodayFill;
        todayGlow.BorderBrush = TodayEdge;
        todayGlow.BorderThickness = new Thickness(1);
    }

    private void DrawDailyForecast(WeatherSnapshot s)
    {
        int today = WeatherClock.LiveIndex(s);
        bool stale = today < 0;   // 跨日缓存：不把任何一天标成「今天/明天」，一律按星期+绝对日期渲染
        var order = Order(s, forecast.Columns);
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p]; var date = DateTime.Parse(s.Days[i].Date); var column = Column(ColumnRowHeights);
            string label = stale ? Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek])
                : i == today ? Loc.T("今天", "Today") : i == today + 1 ? Loc.T("明天", "Tomorrow") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]);
            bool isToday = i == today;
            CellAt(column, new TextBlock { Text = label, FontSize = ColValue, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Foreground = isToday ? UI.Brush(_look.Primary) : ColText }, 0);
            CellAt(column, new TextBlock { Text = date.ToString("MM/dd"), Foreground = ColMuted, FontSize = ColNote, TextAlignment = TextAlignment.Center }, 1);
            int before = DayCodeAt(s, i);
            int night = NightCodeAt(s, i);
            // 折线上方画「转后」（夜间），夜间缺失就退回白天；只有真变了才用夜间样式区分。
            int topCode = DayTopCode(s, i);
            bool changed = night >= 0 && night != before;
            var dayGlyph = new WeatherGlyph(topCode, !changed) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            CellAt(column, dayGlyph, DailyGlyphRow);
            columnGlyphs.Add(dayGlyph);
            var condition = new TextBlock { Text = HourCondition(topCode), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
            CellAt(column, condition, 3);
            conditionLabels.Add(condition); conditionRows.Add(column.RowDefinitions[3]);
            // 高温在预报列第 5 行、低温在下方独立低温行，折线夹在两者之间；两个温度都登记进 tempLabels 供低档位统一收起。
            var high = new TextBlock { Text = Degree(s.Days[i].HighC), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(high); CellAt(column, high, 4);
            // 今天列的选择态高亮由垫底的 todayGlow 贯穿整列承担，列自身保持透明。
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            var low = new TextBlock { Text = Degree(s.Days[i].LowC), Foreground = UI.Brush(_look.Primary), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(low); lowRow.Children.Add(low);
            // 转前天气名单独占辅注行（与上方天气名同字号同色），信息行只剩 28px 图标与风两行。
            var lowerName = new TextBlock { Text = HourCondition(before), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
            conditionLabels.Add(lowerName);
            lowNote.Children.Add(lowerName);
            var info = new Grid();
            info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ColGlyph, GridUnitType.Star) });
            info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayInfoCompact - ColGlyph, GridUnitType.Star) });
            var lowerGlyph = new WeatherGlyph(before, true) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center };
            lowerGlyph.VerticalAlignment = VerticalAlignment.Center;
            CellAt(info, lowerGlyph, 0);
            lowerGlyphs.Add(lowerGlyph);
            dayInfoGrids.Add(info);
            string dirText = s.Days[i].WindDir ?? "";
            double dir = s.Days[i].WindDirDeg ?? double.NaN;
            double wind = s.Days[i].WindMaxKmh ?? double.NaN;
            var windRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(dir))
            {
                var arrow = WeatherVisuals.WindArrowIcon(WeatherCodes.Wind8Angle(dirText, dir), Color.FromRgb(160, 205, 235));
                arrow.Width = 12; arrow.Height = 12; arrow.Margin = new Thickness(0, 0, 2, 0);
                windRow.Children.Add(arrow);
            }
            string bft = Bft(wind);
            if (bft.Length > 0) windRow.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ColWind });
            if (windRow.Children.Count > 0) CellAt(info, windRow, 1);
            if (!double.IsNaN(dir)) info.ToolTip = !string.IsNullOrWhiteSpace(dirText) ? dirText : WeatherCodes.DegreeToCompass(dir) + Loc.T("风", "");
            dayInfoRow.Children.Add(info);
        }
    }
    // 与预报同序的高低温折线：金线高温、蓝线低温，每个采样点带圆点与温度数字注释，随预报宽度横向伸展。

}
