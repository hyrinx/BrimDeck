using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrimDeck.Core;
namespace BrimDeck;
// 数据层：天气取数入口、城市搜索、自动定位、快照落盘与逐时本地历史
internal static class WeatherData
{
    internal sealed class State
    {
        private readonly Func<DeckSettings> _settings;
        internal State(Func<DeckSettings> settings) { _settings = settings; }
        public List<City> Cities { get => _settings().WeatherCities; set => _settings().WeatherCities = value; }
        public int Selected { get => _settings().WeatherSelected; set => _settings().WeatherSelected = value; }
        public WeatherSettings Schedule { get => _settings().Weather; set => _settings().Weather = value; }
        public bool TrySnapshot(string key, out WeatherSnapshot snapshot) => TryReadSnapshot(key, out snapshot);
        // 自动定位指针：最近一次定位到的实际城市键。快照一律按真实城市键存（定位映射选择的城市/区），
        // "auto" 只作指向，供重启后免定位直接命中缓存、设置页「当前位置」行取数。

        public string AutoKey { get => _autoKey.EndsWith("|" + Schedule.SourceFor(null), StringComparison.Ordinal) ? _autoKey : ""; }
        public City? LocatedCity { get; set; }
        public string DisplayAutoKey => LocatedCity is { } city ? CacheKey(city, Schedule.SourceFor(null)) : AutoKey;
        public string KeyFor(City city) => CacheKey(city, Schedule.SourceFor(city));
    }
    private sealed class SnapshotFile { public string Key {get;set;}=""; public WeatherSnapshot? Value {get;set;} }
    private static readonly HttpClient Http=new() {Timeout=TimeSpan.FromSeconds(20)};
    // 快照持有结构化 nullable 对象：CurrentPart/DayPoint 用 public 字段（合并时 ref 写入），必须 IncludeFields 才会落盘。
    // 缺字段是 null，WhenWritingNull 直接不写；缩进让缓存可读。
    private static readonly JsonSerializerOptions WeatherJson=new() {WriteIndented=true,IncludeFields=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    // 已落盘的快照时间：同一条数据没有更新就不再重复写盘，刷新一座城只碰一个文件。
    private static readonly object SnapLock=new();
    private static readonly Dictionary<string,DateTimeOffset> Written=new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WeatherSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static string _autoKey = "";
    internal static event Action? SnapshotChanged;
    private static int _started;
    // 进程内只做一次：清理过期缓存条目、恢复当日请求计数。
    private static void Start()
    {
        if(Interlocked.Exchange(ref _started,1)==1)return;
        WeatherFiles.Ensure(WeatherFiles.Snapshots);
        WeatherCache.Prune(TimeSpan.FromDays(14));
        RequestGate.Restore();
        // 自动定位指针（weather/auto.json，与快照分开放，避免混进快照枚举）：
        // 快照都按真实城市键存，"auto" 只是映射，重启后靠它免定位命中缓存。
        try
        {
            string pointer=Path.Combine(WeatherFiles.Root,"weather","auto.json");
            if(File.Exists(pointer)&&JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(pointer),WeatherJson) is { Key.Length:>0 } entry)_autoKey=entry.Key;
        }
        catch { }
    }
    internal static State Load(Func<DeckSettings> settings)
    {
        Start();
        return new State(settings);
    }
    private static bool TryReadSnapshot(string key, out WeatherSnapshot snapshot)
    {
        lock (SnapLock)
        {
            if (Snapshots.TryGetValue(key, out snapshot!)) return true;
            if (key.Length == 0) return false;
            try
            {
                var path = Path.Combine(WeatherFiles.Snapshots, WeatherFiles.Name(key, ".json"));
                if (!File.Exists(path)) return false;
                var entry = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path), WeatherJson);
                if (entry?.Key != key || entry.Value is null) return false;
                snapshot = entry.Value;
                snapshot.CityKey = key;
                Snapshots[key] = snapshot;
                Written[key] = snapshot.FetchedAt;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }

