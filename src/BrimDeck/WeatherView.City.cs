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
    internal sealed record CityChoice(string Label, bool Selected, int Index);

    // 灵动岛空间有限：城市名只显示前两级（洪山区 · 武汉市）。
    internal static string BriefName(string full) => City.BriefOf(full);
    private bool AutoSelected => state.Selected < 0 || state.Selected >= state.Cities.Count;
    private string AutoCityName => _autoCityName == "当前位置" ? "" : _autoCityName;
    internal string CityLabel
    {
        get
        {
            string brief = AutoSelected
                ? BriefName(AutoCityName)
                : state.Selected >= 0 && state.Selected < state.Cities.Count ? state.Cities[state.Selected].Brief : "";
            return brief.Length > 0 ? brief : Loc.T("自动定位", "Auto location");
        }
    }
    // 下拉第一行写清是自动定位，后面跟前两级城市名；其余选择项同样只显示前两级。
    internal string AutoMenuLabel => AutoCityName.Length > 0
        ? Loc.T($"自动定位·{BriefName(AutoCityName)}", $"Auto · {BriefName(AutoCityName)}")
        : Loc.T("自动定位", "Auto location");
    internal List<CityChoice> CityChoices()
    {
        var choices = new List<CityChoice> { new(AutoMenuLabel, AutoSelected, -1) };
        for (int i = 0; i < state.Cities.Count; i++) choices.Add(new CityChoice(state.Cities[i].Brief, !AutoSelected && state.Selected == i, i));
        return choices;
    }
    internal async Task ChooseCityAsync(int index)
    {
        if (state.Selected == index) return;
        state.Selected = index; _app.QueueSave();
        foreach (var deck in _app.Decks) deck.WeatherSelectionChanged(this);
        await Refresh();
    }
    // 设置页增删/切换城市后视图仍展开时走这里：Refresh 开头会取消在途请求，再按最新城市取数。
    internal void ReloadSettings()
    {
        if (!IsLoaded || !IsVisible) return;
        state = WeatherData.Load(() => _app.Settings);
        _ = Refresh();
    }
    internal Action? SettingsRequested;
}
