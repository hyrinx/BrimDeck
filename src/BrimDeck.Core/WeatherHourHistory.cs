using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// 天气缓存键：快照、逐时历史与来源聚合共用同一套键，避免三处各写一遍导致键漂移。
public static class WeatherKeys
{
    public static string CacheKey(City city, string sourceId) => city.StorageKey() + "|" + sourceId;
}

// 本地逐时历史：每次快照落盘时把其中的整点数据按「城市 + 整点」累积，供下一个窗口的过去小时使用。
// 和风逐小时只有未来、时光机不含今天，过去的小时只能靠本机历史；本地没有就留空，绝不联网补。
public static class WeatherHourHistory
{
    // 保留最近 48 小时：足以覆盖「过去 10 小时」，也给隔天再打开留余量。
    private const int KeepHours = 48;
    private static string Root => Path.Combine(WeatherFiles.Root, "weather", "hour-history");
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly object Lock = new();
    private sealed class FileFormat { public string Key { get; set; } = ""; public List<HourPoint> Hours { get; set; } = []; }

    public static Dictionary<string, HourPoint> Read(string key)
    {
        lock (Lock)
        {
            var result = new Dictionary<string, HourPoint>(StringComparer.Ordinal);
            try
            {
                var path = Path.Combine(Root, WeatherFiles.Name(key, ".json"));
                if (File.Exists(path) && JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path), Json) is { } entry && entry.Key == key)
                    foreach (var hour in entry.Hours ?? [])
                        if (!string.IsNullOrWhiteSpace(hour.Time)) result[hour.Time] = hour;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return result;
        }
    }

    // 把一份快照里的整点数据并入历史；以最新整点为基准保留最近 KeepHours 小时，写盘失败不影响刷新。
    public static void Record(string key, WeatherSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (Lock)
        {
            var dict = Read(key);
            // 快照直接持有 HourPoint，无需从哨兵数组还原；只挑有数据的整点入历史。
            foreach (var hour in snapshot.Hours)
            {
                if (string.IsNullOrWhiteSpace(hour.Time) || !hour.HasData()) continue;
                dict[hour.Time] = hour;
            }
            if (dict.Count == 0) return;
            var newest = dict.Keys.Select(ParseTime).DefaultIfEmpty(DateTime.MinValue).Max();
            string cutoff = newest.AddHours(-KeepHours).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var old in dict.Keys.Where(t => string.CompareOrdinal(t, cutoff) < 0).ToList()) dict.Remove(old);
            try
            {
                WeatherFiles.WriteAtomic(Path.Combine(Root, WeatherFiles.Name(key, ".json")),
                    JsonSerializer.Serialize(new FileFormat { Key = key, Hours = [.. dict.Values.OrderBy(p => p.Time)] }, Json));
            }
            catch { }
        }
    }

    private static DateTime ParseTime(string t)
        => DateTime.TryParseExact(t, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var value) ? value : DateTime.MinValue;
}
