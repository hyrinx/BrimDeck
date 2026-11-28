using System.Threading;
using Windows.Devices.Geolocation;

namespace BrimDeck;
// Windows 系统定位服务：走系统位置提供程序（WiFi / 网络 / GPS）拿真实坐标，精度数百米级，
// 远高于 IP 库给的城市中心点（武汉的 IP 中心点就落在江汉区，反查到区县必然报错位置）。
// 系统未开「定位服务」或权限被拒时返回 null，调用方退回 IP 定位——那里只敢声称城市级。
internal static class WindowsLocation
{
    // 定位结果连同来源与误差一起给出：只有坐标而没有误差，就没法判断这次该不该声称「到区县」。
    // AccuracyMeters 是系统给的误差半径（米），未知时为 NaN。
    internal readonly record struct Fix(double Latitude, double Longitude, double AccuracyMeters, PositionSource Source)
    {
        // 只有 GPS / WiFi（含基站）且误差在 2 km 内，才值得拿去反查区县；IP 级结果本质是城市级。
        internal bool District => AccuracyMeters is > 0 and <= 2000
            && Source is PositionSource.Satellite or PositionSource.WiFi or PositionSource.Cellular;
    }

    private static readonly object Lock = new();
    private static (DateTime At, Fix Value)? _last;
    // 同一位置几分钟内复用：天气自动刷新与设置页打开都会查定位，不必每次等一轮 WiFi 定位。
    public static async Task<Fix?> PositionAsync(CancellationToken token)
    {
        bool cached;
        lock (Lock) cached = _last is { } kept && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(5);
        // 权限被撤销后 5 分钟内的缓存坐标不该继续发给和风：复用前先用 RequestAccessAsync 快速确认
        // 定位权限仍然有效（已授权时它只返回当前状态，不弹窗）；已拒绝或查询失败时不返回缓存坐标。
        if (cached && await AllowedAsync(token))
        {
            lock (Lock)
            {
                if (_last is { } kept && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(5)) return kept.Value;
            }
        }
        try
        {
            // RequestAccessAsync 要求前台窗口的 UI 线程；调用方多为 UI 事件，跨线程时借调度器回去。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is { } d && !d.CheckAccess()) return await await d.InvokeAsync(() => ReadAsync(token), System.Windows.Threading.DispatcherPriority.Normal, token);
            return await ReadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static async Task<bool> AllowedAsync(CancellationToken token)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is { } d && !d.CheckAccess()) return await await d.InvokeAsync(() => AllowedAsync(token), System.Windows.Threading.DispatcherPriority.Normal, token);
            token.ThrowIfCancellationRequested();
            if (System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().Any(window => window.IsActive) != true)
                return Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("location").CheckAccess()
                    == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
            return await Geolocator.RequestAccessAsync().AsTask(token) == GeolocationAccessStatus.Allowed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<Fix?> ReadAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!await AllowedAsync(token)) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)).AsTask(cts.Token);
            var coordinate = position.Coordinate;
            var point = coordinate.Point.Position;
            var fix = new Fix(point.Latitude, point.Longitude, coordinate.Accuracy, coordinate.PositionSource);
            lock (Lock) _last = (DateTime.UtcNow, fix);
            return fix;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