    internal static void ReleaseCachedSnapshots()
    {
        lock (SnapLock)
        {
            foreach (var key in Written.Keys) Snapshots.Remove(key);
            Written.Clear();
        }
    }
    internal static void SaveSnapshot(string key,WeatherSnapshot snapshot,string? autoKey=null)
    {
        lock(SnapLock)
        {
            TryReadSnapshot(key, out var previous);
            if (previous is not null && previous.FetchedAt > snapshot.FetchedAt) return;
            Snapshots[key] = snapshot;
            if (!Written.TryGetValue(key, out var at) || at != snapshot.FetchedAt || !ReferenceEquals(previous, snapshot))
            {
                if(WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Snapshots,WeatherFiles.Name(key,".json")),JsonSerializer.Serialize(new SnapshotFile{Key=key,Value=snapshot},WeatherJson)))
                {
                    Written[key]=snapshot.FetchedAt;
                    // 快照确实被接受后才并入本地历史；历史键=快照的真实城市键（CityKey，Merge 时写入）。
                    WeatherHourHistory.Record(snapshot.CityKey,snapshot);
                }
                else Written.Remove(key);
            }
            if(autoKey is {Length:>0})
            {
                _autoKey = autoKey;
                WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Root,"weather","auto.json"),JsonSerializer.Serialize(new SnapshotFile{Key=autoKey},WeatherJson));
            }
        }
        RequestGate.Flush();
        SnapshotChanged?.Invoke();
    }
    // 城市搜索：配置了和风 Key/Host 就只用和风 GeoAPI——能精确命中区县（搜「洪山区」得「洪山区 · 武汉市 · 湖北省」），
    // 返回的 LocationID 与和风时光机同源。请求失败不退回 Open-Meteo，避免同一次搜索混用两套层级和精度。
    // 未配置和风时才用 Open-Meteo（全球地名库，中文覆盖一般）。
    // 拼音里的分隔撇号（chang'feng）会被和风当成分词符，先试去掉撇号的写法，失败再用原串。
    internal static async Task<List<City>> Search(string query, WeatherSettings settings, CancellationToken token)
    {
        string raw = query.Trim();
        var attempts = new List<string> { raw };
        if (raw.Any(ch => ch is '\'' or '’') && raw.All(ch => char.IsAsciiLetter(ch) || ch is '\'' or '’' or '-'))
        {
            string plain = new(raw.Where(ch => ch is not ('\'' or '’')).ToArray());
            if (plain.Length > 0 && plain != raw) attempts.Insert(0, plain);
        }
        if (WeatherSecrets.Key.Length > 0 && settings.HeFengHost.Trim().Length > 0)
        {
            bool failed = false;
            foreach (var attempt in attempts)
            {
                try
                {
                    var found = await SearchHeFeng(attempt, settings, token);
                    if (found.Count > 0) return found;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { failed = true; }
            }
            // 区分「确实没有这个城市」与「这次请求失败」：两者都返回空列表会让界面把超时/限流
            // 显示成「未找到」，看起来就是搜索偶尔失灵。失败交给界面提示重试。
            if (failed) throw new HttpRequestException("QWeather search unavailable");
            return [];
        }
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=10&language=" + (Loc.IsEnglish ? "en" : "zh") + "&name=" + Uri.EscapeDataString(raw), token));
        var cities = new List<City>();
        if (doc.RootElement.TryGetProperty("results", out var list))
            foreach (var c in list.EnumerateArray())
                cities.Add(new City(
                    City.Compose(Json.Text(c, "name"), Json.Text(c, "admin2"), Json.Text(c, "admin1"), Json.Text(c, "country")),
                    c.GetProperty("latitude").GetDouble(), c.GetProperty("longitude").GetDouble()) { TimeZoneId = Json.Text(c, "timezone"), CountryCode = Json.Text(c, "country_code") });
        return cities;
    }
    // 和风 GeoAPI v2 city/lookup：按地名或「经度,纬度」坐标反查最多 10 个，结构化解析省/市/国家/城市 id。
    // 和风明确禁止缓存 GeoAPI 数据，因此这里不做任何落盘/内存缓存：防抖后的每一次查询都直接请求。
    // 错误响应（HTTP 非 2xx 或 code != 200）直接当作空结果，调用方据此区分「没有这个城市」与请求失败。
    private static async Task<List<City>> SearchHeFeng(string query, WeatherSettings settings, CancellationToken token)
    {
        string host = HeFengSource.NormalizeHost(settings.HeFengHost);
        string lang = Loc.IsEnglish ? "en" : "zh";
        string body = "";
        // 搜索用独立闸门通道：天气取数（尤其时光机一轮 10 次请求）触发 429/失败熔断时，
        // 不该把城市搜索一起停掉——否则用户搜新词会全部落空，看起来像搜索坏了。
        // 超时 8 秒：搜索是交互操作，不该让输入框等满 20 秒。
        var result = await WeatherHttp.GetAsync(HeFengSource.GeoId, $"{host}/geo/v2/city/lookup?location={Uri.EscapeDataString(query)}&number=10&lang={lang}", token,
            request => request.Headers.TryAddWithoutValidation("X-QW-Api-Key", WeatherSecrets.Key), 8, settings);
        if (result.Body is { Length: > 0 } raw)
        {
            using var probe = JsonDocument.Parse(raw);
            var code = Json.Text(probe.RootElement, "code");
            if (code == "404") return [];
            if (code is not (null or "200")) throw new HttpRequestException($"QWeather city lookup failed ({code})");
            body = raw;
        }
        var cities = new List<City>();

        if (body.Length == 0) throw new HttpRequestException($"QWeather city lookup failed ({result.Status})");
        using var doc = JsonDocument.Parse(body);
        if (Json.Get(doc.RootElement, "location") is not { ValueKind: JsonValueKind.Array } list) throw new JsonException("Missing QWeather city list");
        foreach (var item in list.EnumerateArray())
        {
            double? lat = Json.Number(item, "lat"), lon = Json.Number(item, "lon");
            if (Json.Text(item, "name") is not { Length: > 0 } name || lat is null || lon is null) continue;
            // 和风返回的大陆坐标是 GCJ-02（官方词汇表），入库前还原成 WGS-84：程序内只存一套真实坐标，
            // 发给和风时再由请求层加密一次，两边各转一次互相抵消。
            var (wgsLat, wgsLon) = Geo.ToWgs84(lat.Value, lon.Value);
            cities.Add(new City(
                City.Compose(name, Json.Text(item, "adm2"), Json.Text(item, "adm1"), Json.Text(item, "country")),
                wgsLat, wgsLon, Json.Text(item, "id") ?? "") { TimeZoneId = Json.Text(item, "tz") });
        }
        return cities;
    }
    // 自动定位，精度分层，不冒充：
    // 1) Windows 系统定位（GPS/WiFi，十米到数百米级）→ 和风反查到区县，「洪山区 · 武汉市 · 湖北省」才真正可信；
    // 2) 系统只给出 IP 级结果（来源是 IP 或误差 > 2 km）→ 反查只取城市与省两级；
    // 3) 拿不到系统坐标：才退回 IP 定位（城市级），名字和坐标都来自 IP。
    // 系统定位与是否配置和风无关：没 Key 时坐标照样要拿，只是名字只能靠 IP 或 Open-Meteo 补。
    internal static async Task<City?> LocateAsync(WeatherSettings settings, CancellationToken token)
    {
        bool hefeng=WeatherSecrets.Key.Length>0&&settings.HeFengHost.Trim().Length>0;
        WindowsLocation.Fix? fix=null;
        try { fix=await WindowsLocation.PositionAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
        // 反查失败不清空已取得的系统坐标：只丢「区县级」精度，不丢定位本身，流程落到 IP 阶段继续。
        if(hefeng&&fix is { } located)
        {
            try
            {
                // 和风要求大陆坐标用 GCJ-02（官方词汇表）；City 里存的是系统给的 WGS-84，
                // 这次查询按加密后的坐标提问，贴边界的点才不会落到隔壁区。
                var (queryLat,queryLon)=Geo.ToGcj02(located.Latitude,located.Longitude);
                var found=await SearchHeFeng(FormattableString.Invariant($"{queryLon:0.###},{queryLat:0.###}"),settings,token);
                if(found.Count>0)
                {
                    // 反查返回的是行政区中心点：只借名称/ID/时区，坐标用系统定位值；
                    // 精度不够时按城市级显示，超不出城市范围的区县名不摆出来。
                    string name=located.District?found[0].Name:City.WithoutDistrict(found[0].Name);
                    return found[0] with { Name=name, Latitude=located.Latitude, Longitude=located.Longitude };
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
        }
        try {
            using var doc=JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang="+(Loc.IsEnglish?"en":"zh-CN"),token));
            var root=doc.RootElement;
            if(root.TryGetProperty("success",out var ok)&&ok.ValueKind==JsonValueKind.False)return null;
            if(!root.TryGetProperty("latitude",out var la)||!root.TryGetProperty("longitude",out var lo))return null;
            double lat=la.GetDouble(),lon=lo.GetDouble();
            // 系统坐标在手（反查失败落下来的）不再对 IP 坐标做反查：坐标已是数百米级，
            // 只需要 IP 补城市名；返回时坐标仍用系统定位的精确值。
            if(hefeng&&fix is null)
            {
                List<City> found;
                try {
                    var (queryLat,queryLon)=Geo.ToGcj02(lat,lon);
                    found=await SearchHeFeng(FormattableString.Invariant($"{queryLon:0.###},{queryLat:0.###}"),settings,token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { found=[]; }
                if(found.Count>0)
                {
                    // IP 坐标只有城市精度：丢掉反查结果里的区县段与区县 id，不冒充精确到区。
                    return new City(City.WithoutDistrict(found[0].Name), lat, lon) { TimeZoneId = found[0].TimeZoneId, CountryCode = Json.Text(root, "country_code") };
                }
            }
            if(root.TryGetProperty("city",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} query) {
                List<City> found;
                try { found=await Search(query,settings,token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { found=[]; }
                if(found.Count>0)
                {
                    var best=found.OrderBy(c=>Math.Abs(c.Latitude-lat)+Math.Abs(c.Longitude-lon)).First();
                    if(fix is { } at)
                    {
                        // IP 城市名离系统坐标三百公里以上，说明 IP 落在运营商出口（跨城 NAT / 代理）：
                        // 名称按不可信处理，显示「当前位置」。
                        if(Geo.DistanceKm(at.Latitude,at.Longitude,best.Latitude,best.Longitude)>300)
                            return new City("当前位置",at.Latitude,at.Longitude) { CountryCode = Json.Text(root, "country_code") };
                        // 有系统坐标时搜索只用来拿规范的名称与 LocationId，坐标以系统定位为准。
                        return best with { Latitude = at.Latitude, Longitude = at.Longitude, TimeZoneId = null };
                    }
                    return best;
                }
            }
            var parts=new List<string>();
            if(root.TryGetProperty("city",out name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} s)parts.Add(s);
            if(root.TryGetProperty("region",out var region)&&region.ValueKind==JsonValueKind.String&&region.GetString() is {Length:>0} r)parts.Add(r);
            if(root.TryGetProperty("country",out var country)&&country.ValueKind==JsonValueKind.String&&country.GetString() is {Length:>0} c&&!parts.Contains(c))parts.Add(c);
            // 精确坐标在手时同样不信任 IP 时区（原因同上）：清空让聚合层按坐标自行确定；
            // 没有精确坐标时坐标与时区同属 IP 返回，保留。
            return new City(parts.Count>0?City.Compose([.. parts]):"当前位置",fix?.Latitude ?? lat,fix?.Longitude ?? lon)
            {
                CountryCode = Json.Text(root, "country_code"),
                TimeZoneId = (fix is null && Json.Get(root, "timezone") is { } tz) ? Json.Text(tz, "id") : null
            };
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; } catch { return null; }
    }
    // 天气聚合入口：当前、24 小时、24 天（历史 10 + 今天 + 未来 13）、日升日落与空气质量由 WeatherAggregator 统一拼装。
    // 历史单独一轮，progress 让界面先画不含历史的版本，历史到了再用返回值整体替换。
    internal static string CacheKey(City city, string sourceId) => WeatherKeys.CacheKey(city, sourceId);
    internal static async Task<WeatherSnapshot> CurrentAsync(City city, WeatherSettings schedule, string sourceId, CancellationToken token, Action<WeatherSnapshot>? progress = null)
    {
        var snapshot = await WeatherAggregator.FetchAsync(city, schedule, sourceId, token, progress is null ? null : partial =>
        {
            token.ThrowIfCancellationRequested();
            WeatherLive.FromDetails(city, sourceId, partial);
            progress(partial);
        });
        token.ThrowIfCancellationRequested();
        WeatherLive.FromDetails(city, sourceId, snapshot);
        if (HasWeather(snapshot)) return snapshot;
        // 预警返回有效空列表也是成功，但不能因此把同城上次成功的实况/预报当成缺测覆盖掉。
        if (TryReadSnapshot(CacheKey(city, sourceId), out var cached))
        {
            if (snapshot.Sources.ContainsKey("Warning") || snapshot.Sources.ContainsKey("Indices"))
            {
                cached = cached.ShallowCopy();
                if (snapshot.Sources.ContainsKey("Warning")) cached.Warnings = [.. snapshot.Warnings];
                if (snapshot.Sources.ContainsKey("Indices")) cached.Indices = [.. snapshot.Indices];
                cached.Sources = new Dictionary<string, string>(cached.Sources, StringComparer.Ordinal);
                foreach (var (domain, source) in snapshot.Sources) cached.Sources[domain] = source;
            }
            return cached;
        }
        return snapshot;
    }

    // 只有天气相关域的成功才算一次可用取数；只有预警/指数成功时回退同城缓存的天气。
    internal static bool HasWeather(WeatherSnapshot snapshot)
        => snapshot.Temperature is { } t && double.IsFinite(t) || snapshot.Sources.Keys.Any(domain =>
            domain is "Current" or "Hourly" or "Daily" or "Air" or "History" or "Minutely");
}
