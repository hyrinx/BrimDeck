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
    private readonly App _app;
    private State state;
    // 今日天气按内容高度贴顶、不拉伸；预报区占满余下高度贴底，两者首尾相接，中间不留空白。
    // MinHeight 贴近卡片两行的真实高度（约 86px），不再人为抬高到 112——省下的高度留给折线，
    // 避免下方信息行加高后 room 不足被降到「藏折线」档。折线判断用的就是这个值，必须与内容高度相符。
    private readonly RowDefinition summaryRow = new() { Height = GridLength.Auto, MinHeight = 86 };
    private readonly RowDefinition daysRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    // 页脚行占用原本的下边距 16px：刷新时刻与主来源靠右下对齐，既不挤占上方的读数卡与预报区，
    // 也不改变整页总高度（Fit 里从可用高度扣掉的仍是这 16px）。
    private readonly RowDefinition footerRow = new() { Height = new GridLength(16) };
    // 未来 2 小时降水窄条：有降水才展开，无数据整行归零，不占预报区的空间。
    private readonly RowDefinition minuteRow = new() { Height = new GridLength(0) };
    private readonly UniformGrid minuteBars = new()
    {
        Rows = 1, Columns = 24, Width = 252, Height = 22,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0)
    };
    private readonly TextBlock minuteTitle = new()
    {
        Text = Loc.T("未来 2 小时降雨", "Next 2h Precipitation"), FontSize = 9.5,
        Foreground = new SolidColorBrush(Color.FromRgb(174, 196, 220)), VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock minuteNote = new()
    {
        FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(206, 220, 238)),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Border minuteStrip;
    private bool _minuteHasRain;
    private readonly TextBlock footerNote = new()
    {
        FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(174, 196, 220)),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 2, 0)
    };
    private readonly Grid summary = new();
    private readonly UniformGrid stats = new() { Rows = 2, Columns = 2 };
    private readonly UniformGrid forecast = new() { Rows = 1, Columns = 7 };
    private readonly Grid days = new();
    // 底部区域第二套视图：生活指数。与 days 互斥占据同一行，点天气图标切换。
    private readonly ScrollViewer indicesView = new() { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
    private readonly StackPanel indicesBody = new();
    private bool _indicesMode;
    // 折线是预报区里唯一的弹性带：所在行是 Star、画布拉伸填充（MinHeight 保底），多余高度全部由折线吸收。
    // 高度变化被折线连续伸缩消化，而不是靠收起/展开内容行——骨架恒定，抖动在结构上无从产生。
    private readonly Canvas chart = new() { MinHeight = ChartMinHeight, Width = 588, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
    private bool _hourlyMode = true;    // 底部曲线视图：true=当日 24h 逐时（默认），false=每日高低温
    // 底部「看什么」：卡片点一下换成该指标的逐时曲线，再点一次回到气温。逐日只有气温，所以非气温镜头一律切到逐时。
    private WeatherChip[] _drawnChips = [];
    private Lens _lens = Lens.Temp;
    private readonly UniformGrid lowRow = new() { Rows = 1, Columns = 7, Height = LowRowHeight };
    private readonly UniformGrid lowNote = new() { Rows = 1, Columns = 7, Height = 0 };
    private readonly UniformGrid dayInfoRow = new() { Rows = 1, Columns = 7, Height = 60 };
    private readonly List<TextBlock> tempLabels = new();   // 高/低温文本，供“隐藏高低温”档统一收起
    // 天气名称文本与其所在列的第 4 行：收纳到档 3 时上下两处名称同时收起（行高同步归零），折线保留。
    private readonly List<TextBlock> conditionLabels = new();
    private readonly List<RowDefinition> conditionRows = new();
    // 预报列里的天气图标：档 1 连同图标行一起收起（只留数值与时刻），行高归零前先把图标藏掉，
    // 否则 0 高的行不会裁切子元素，图标会溢到相邻行上。
    private readonly List<UIElement> columnGlyphs = new();
    private readonly List<UIElement> lowerGlyphs = new();
    private readonly List<Grid> dayInfoGrids = new();
    // 当前预报区收纳状态：4 完整 / 3 藏天气文字 / 2 藏折线 / 1 藏图标 / 0 整行折叠。
    // 五档各有独立高度：档 1 藏图标但保留温度与时刻，所以矮面板不会从「有图标」直接跌到「整行消失」。
    // 每一档需要的高度是骨架常量之和（StateHeight），决策是 room→状态 的纯函数；
    // 列每次重绘都会重建行定义，状态必须独立保存，重建后原样贴回，否则天气名称会闪回。
    private int _forecastLevel = 4;
    // 「今天」列选择态高亮：垫在 days 底部、跨四行（预报列上段/折线/低温/逐日信息行），从列顶贯穿到最底部，折线与下方各行都被它包住。
    // 显式左对齐 + 左边距定位，与预报网格列几何完全一致，避免 Stretch+Width 时被居中导致的横向偏移。
    private readonly Border todayGlow = new() { CornerRadius = new CornerRadius(15), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
    // 逐时的「现在」不占任何一列：一条竖线贯穿列区到底行、线顶标「现在」，按真实时刻插在
    // 当前整点列内（01:20 落在 01:00 列的 1/3 处），时刻标签照常显示各自的整点。
    private readonly Border nowRule = new() { Width = 2, CornerRadius = new CornerRadius(1), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly TextBlock nowText = new() { Text = "", Width = 40, FontSize = 9.5, TextAlignment = TextAlignment.Center, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    internal static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(15);
    private readonly DispatcherTimer timer = new() { Interval = AutoInterval };
    private WeatherSnapshot? _last;
    // 上一次成功绘制的内容签名（不含 FetchedAt）与失败标记：签名一致且没画全过时只更新页脚。
    // 签名为 FNV-1a 64 位哈希：整份快照先序列化成 UTF-8 字节再滚动哈希，不再产生几十 KB 的字符串。
    private ulong _renderSignature;
    private bool _renderFailed;
    private string _shownKey = "";   // 当前画面显示的是哪座城市（缓存键）：显示数据与城市绑定，换城不残留
    // 排队中的部分结果：历史单独一轮时 partial 与终版只隔几十毫秒，合并到一次渲染。
    private Action? _pendingPartial;
    private bool _partialQueued;
    // 重画节流：拖动改宽度时 Fit 每帧都会被调用，而重建 24 列的列元素与折线并不便宜。
    // 画布宽度与列数当场更新，真正重建最多 30fps，落后的一帧由 _flush 补上。
    private long _drawnAt;
    private bool _redrawPending;
    private DispatcherTimer? _flush;
    // 折线画布最近一次真正绘制时的宽度与「拖宽结束」计时器：宽度变化先用缩放顶住，
    // 停手 120ms 后再按最终宽度重建一次，把拉伸的文字恢复成正常比例。
    private double _chartDrawnWidth;
    private DispatcherTimer? _chartSettle;
    // 主题配色只有几种，画刷冻结后共享：一次重画会碰上百个元素，逐个 new 只会白白制造垃圾。
    private PanelLook _look;
    private Brush ColText => UI.Brush(_look.Secondary);
    private Brush ColMuted => UI.Brush(_look.Tertiary);
    private Brush ColCondition => UI.Brush(_look.Primary);
    private Brush ColWind => Ink(Color.FromRgb(160,205,235));
    private Brush ChartTemp => Ink(Color.FromRgb(102,194,255));
    private Brush Ink(Color color) => new SolidColorBrush(_look.Readable(color, PanelThemes.TextContrast));
    internal void ApplyWeatherTheme(PanelLook look)
    {
        _look = look;
        Foreground = UI.Brush(_look.Primary);
        minuteTitle.Foreground = footerNote.Foreground = ColMuted;
        minuteNote.Foreground = ColText;
        _renderSignature = 0;
        if (_last is not { } snap) return;
        Render(snap);
    }
    private static readonly Brush NowRuleBrush = Frozen(150, 143, 208, 255);
    private Brush NowTextBrush => Ink(Color.FromRgb(143, 208, 255));
    private static readonly Brush TodayFill = Frozen(26, 255, 255, 255);
    private static readonly Brush TodayEdge = Frozen(56, 255, 255, 255);
    private static Brush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static Brush Frozen(byte a, byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
    private double _lastAvail = -1;       // 上次参与状态决策的可用高度，高度没变就跳过整段决策
    private string _autoCityName = "";   // 最近一次自动定位到的城市
    private int _columns = 7;
    private double _chartWidth;
    private SummaryPlan? _summaryPlan;
    private int generation;
    private int _visibilityEpoch;   // 隐藏/卸载会让等待中的加载续体作废，避免它回头启动计时器
    // 视口即内容区，宽度与高度随 Fit 传入值整体填充。预报区骨架恒定（各行行高固定、图标恒定 28px），
    // 折线行是唯一弹性带；高度不足按 4→3→2→1→0 逐级收纳：4 完整 → 3 藏上下天气名称 → 2 藏折线 → 1 藏图标 → 0 整行折叠。
    internal void Fit(double width, double height, bool force = false)
    {
        Width = width; Height = height;
        UpdateMinuteVisibility();
        // 顶部读数卡按可用宽度取列数（2–6）、按竖向余量取行数（2–3）；只有跨档才重建。
        // 列宽本身由 Star 列弹性分配，连续拖宽时不需要重建：卡片跟着变宽变窄即可。
        if (_indicesMode && _last is { } indicesSnap && !_drawnIndices.SequenceEqual(_app.Settings.Weather.LifestyleIndices))
            DrawIndices(indicesSnap);
        double avail = Math.Max(0, height - 16 - minuteRow.Height.Value);   // 根 Grid 上边距 0 + 底部页脚行 16 + 分钟窄条
        summaryRow.MinHeight = Math.Min(summaryMinHeight, avail);     // 今日天气最小高度封顶，避免自身被压坏
        double room = Math.Max(0, avail - summaryMinHeight);
        int previousLevel = _forecastLevel;
        while (_forecastLevel > 0 && StateHeight(_forecastLevel) > room) _forecastLevel--;
        while (_forecastLevel < 4 && StateHeight(_forecastLevel + 1) + UpgradeMargin <= room) _forecastLevel++;
        bool compact = CompactForecast;
        double upperHeight = avail - SkeletonHeight(_forecastLevel, _hourlyMode);
        int rows = compact && upperHeight >= 3 * SummaryTileRow ? 3 : 2;
        var summaryPlan = PlanSummary(width, rows);
        bool summaryChanged = summaryPlan != _summaryPlan || compact != _compactSummary
            || !_drawnChips.SequenceEqual(_app.Settings.Weather.Chips);
        _compactSummary = compact;
        _summaryPlan = summaryPlan;
        if (summaryChanged && _last is { } summarySnap) DrawSummary(summarySnap, summaryPlan);
        double chartWidth = Math.Max(30, width - 40);
        int columns = LayoutColumns(width);
        // 24h 模式固定 24 列对应 24 个逐时数据点，保证「24 个日期 ↔ 24 个时刻」一一对应。
        // 逐时与逐日共用同一套列数阶梯：宽度压缩到 400 以下就只留 7 列。
        int shownValue = LayoutShown(columns, _last);
        bool columnsChanged = columns != _columns, shownChanged = shownValue != forecast.Columns;
        bool heightChanged = Math.Abs(avail - _lastAvail) > 0.5;
        // 列数或实际列数变了必须重画：改 Columns 而不重建子元素会让 UniformGrid 换行，旧列残影落在左侧。
        bool layoutChanged = force || columnsChanged || shownChanged;
        bool widthChanged = Math.Abs(chartWidth - _chartWidth) > 0.5;
        _columns = columns; _chartWidth = chartWidth; forecast.Columns = shownValue; lowRow.Columns = shownValue; lowNote.Columns = shownValue; dayInfoRow.Columns = shownValue; chart.Width = chartWidth;
        if (_last is { } f)
        {
            // 列数跨档：列元素与折线一起重建。跨档次数少，直接重画比节流更稳。
            if (layoutChanged) { _drawnAt = Environment.TickCount64; _redrawPending = false; DrawForecast(f); DrawChart(f); }
            // 只有宽度变了：列内容与列宽无关，不重建列，只把锚点按新列宽重贴；
            // 折线先横向缩放顶住，停手 120ms 后按最终宽度重建一次，文字恢复原始比例。
            else if (widthChanged) { UpdateAnchors(f); ScaleChart(); }
        }
        // 列数、列内数据量、可用高度、摘要行数都没变，状态就不可能变：跳过整段决策。
        if (!(force || columnsChanged || shownChanged || heightChanged || summaryChanged || previousLevel != _forecastLevel)) return;
        _lastAvail = avail;
        ApplyForecastLevel();
        SyncSummaryAlignment();
    }
    private bool _compactSummary;
    private bool CompactForecast => !_indicesMode && _forecastLevel < 3;

    private void SyncSummaryAlignment()
    {
        bool visible = _indicesMode || days.Visibility == Visibility.Visible;
        bool expand = CompactForecast || !visible;
        summaryRow.Height = expand ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        daysRow.Height = !visible ? new GridLength(0) : expand ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        summary.VerticalAlignment = expand ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }
    // 把当前状态原样贴到列元素上。列在每次重绘时都会 Clear + 新建行定义，行高与可见性回到默认值；
    // 重绘后必须重贴，否则天气名称/折线会闪回，直到下一次 Fit 才收起——拖动高度时看到的就是这个闪动。
    // 图标尺寸恒定 28px 不随档位缩放：骨架只有「天气名行」「图标行」两个开关，其余全部交给折线行伸缩。
    private void ApplyForecastLevel()
    {
        int level = _forecastLevel;
        bool showCondition = level >= 4;
        bool showGlyph = level >= 2;     // 档 1：图标整行收起，只留风/概率/空气/温度与时刻
        // 天气名行高按模式取：逐时列的名称行与逐日不是同一行号。
        var rows = _hourlyMode ? HourlyRowHeights : ColumnRowHeights;
        int conditionIndex = _hourlyMode ? HourlyConditionRow : DailyConditionRow;
        int glyphIndex = _hourlyMode ? HourlyGlyphRow : DailyGlyphRow;
        double conditionHeight = showCondition ? rows[conditionIndex] : 0;
        double glyphHeight = showGlyph ? rows[glyphIndex] : 0;
        foreach (var row in conditionRows) row.Height = new GridLength(conditionHeight);
        foreach (var label in conditionLabels) label.Visibility = showCondition ? Visibility.Visible : Visibility.Collapsed;
        foreach (var glyph in columnGlyphs) glyph.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;

        double chartHeight = ChartHeightFor(level);
        var chartVisibility = chartHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        chart.MinHeight = chartHeight;
        bool chartToggled = chart.Visibility != chartVisibility;
        chart.Visibility = chartVisibility;
        // 折线行是唯一的弹性带。折线收起后不再把每一行都拉成 Star——那会把图标、温度、时刻均匀撑开，
        // 行距全乱、越拉越丑；改为整块保持骨架原高、贴住底部下沉，多出来的高度由它上方那片区域吸收。
        bool pinned = CompactForecast;
        days.VerticalAlignment = pinned ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        var flexible = new GridLength(1, GridUnitType.Star);
        days.RowDefinitions[0].Height = GridLength.Auto;
        days.RowDefinitions[1].Height = chartHeight == 0 || pinned ? new GridLength(chartHeight) : flexible;
        foreach (var border in forecast.Children.OfType<Border>())
        {
            if (border.Child is not Grid column) continue;
            for (int i = 0; i < column.RowDefinitions.Count; i++)
            {
                double height = i == conditionIndex ? conditionHeight : i == glyphIndex ? glyphHeight : rows[i];
                column.RowDefinitions[i].Height = new GridLength(height);
            }
        }
        bool showTemp = level >= 1 && _lens != Lens.Celestial;   // 天文镜头有自己的底部时刻行，低温/时刻行让位
        // 底部三行各管各的显隐与定高，总高与拆开前完全一致（20 + 14 + 44 = 20 + 58）：
        //   lowRow 逐日低温 / 逐时时刻，档 1 起常驻；
        //   lowNote 逐日转前天气名，只在档 4 出现（与上方天气名同进退），逐时恒为 0；
        //   dayInfoRow 转前图标 + 风，逐日档 1 起常驻：档 1 收图标缩成风向带，风向保留到档 0。
        lowRow.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        bool showNote = showTemp && !_hourlyMode && showCondition;
        lowNote.Visibility = showNote ? Visibility.Visible : Visibility.Collapsed;
        bool showInfo = showTemp && !_hourlyMode && level >= 1;
        dayInfoRow.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        // 档 1 收起转前图标：信息行收缩为风向带并贴底，风向不占用图标空出来的高度。
        foreach (var glyph in lowerGlyphs) glyph.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;
        foreach (var grid in dayInfoGrids) grid.RowDefinitions[0].Height = new GridLength(showGlyph ? ColGlyph : 0, GridUnitType.Star);
        lowRow.Height = LowRowHeight;
        lowNote.Height = showNote ? DayNoteHeight : 0;
        dayInfoRow.Height = showGlyph ? DayInfoCompact : DayInfoCompact - ColGlyph;
        days.RowDefinitions[2].Height = GridLength.Auto;
        days.RowDefinitions[3].Height = GridLength.Auto;
        days.RowDefinitions[4].Height = GridLength.Auto;
        foreach (var p in tempLabels) p.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = !_indicesMode && level >= 1 ? Visibility.Visible : Visibility.Collapsed;
        // 折线行重新出现时行高从 0 展开，画布还没按新高度画过：立刻补一版，布局后 SizeChanged 会再校准一次。
        if (chartToggled && chartVisibility == Visibility.Visible && _last is { } snap) DrawChart(snap);
    }
    // 各收纳状态需要的高度：骨架行高全是常量（ColumnRowHeights 与各行固定高），状态高度因此也是常量。
    // 估算里加了少量安全余量：宁可早一档收起，也不让内容溢出弹性行——决策不需要任何实测。
    // 档位语义：4 完整 / 3 藏天气文字 / 2 藏折线 / 1 藏图标（留温度、时刻与风向）/ 0 整行折叠。
    // 逐日比逐时多一条「转前图标 + 风」镜像行，逐时比逐日多两行小时级数据（降水概率、空气等级）：
    // 两套骨架各自估算后取更高的那套，同一尺寸下两种模式的档位一致，切换不会跳档。
    // 镜像行在档 1 起常驻（折线收起不影响它）：档 1 收图标缩成风向带贴底，档 0 才整行下线。
    private double ChartHeightFor(int level) => _lens switch
    {
        Lens.Celestial when level >= 3 => CelestialArcHeight,
        Lens.Celestial when level >= 1 => CelestialMinHeight,
        _ when level >= 3 => ChartMinHeight,
        _ => 0
    };

    private double StateHeight(int level) => Math.Max(SkeletonHeight(level, true), SkeletonHeight(level, false));
    private double SkeletonHeight(int level, bool hourly)
    {
        if (level <= 0) return 0;
        double chartHeight = ChartHeightFor(level);
        double chartSpace = chartHeight > 0 ? chartHeight + ChartTopMargin + BandSafety : 0;
        if (_lens == Lens.Celestial) return chartSpace;
        var rows = hourly ? HourlyRowHeights : ColumnRowHeights;
        int condRow = hourly ? HourlyConditionRow : DailyConditionRow;
        int glyphRow = hourly ? HourlyGlyphRow : DailyGlyphRow;
        double h = ColumnInset + BandSafety;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == condRow && level < 4) continue;
            if (i == glyphRow && level < 2) continue;
            h += rows[i];
        }
        h += chartSpace;
        if (level >= 1) h += LowRowHeight + BandSafety;                     // 低温/时刻行：档 1 起常驻（档 1 只收起图标）
        if (!hourly && level >= 4) h += DayNoteHeight + BandSafety;         // 转前天气名行：档 4 才有
        if (!hourly && level >= 2) h += DayInfoCompact + BandSafety;            // 转前图标 + 风：档 2 起完整（图标 + 风向带）
        if (!hourly && level == 1) h += DayInfoCompact - ColGlyph + BandSafety; // 档 1 只留风向带，行高随图标收起收缩
        return h;
    }
    // 状态决策：room 的纯函数，双向即时、阈值不对称。降档在 room < 档高 时立即执行
    // 预报状态、无回路可振荡，去抖只剩「拖动期间每帧重启定时器、必须停手才回升」的副作用，故弃用。
    // 节流窗口里被跳过的那一次重画：33ms 后补上。拖到一半停手、或拖动在节流窗口内结束，都不会留下半截折线。
    private void FlushLater()
    {
        if (_flush is not null) return;
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _flush = timer;
        timer.Tick += (_, _) =>
        {
            if (_redrawPending)
            {
                _redrawPending = false; _drawnAt = Environment.TickCount64;
                // 这里只可能是折线随高重画（宽度变化已改走缩放 + 停手重建）：列元素与高度无关，不重建。
                if (_last is { } s) DrawChart(s);
            }
            if (_redrawPending) return;
            _flush = null; timer.Stop();
        };
        timer.Start();
    }
    // 弹性折线的随高重画：画布高度被拉伸改变（拖高度、升降收纳档）时按新几何重画，33ms 节流与整页重画同一档。
    private void ScheduleChartRedraw()
    {
        if (_last is null) return;
        long now = Environment.TickCount64;
        if (now - _drawnAt >= 33) { _drawnAt = now; DrawChart(_last); }
        else { _redrawPending = true; FlushLater(); }
    }
    // 宽度变化时的过渡：列内容不重建，折线先横向缩放顶住（文字随之轻微拉伸），
    // 停手 120ms 后按最终宽度真正重画一次，恢复原始字宽。
    private void ScaleChart()
    {
        double target = Math.Max(30, chart.Width);
        double baseWidth = _chartDrawnWidth > 1 ? _chartDrawnWidth : target;
        chart.RenderTransform = Math.Abs(target - baseWidth) < 0.5 ? null : new ScaleTransform(target / baseWidth, 1);
        SettleChart();
    }
    private void SettleChart()
    {
        if (_chartSettle is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _chartSettle = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop(); _chartSettle = null;
                if (_last is { } s) { _drawnAt = Environment.TickCount64; DrawChart(s); }
            };
        }
        _chartSettle.Stop(); _chartSettle.Start();
    }
    private const double summaryMinHeight = 86;   // 今日天气卡片两行的真实高度，与 summaryRow 初始值一致，让 room 估算准确
    // —— 摘要区骨架：指标卡列宽在 84–120 之间弹性浮动，列数 2–6 随可用宽度取整。
    // 理想列距 102（卡片内容约 96 + 左右外边距 6）。指标区占 Star 列，宽度变化直接由卡片伸缩吸收，
    // 不重建、不留右侧固定空档——旧版「列数 × 105 固定宽」正是宽窗口右侧一大块空白与窄窗口挤扁 Hero 的来源。
    // 每个卡位包含卡片左右各 3 DIP 的外边距。
    private const double TilePitch = 124;
    private const double SummaryTileRow = 44;     // 一排读数卡的高度（卡约 41 + 上下外边距 6）：行数升档的余量门槛
    private const double ChartMinHeight = 46;     // 折线画布最小高度：上下各留标签与圆点的空间，低于此折线档装不下
    private const double CelestialMinHeight = 96; // 天文镜头的最低需求：整块只画画布本身（弧线带 + 地平线 + 时刻刻度）
    // 天文镜头画完弧线、大天体与事件标注所需的最小高度：弧线区至少 52px（容得下大天体），
    // 再加地平线以下的标注、色带与刻度。低于它才切矮版（只留地平线以下），画布高约 160、面板高约 300。
    private const double CelestialArcHeight = 160;
    // —— 骨架尺寸的唯一事实来源：ApplyForecastLevel 贴行高、StateHeight 估状态高度，两边引用同一组常量。
    // 估算 = 真实开销 + BandSafety。要改任何一行尺寸只改这里（及列 Border 的 Padding），决策与呈现永远不会漂移。
    private const double LowRowHeight = 20;     // 低温/时刻行（低温与高温同字号 15px，行高相应加大）
    private const double DayInfoFull = 58;      // 逐日信息行·完整（天气名 14 + 28px 图标 + 风 16），拆行后 = DayNoteHeight + DayInfoCompact
    private const double DayInfoCompact = 44;   // 逐日信息行·紧凑（图标 + 风）
    private const double DayNoteHeight = DayInfoFull - DayInfoCompact;   // 辅注行：转前天气名（10px 字，14px 行高）
    private const double ChartTopMargin = 2;    // 折线画布上边距
    private const double ColumnInset = 6;       // 预报列上段真实开销：forecast 上边距 2 + 列 Border 上下 Padding 4
    private const double BandSafety = 2;        // 每条横带的安全余量：宁可早一档收起，也不让内容溢出弹性行
    private const double UpgradeMargin = 8;     // 升档死区：升档要求比该档自身需求再多 8px 富余，与降档阈值隔开，吸收手抖与 4px 量化噪声
    // 折线画布的当前实际高度：由弹性行拉伸决定；尚未完成首帧布局时退回最小高度。
    private double ChartBand() { double h = chart.ActualHeight; return !double.IsNaN(h) && h >= 20 ? h : ChartMinHeight; }
    private readonly Grid host = new();
    private bool fetching;
    private (int Index, City? City, string Source) Selection => (state.Selected, AutoSelected ? null : state.Cities[state.Selected], state.Schedule.SourceFor(AutoSelected ? null : state.Cities[state.Selected]));
    private (int Index, City? City, string Source) _activeSelection;
    private CancellationTokenSource? _token;
    private DateTimeOffset _cycleStart = DateTime.UtcNow;
    internal bool IsFetching => fetching;
    internal DateTimeOffset CycleStart => _cycleStart;
    internal event Action? StateChanged;
    private void RaiseState() => StateChanged?.Invoke();

    public WeatherView(App app, PanelLook look)
    {
        _app = app;
        _look = look;
        Width = 620; Height = 290;
        Background = Brushes.Transparent; ApplyWeatherTheme(look);
        // 内容整块填满视口：UserControl 默认按内容自然尺寸贴左上，预报区取 Star 行时整块内容只剩自然高度，
        // 上下会被留出等量空白（页头下沿到摘要之间那一大条黑带就是这么来的）。显式拉伸后视图上沿紧贴页头。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        state = WeatherData.Load(() => _app.Settings);
        // Grid 两行：今日天气(Auto 按内容贴顶) / 底部预报(Star 填满余下高度)，宽度与高度都随 Fit 传入值整体填充。
        // 今日天气内容不多，按内容高度就够；多出来的高度全部给下方预报区，让它上沿紧接天气、下沿贴住页脚，
        // 中间不再留一大块空白。底部 16px 由页脚行占住（刷新时刻与来源），与用量页列表底部基准一致。
        // 上边距 0：页头那一行（36）本身就是内容的上沿，用量页也是把内容直接贴在页头下沿的
        // （UsageContent.Margin 顶边为 0，列内 ColumnTopInset = 2）。这里再留 6 会和用量页形成两套基准。
        var minutePanel = new DockPanel { LastChildFill = true };
        minutePanel.Children.Add(minuteTitle);
        minutePanel.Children.Add(minuteBars);
        minutePanel.Children.Add(minuteNote);
        minuteStrip = new Border
        {
            Child = minutePanel, Margin = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(8, 2, 8, 2), Visibility = Visibility.Collapsed
        };
        var root = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        root.RowDefinitions.Add(summaryRow);
        root.RowDefinitions.Add(minuteRow);
        root.RowDefinitions.Add(daysRow);
        root.RowDefinitions.Add(footerRow);
        Grid.SetRow(summary, 0); Grid.SetRow(minuteStrip, 1); Grid.SetRow(days, 2); Grid.SetRow(footerNote, 3);
        root.Children.Add(summary); root.Children.Add(minuteStrip); root.Children.Add(days); root.Children.Add(footerNote);
        // 生活指数视图与 days 占同一行、同一层；默认折叠，点天气图标时与 days 互换可见性。
        Grid.SetRow(indicesView, 2);
        indicesView.Content = indicesBody;
        indicesView.SetResourceReference(Control.TemplateProperty, "WeatherIndicesScrollTemplate");
        indicesView.Margin = new Thickness(0, 0, -root.Margin.Right, 0);
        indicesView.Padding = new Thickness(6, 2, 1, 2);
        root.Children.Add(indicesView);
        host.Children.Add(root); Content = host;
        // days 内部四行上下排列：预报列上段(星期/日期/图标/天气名称/高温)贴顶、折线图占据中间弹性行、
        // 折线下方再分三行独立排布（主值 低温/时刻、辅注 转前天气名/时刻说明、信息 转前图标+风），
        // 与折线上方镜像。折线行吸收预报区多出来的高度，让底部各行紧贴窗口下沿。
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 当前位置高亮垫底：跨五行(上段/折线/低温/辅注/信息行)，Forecast、折线与底部各行都画在它上面。
        // 逐日高亮「今天」那一列，逐时高亮「现在」那一列——同一块 Border、同一套定位公式，两种模式共用。
        Grid.SetRow(todayGlow, 0); Grid.SetRowSpan(todayGlow, 5); days.Children.Add(todayGlow);
        // 「现在」分界线垫在同一层：竖线在两格之间、文字在列区上方那两行空位里，都不参与命中测试。
        Grid.SetRow(nowRule, 0); Grid.SetRowSpan(nowRule, 5); days.Children.Add(nowRule);
        forecast.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(forecast, 0); days.Children.Add(forecast);
        // 折线画布与预报网格左边距同为 6px 显式左对齐，画布原点与列起点完全重合，点坐标按列中心计算。
        chart.Margin = new Thickness(6, 2, 6, 0); chart.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(chart, 1); days.Children.Add(chart);
        // 弹性折线：画布被拉伸改变高度时（拖高度、升降收纳档）按新高度重画，节流 33ms 与整页重画同一档。
        // 宽度变化不再走这里：宽度只做横向缩放，等拖宽停手后由 _chartSettle 统一重建。
        chart.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) ScheduleChartRedraw(); };
        // 整块底部（列 + 折线 + 低温行）就是一个切换区：鼠标移上去变手型，点一下在「逐时」与「逐日」之间互换。
        // 不设开关按钮——底部本身就是开关，和卡片镜头互不干扰（卡片改看什么，底部改看多久）。
        // 键盘用户同样可达：可聚焦，Enter/空格与鼠标点按共用同一入口。
        days.Cursor = System.Windows.Input.Cursors.Hand;
        days.Background = Brushes.Transparent;                       // 透明也要有画刷，否则空白处收不到命中测试
        void ToggleMode()
        {
            if (_last is not { } snap || snap.Hours.Length == 0) return;
            _hourlyMode = !_hourlyMode;
            _lens = Lens.Temp;                                        // 切回逐日时没有非气温镜头，顺手复位
            Fit(Width, Height, true); DrawSummary(snap);
        }
        days.MouseLeftButtonUp += (_, e) => { ToggleMode(); e.Handled = true; };
        days.Focusable = true;
        days.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { ToggleMode(); e.Handled = true; } };
        AutomationProperties.SetName(days, Loc.T("切换逐时/逐日预报", "Toggle hourly or daily forecast"));
        lowRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(lowRow, 2); days.Children.Add(lowRow);
        lowNote.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(lowNote, 3); days.Children.Add(lowNote);
        dayInfoRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(dayInfoRow, 4); days.Children.Add(dayInfoRow);
        Grid.SetRow(nowText, 0); Grid.SetRowSpan(nowText, 2); days.Children.Add(nowText);
        // 首帧与切页都走同一个入口：有请求在飞时直接返回，数据还在自动周期内就跳过，
        // 不再让 Loaded 与 IsVisibleChanged 各发一次、后者把前者取消。
        _timeBoundary.Tick += (_, _) => { _timeBoundary.Stop(); if (IsVisible && _last is { } snap) Render(snap); };
        // 「现在」分界线按分钟走：只重贴线与标签，不重建列；隐藏/卸载即停。
        _nowTick.Tick += (_, _) => { if (IsVisible && _hourlyMode && _lens != Lens.Celestial && _last is { } snap) PlaceNowRule(snap); };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { Pause(); return; }
            if (!IsLoaded) return;
            int epoch = _visibilityEpoch;
            state = WeatherData.Load(() => _app.Settings);
            await ResumeAsync(epoch);
        };
        Loaded += async (_, _) => { if (IsVisible) await ResumeAsync(_visibilityEpoch); };
        // 15 分钟周期是硬触发；正在手动刷新时让它跑完，不打断。
        timer.Tick += async (_, _) => { if (IsVisible && !fetching) await Refresh(); };
        Unloaded += (_, _) => Pause();
    }

    private async Task ResumeAsync(int epoch)
    {
        await EnsureFreshAsync();
        if (epoch != _visibilityEpoch || !IsLoaded || !IsVisible) return;
        if (_last is { } snap) Render(snap);
        timer.Start();
        _nowTick.Start();
    }

    private void Pause()
    {
        timer.Stop(); _timeBoundary.Stop(); _nowTick.Stop();
        _flush?.Stop(); _flush = null; _redrawPending = false;
        _chartSettle?.Stop(); _chartSettle = null;
        _pendingPartial = null;
        if (_warningPopup is { } popup) popup.IsOpen = false;
        Cancel();
        generation++;
        _visibilityEpoch++;
    }

    private async Task EnsureFreshAsync()
    {
        if (fetching && _token?.IsCancellationRequested == false && _activeSelection == Selection) return;
        var key = AutoSelected ? state.AutoKey : state.KeyFor(state.Cities[state.Selected]);
        if (!fetching && _shownKey == key && _last is { } snapshot && DateTimeOffset.Now - snapshot.FetchedAt < AutoInterval) return;
        await Refresh();
    }

    internal Task ManualRefresh()
    {
        if (fetching) { Cancel(); return Task.CompletedTask; }
        return Refresh(force: true);
    }
    internal void Cancel() => _token?.Cancel();
    // force：手动刷新要真的发请求。自动轮询照常吃缓存（实况 10 分钟、逐时 30 分钟、逐日 2 小时各自过期），
    // 否则点下去只是在 TTL 内再把同一份缓存画一遍，看起来就是「刷新没反应」。
    private async Task Refresh(bool force = false)
    {
        _token?.Cancel();
        var cts = new CancellationTokenSource();
        _token = cts;
        var token = cts.Token;
        int request = ++generation;
        var selection = _activeSelection = Selection;
        var selected = selection.Index;
        bool StillSelected() => request == generation && selection == Selection && !token.IsCancellationRequested;
        // 缓存键一律是实际城市键：手动选城市用所选城市的键，自动定位用 AutoKey 指向的键；
        // "auto" 只在 AutoKey 里做映射，不是数据键。AutoKey 为空（首启动）时跳过缓存预渲染。
        string key = selected >= 0 && selected < state.Cities.Count ? state.KeyFor(state.Cities[selected]) : state.AutoKey;
        // 显示数据与城市绑定：换到没有缓存的城市就清掉上一城的残留画面，刷新失败或取消时也不会把
        // 别的城市的数据挂在新城市标签下；有该城市自己的缓存则直接渲染缓存。
        ShowKey(key);
        fetching = true; RaiseState();
        try {
            using var fresh = force ? WeatherCache.Fresh() : null;
            City city;
            if (selected >= 0 && selected < state.Cities.Count) city = state.Cities[selected];
            else
            {
                var located = await WeatherData.LocateAsync(state.Schedule, token);
                token.ThrowIfCancellationRequested();
                if (!StillSelected()) return;
                if (located is null)
                {
                    if (_last is null) ShowEmpty();
                    return;
                }
                city = located;
                WeatherLive.Located(city);
            }
            // 旧请求交给 token 中断；generation 只在部分结果回写时挡一次，避免迟到的首屏覆盖新请求。
            var sourceId = selection.Source;
            key = WeatherData.CacheKey(city, sourceId);
            // 定位后可能换成另一座城市：同样走切城逻辑，没缓存就清空旧画面，别等数据到了才发现城市标签不对。
            ShowKey(key);
            var snapshot = await WeatherData.CurrentAsync(city, state.Schedule, sourceId, token, partial => { if (StillSelected() && (_shownKey != key || _last is null)) QueuePartial(partial, key, StillSelected); });
            if (!StillSelected())
            {
                if (!token.IsCancellationRequested && WeatherData.HasWeather(snapshot)) WeatherData.SaveSnapshot(key, snapshot);
                return;
            }
            // 终版马上就到：作废还没执行的 partial，避免一次刷新把整页重建两遍。
            _pendingPartial = null;
            Render(snapshot); _shownKey = key;
            // 定位/选择出的实际城市即缓存键：自动定位的快照也按位置存，AutoKey 记下映射。
            // 只回写天气快照与定位指针，不走整体 Save：设置页刚保存的城市/来源改动不被刷新流程覆盖。
            // 天气域全失败时 CurrentAsync 已回退同城缓存；若连缓存都没有，别用缺测快照覆盖已有数据。
            if (WeatherData.HasWeather(snapshot)) WeatherData.SaveSnapshot(key, snapshot, AutoSelected ? key : null);
            _cycleStart = DateTime.UtcNow;
            // 下一次自动刷新从本次成功刷新重新起算，页头倒计时环与实际自动刷新时刻保持同步。
            timer.Stop(); if (IsVisible && IsLoaded) timer.Start();
        } catch (OperationCanceledException) {
            // 取消即本轮作废，界面与状态由新的 Refresh 接管（画面已是目标城市自己的缓存或空态）。
        } catch { if (StillSelected() && (_last is null || _shownKey != key)) { _pendingPartial = null; _renderSignature = 0; summary.Children.Clear(); summary.Children.Add(new TextBlock { Text = Loc.T("天气数据不可用", "Weather data unavailable"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center }); } }
        finally { if (ReferenceEquals(_token, cts)) { fetching = false; _token = null; } cts.Dispose(); RaiseState(); }
    }
    // 切城：键变了就显示新城市的缓存；没有缓存则清空旧画面。键未变时保留本城数据（含部分结果）。
    private void ShowKey(string key)
    {
        if (key.Length == 0) { if (_last is not null) ShowEmpty(); return; }
        if (_shownKey == key) return;
        if (state.TrySnapshot(key, out var cached)) { Render(cached); _shownKey = key; }
        else ShowEmpty();
    }
    // 空态：换到没有缓存的城市后、刷新尚未成功时清掉上一城的残留画面（指数、分钟降水、页脚一并清）。
    private void ShowEmpty()
    {
        _last = null; _shownKey = ""; _pendingPartial = null;
        // 区块已被清空：作废内容签名，否则换回同一份缓存时会被「无变化」判定跳过、只剩页脚。
        _renderSignature = 0; _renderFailed = false;
        summary.Children.Clear();
        summary.Children.Add(new TextBlock { Text = Loc.T("暂无天气数据", "No weather data"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        forecast.Children.Clear(); lowRow.Children.Clear(); lowNote.Children.Clear(); dayInfoRow.Children.Clear(); chart.Children.Clear();
        todayGlow.Visibility = Visibility.Collapsed; nowRule.Visibility = nowText.Visibility = Visibility.Collapsed;
        // 生活指数视图与分钟窄条也是上一城的内容：一并清掉，但不改用户选中的视图模式。
        indicesBody.Children.Clear(); minuteBars.Children.Clear();
        _minuteHasRain = false; minuteNote.Text = "";
        UpdateMinuteVisibility();
        footerNote.Text = "";
    }
    // 部分结果（不含历史）与终版通常只隔几十毫秒：合并到一次渲染，避免一次刷新把整页重建两遍。
    // 终版到达时会清空 _pendingPartial，排队中的这帧于是直接空转，不会用旧数据盖掉新画面。
    private void QueuePartial(WeatherSnapshot snapshot, string key, Func<bool> stillSelected)
    {
        _pendingPartial = () => { if (stillSelected()) { Render(snapshot); _shownKey = key; } };
        if (_partialQueued) return;
        _partialQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _partialQueued = false;
            var render = _pendingPartial;
            _pendingPartial = null;
            render?.Invoke();
        }));
    }
    // 列数几何的唯一计算点：宽度→列数、快照→实际列数，Fit（判变）与 Render（重画）共用。
    private static int LayoutColumns(double width) => Math.Clamp(7 + (int)Math.Round((width - 440) / 760.0 * 17), 7, 24);
    private int LayoutShown(int columns, WeatherSnapshot? s) => Math.Max(1, s is { } available
        ? (_hourlyMode ? Math.Min(columns, Math.Max(1, available.Hours.Length)) : Math.Min(columns, available.Days.Length))
        : columns);
    private void ApplyLayout(WeatherSnapshot s)
    {
        int columns = LayoutColumns(Width), shown = LayoutShown(columns, s);
        _columns = columns; forecast.Columns = shown; lowRow.Columns = shown; dayInfoRow.Columns = shown;
    }
    // 单个区块绘制失败不拖累其余区块；异常进调试输出，避免整页静默变空白。返回值用于标记这一版没画全，
    // 让下一次相同的快照不会因为「内容签名没变」被跳过，而是重试一遍。
    private bool DrawPart(string name, Action draw)
    {
        try { draw(); return true; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WeatherView] {name}: {ex}"); return false; }
    }
    // 内容签名不含 FetchedAt：自动刷新拿回同一份数据时只需换页脚时刻，整页重建没有意义。
    private static readonly System.Text.Json.JsonSerializerOptions SignatureJson = new()
    {
        IncludeFields = true,   // CurrentPart/DayPoint 用 public 字段存数据，不写上就漏掉实况/逐日变化，界面不会刷新。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static ulong Signature(WeatherSnapshot s)
    {
        var fetched = s.FetchedAt;
        s.FetchedAt = default;
        try
        {
            // 直接序列化成 UTF-8 字节后做 FNV-1a。
            // 覆盖视图用到的全部字段（含数组与字典），30KB 量级下哈希只要几十微秒，分配量减半。
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s, SignatureJson);
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes) { hash ^= b; hash *= 1099511628211UL; }
            return hash;
        }
        catch { return 0; }
        finally { s.FetchedAt = fetched; }
    }

    private readonly DispatcherTimer _timeBoundary = new();
    private readonly DispatcherTimer _nowTick = new() { Interval = TimeSpan.FromMinutes(1) };   // 「现在」分界线的分钟步进
    private DateTimeOffset _nextBoundary;

    private void Render(WeatherSnapshot s)
    {
        s = WeatherLive.ForDisplay(s);
        if (_hourlyMode && s.Hours.Length == 0) { _hourlyMode = false; _lens = Lens.Temp; }
        _last = s;
        ApplyLayout(s);
        if (AutoSelected) _autoCityName = s.Name;
        var now = WeatherClock.Now(s);
        bool timeChanged = now >= _nextBoundary;
        ScheduleBoundary(s, now);
        ulong signature = Signature(s);
        if (!timeChanged && signature != 0 && signature == _renderSignature && !_renderFailed && forecast.Children.Count == forecast.Columns)
        {
            DrawPart("页脚", () => DrawFooter(s));
            return;
        }
        if (_warningPopup is { } warningPopup) warningPopup.IsOpen = false;
        _renderSignature = signature;
        bool drawn = true;
        drawn &= DrawPart("页脚", () => DrawFooter(s));
        drawn &= DrawPart("摘要", () => DrawSummary(s));
        drawn &= DrawPart("预报列", () => DrawForecast(s));
        drawn &= DrawPart("折线", () => DrawChart(s));
        drawn &= DrawPart("分钟降水", () => DrawMinute(s));
        if (_indicesMode) drawn &= DrawPart("生活指数", () => DrawIndices(s));
        _renderFailed = !drawn;
    }

    // 数值/时间的容错格式化：来源没给的字段（null）显示为 --，不让空值进入界面。
    private static string Num(double? value, string suffix, int digits = 0) => value is { } v && double.IsFinite(v) ? v.ToString($"F{digits}", System.Globalization.CultureInfo.InvariantCulture) + suffix : "--";
    private static string Degree(double? value) => value is { } v && double.IsFinite(v) ? $"{v:0}°" : "--";

    private UIElement WindMark(double deg) => new Path
    {
        Data = Geometry.Parse("M0,-4.6 L3.6,3.4 L0,1.4 L-3.6,3.4 Z"),
        Fill = ColWind,
        Width = 7, Height = 7, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(double.IsNaN(deg) ? 0 : WeatherCodes.Compass8Angle(deg))
    };
    // 码值已由 WeatherCodes 归一到同一套区间，这里只翻成最短的一档文字：列宽最小约 44px，
    // 中文最多三个字，英文最多六个字母，超出就会被 TextBlock 直接裁掉。
    private static string AqiLevelShort(int aqi) => WeatherCodes.AqiBand(aqi) switch
    {
        0 => "", 1 => Loc.T("优", "Good"), 2 => Loc.T("良", "Fair"), 3 => Loc.T("轻度", "Light"),
        4 => Loc.T("中度", "Med."), 5 => Loc.T("重度", "Heavy"), _ => Loc.T("严重", "Sev.")
    };
    private static string HourCondition(int code) => code switch
    {
        0 => Loc.T("晴", "Clear"),
        1 or 2 => Loc.T("多云", "Partly"),
        3 => Loc.T("阴", "Cloudy"),
        45 or 48 => Loc.T("雾", "Fog"),
        51 or 53 or 55 => Loc.T("毛毛雨", "Drizzle"),
        56 or 57 or 66 or 67 => Loc.T("冻雨", "Ice"),
        68 or 69 => Loc.T("雨夹雪", "Sleet"),
        61 => Loc.T("小雨", "Light"),
        63 => Loc.T("中雨", "Rain"),
        65 or 82 => Loc.T("大雨", "Heavy"),
        71 => Loc.T("小雪", "Light"),
        73 => Loc.T("中雪", "Snow"),
        75 or 77 => Loc.T("大雪", "Heavy"),
        80 => Loc.T("阵雨", "Showers"),
        81 => Loc.T("强阵雨", "Storm"),
        85 or 86 => Loc.T("阵雪", "Snow"),
        95 or 96 or 99 => Loc.T("雷雨", "Storm"),
        _ => ""
    };
    // ── 工具栏的城市选择 ───────────────────────────────
}
