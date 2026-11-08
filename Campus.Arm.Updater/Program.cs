using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;


var jsonPath = Path.GetFullPath("updater.json");
Console.WriteLine($"Читаю: {jsonPath}");
var json = File.ReadAllText(jsonPath);
Console.WriteLine(json);
var cfg = JsonSerializer.Deserialize<Config>(json,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
foreach (var a in args)
{
    if (a.StartsWith("--api-url="))
        cfg = cfg with { ApiUrl = a["--api-url=".Length..] };
    if (a.StartsWith("--api-timeout="))
        cfg = cfg with { ApiTimeoutSeconds = int.Parse(a["--api-timeout=".Length..]) };
}
var rows = new List<Row>();

async Task<Row> Run(string name, bool critical, Func<Task<string>> check)
{
    var sw = Stopwatch.StartNew();
    try { return new(name, critical, "ПРОЙДЕНА", sw.ElapsedMilliseconds, await check()); }
    catch (Exception ex) { return new(name, critical, "НЕ ПРОЙДЕНА", sw.ElapsedMilliseconds, ex.Message); }
}

rows.Add(await Run("INTEGRITY", true, () =>
{
    foreach (var f in new[] { cfg.DbPath, cfg.ReaderFile })
        if (!File.Exists(f)) throw new FileNotFoundException(f);
    return Task.FromResult("файлы на месте");
}));

rows.Add(await Run("DB", true, () =>
{
    using var c = new SqliteConnection($"Data Source={cfg.DbPath}");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "PRAGMA integrity_check";
    var ok = cmd.ExecuteScalar()?.ToString();
    if (ok != "ok") throw new InvalidDataException($"integrity_check={ok}");
    return Task.FromResult("integrity_check=ok");
}));

rows.Add(await Run("API", true, async () =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.ApiTimeoutSeconds) };
    var json = await http.GetStringAsync(cfg.ApiUrl);
    using var doc = JsonDocument.Parse(json);
    var ip = doc.RootElement.GetProperty("ip").GetString()!;
    if (!IPAddress.TryParse(ip, out _)) throw new FormatException("нет IP");
    var p = ip.Split('.');
    return $"HTTP 200, IP {p[0]}.{p[1]}.*.*";
}));

rows.Add(await Run("READER", true, async () =>
{
    var uid = (await File.ReadAllTextAsync(cfg.ReaderFile)).Trim();
    if (uid.Length == 0) throw new InvalidDataException("карта не прочитана");
    return $"UID {uid[..Math.Min(4, uid.Length)]}****";
}));

var ready = rows.All(r => !r.Critical || r.Status == "ПРОЙДЕНА");
Directory.CreateDirectory(cfg.ReportsDir);
var act = Path.Combine(cfg.ReportsDir, $"ACT_{cfg.ArmId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
File.WriteAllText(act, $"""
# АКТ готовности АРМ
АРМ: {cfg.ArmId}
Операция: Health Check после обновления

| Проверка | Критичная | Статус | Задержка, мс | Результат |
|---|---|---|---|---|
{string.Join("\n", rows.Select(r => $"| {r.Name} | {(r.Critical ? "да" : "нет")} | {r.Status} | {r.Ms} | {r.Details} |"))}

Заключение: {(ready ? "ГОТОВ" : "НЕ ГОТОВ")}
""");

Console.WriteLine($"Акт: {act}");
return ready ? 0 : 1;

record Config(string ArmId, string DbPath, string ReaderFile, string ApiUrl, int ApiTimeoutSeconds, string ReportsDir);
record Row(string Name, bool Critical, string Status, long Ms, string Details);