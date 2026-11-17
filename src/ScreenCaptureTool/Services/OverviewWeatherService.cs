using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Services;

public sealed record WeatherRegion(string Id, string Province, string City, string District, double Latitude, double Longitude, string Search)
{
    public string Label => City == District ? City : City + " · " + District;
    public string ParentLabel => Province == City ? Province : Province + " · " + City;
    public string FullLabel => ParentLabel + " · " + District;
    public override string ToString() => District;
}

/// <summary>离线区县目录，不以在线搜索结果冒充完整行政区划。</summary>
public static class WeatherRegions
{
    private static readonly Lazy<IReadOnlyList<WeatherRegion>> Data = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://yuSnip/Assets/Weather/MainlandRegions.tsv"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var rows = new List<WeatherRegion>();
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length != 7 || !double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            rows.Add(new(p[0], p[1], p[2], p[3], lat, lon, p[6]));
        }
        return rows;
    });
    public static IReadOnlyList<WeatherRegion> All => Data.Value;
    public static WeatherRegion? Find(string? id) => string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(r => r.Id == id);
    public static IReadOnlyList<WeatherRegion> Search(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Concat(terms);
        return All.Where(r => terms.All(q => (r.Province + r.City + r.District + r.Search).Contains(q, StringComparison.OrdinalIgnoreCase)))
            // 连续名称／拼音优先，避免 xi hu 被大量分散音节挤出候选项。
            .OrderByDescending(r => r.District.Contains(joined, StringComparison.OrdinalIgnoreCase) ? 2
                : (r.Province + r.City + r.District + r.Search.Replace(" ", "")).Contains(joined, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Take(30).ToArray();
    }
}

public enum WeatherScene { Clear, Cloudy, Overcast, Fog, Rain, Snow, Thunder }

public sealed record OverviewWeather(string RegionId, DateTimeOffset FetchedAt, DateTimeOffset ValidAt,
    double Temperature, int Code, double WindKmh, double CloudPercent, double Precipitation,
    IReadOnlyList<(DateTimeOffset Rise, DateTimeOffset Set)> Sun)
{
    public WeatherScene Scene => SceneFor(Code);
    public bool IsUsable(DateTimeOffset now) => now >= FetchedAt.AddMinutes(-5) && now - FetchedAt <= TimeSpan.FromHours(2)
        && now - ValidAt <= TimeSpan.FromHours(3) && ValidAt <= now.AddMinutes(30);
    public bool IsFresh(DateTimeOffset now) => IsUsable(now) && now - FetchedAt < TimeSpan.FromMinutes(30);
    public string PeriodAt(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        foreach (var sun in Sun)
            if (sun.Rise.Date == local.Date && sun.Set > sun.Rise)
            {
                if (now < sun.Rise || now >= sun.Set.AddMinutes(35)) return "Night";
                return now >= sun.Set.AddMinutes(-35) ? "Dusk" : "Day";
            }
        return Controls.OverviewSky.PeriodAt(local.Hour);
    }
    public static WeatherScene SceneFor(int code) => code switch
    {
        0 or 1 => WeatherScene.Clear, 2 => WeatherScene.Cloudy, 3 => WeatherScene.Overcast,
        45 or 48 => WeatherScene.Fog,
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherScene.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherScene.Snow,
        95 or 96 or 97 or 99 => WeatherScene.Thunder, _ => WeatherScene.Overcast
    };
    public static string Description(int code) => code switch
    {
        0 => "晴", 1 => "晴间多云", 2 => "多云", 3 => "阴", 45 => "雾", 48 => "雾凇",
        51 => "毛毛雨", 53 or 55 => "细雨", 56 or 57 => "冻毛毛雨", 61 => "小雨", 63 => "中雨", 65 => "大雨",
        66 or 67 => "冻雨", 71 => "小雪", 73 => "中雪", 75 => "大雪", 77 => "雪粒", 80 => "小阵雨",
        81 => "阵雨", 82 => "强阵雨", 85 => "小阵雪", 86 => "强阵雪", 95 or 97 => "雷雨", 96 or 99 => "雷雨伴冰雹",
        _ => "天气未知"
    };
}

public sealed class WeatherRequestException(string message, TimeSpan retryAfter) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>仅由可见概览调用；手工 JSON 读写兼容 NativeAOT，测试可注入 HTTP 和独立存储路径。</summary>
public sealed class OverviewWeatherService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http;
    private readonly string _path;
    public string RegionId { get; private set; } = "";
    public OverviewWeather? Cached { get; private set; }
    public OverviewWeatherService(string path, HttpClient? http = null)
    {
        _path = path;
        _http = http ?? SharedHttp;
    }
    public void Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 64 * 1024) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return;
            RegionId = root.GetProperty("region").GetString() ?? "";
            if (WeatherRegions.Find(RegionId) == null) { RegionId = ""; return; }
            if (root.TryGetProperty("payload", out var payload)
                && DateTimeOffset.TryParse(root.GetProperty("fetched").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetched))
            {
                Cached = Parse(payload, RegionId, fetched);
                _payload = payload.GetRawText();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { AppLogger.Warn("天气缓存不可用：" + ex.GetType().Name); }
    }
    private string? _payload;
    public void Select(string regionId)
    {
        RegionId = regionId;
        Cached = null;
        _payload = null;
        Save();
    }
    public void Accept(OverviewWeather weather, string payload)
    {
        if (weather.RegionId != RegionId) return;
        Cached = weather;
        _payload = payload;
        Save();
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("region", RegionId);
                if (Cached != null && _payload != null)
                {
                    writer.WriteString("fetched", Cached.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
                    writer.WritePropertyName("payload");
                    using var doc = JsonDocument.Parse(_payload); doc.RootElement.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            File.WriteAllBytes(_path + ".tmp", buffer.ToArray());
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.Warn("天气偏好保存失败：" + ex.GetType().Name); }
    }
    public static Uri RequestUri(WeatherRegion region) => new(string.Create(CultureInfo.InvariantCulture,
        $"https://api.open-meteo.com/v1/forecast?latitude={region.Latitude:F4}&longitude={region.Longitude:F4}&current=temperature_2m,weather_code,is_day,precipitation,cloud_cover,wind_speed_10m&daily=sunrise,sunset&timezone=Asia%2FShanghai&forecast_days=2"));

    public async Task<(OverviewWeather Weather, string Payload)> FetchAsync(WeatherRegion region, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(region));
        request.Headers.UserAgent.ParseAdd("yuSnip/1.0 (https://yusnip.top)");
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromHours(1);
            throw new WeatherRequestException("天气服务繁忙，稍后再试", retry > TimeSpan.FromMinutes(5) ? retry : TimeSpan.FromMinutes(5));
        }
        response.EnsureSuccessStatusCode();
        string payload = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (payload.Length > 64 * 1024) throw new JsonException("天气数据过大");
        using var document = JsonDocument.Parse(payload);
        var weather = Parse(document.RootElement, region.Id, DateTimeOffset.UtcNow);
        if (!weather.IsUsable(DateTimeOffset.UtcNow)) throw new JsonException("天气数据时间已过期");
        return (weather, payload);
    }
    public static OverviewWeather Parse(JsonElement root, string id, DateTimeOffset fetched)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) throw new JsonException("天气服务返回错误");
        if (root.GetProperty("utc_offset_seconds").GetInt32() != 28800) throw new JsonException("天气时区不匹配");
        var units = root.GetProperty("current_units");
        if (units.GetProperty("temperature_2m").GetString() != "°C" || units.GetProperty("wind_speed_10m").GetString() != "km/h"
            || units.GetProperty("precipitation").GetString() != "mm") throw new JsonException("天气单位不匹配");
        var current = root.GetProperty("current");
        double Number(string key, double min, double max)
        {
            double v = current.GetProperty(key).GetDouble();
            if (!double.IsFinite(v) || v < min || v > max) throw new JsonException("天气字段异常");
            return v;
        }
        static DateTimeOffset Time(JsonElement e) => new(DateTime.ParseExact(e.GetString()!, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), TimeSpan.FromHours(8));
        var sun = new List<(DateTimeOffset, DateTimeOffset)>();
        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("sunrise", out var rises) && daily.TryGetProperty("sunset", out var sets))
            for (int i = 0; i < Math.Min(rises.GetArrayLength(), sets.GetArrayLength()); i++)
                if (rises[i].ValueKind == JsonValueKind.String && sets[i].ValueKind == JsonValueKind.String)
                    sun.Add((Time(rises[i]), Time(sets[i])));
        return new(id, fetched, Time(current.GetProperty("time")), Number("temperature_2m", -100, 70), current.GetProperty("weather_code").GetInt32(),
            Number("wind_speed_10m", 0, 500), Number("cloud_cover", 0, 100), Number("precipitation", 0, 1000), sun);
    }
}
