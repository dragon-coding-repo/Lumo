using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumo.Core;

/// <summary>翻译结果缓存。有界 LRU + TTL，常驻内存只有几百 KB 量级。</summary>
public sealed class TranslationCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _map = new();
    private readonly LinkedList<string> _order = new();
    private long _hits, _misses;

    private sealed record Entry(string Value, DateTimeOffset ExpiresAt);

    public TranslationCache(int capacity, TimeSpan ttl)
    {
        // 容量下限兜底，避免配置写错导致每次都 miss。
        _capacity = Math.Clamp(capacity, 16, 50_000);
        _ttl = ttl < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1)
            : ttl > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : ttl;
    }

    /// <summary>缓存键。任何影响结果的字段都必须参与，否则会串味。</summary>
    public static string KeyOf(string engine, TranslateRequest req) =>
        $"{engine}\u0001{req.From}\u0001{req.To}\u0001{req.Text.Trim()}";

    public string? Get(string key)
    {
        if (!_map.TryGetValue(key, out var entry)) { _misses++; return null; }
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Remove(key);
            _misses++;
            return null;
        }
        Promote(key);
        _hits++;
        return entry.Value;
    }

    public void Put(string key, string value)
    {
        _map[key] = new Entry(value, DateTimeOffset.UtcNow + _ttl);
        Promote(key);
        while (_order.Count > _capacity)
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _map.Remove(oldest);
        }
    }

    private void Promote(string key)
    {
        _order.Remove(key);
        _order.AddLast(key);
    }

    private void Remove(string key)
    {
        _order.Remove(key);
        _map.Remove(key);
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
        _hits = _misses = 0;
    }

    public (long Hits, long Misses, int Entries, int Bytes, double HitRate) Stats()
    {
        var total = _hits + _misses;
        return (_hits, _misses, _map.Count, ApproxBytes(), total == 0 ? 0 : (double)_hits / total);
    }

    /// <summary>近似占用字节数，给设置页显示内存开销用。</summary>
    private int ApproxBytes() => _map.Sum(e => e.Key.Length + e.Value.Value.Length + 64);
}

/// <summary>配置。序列化时用 camelCase，与前端保持一致。</summary>
public sealed class AppConfig
{
    /// <summary>当前配置版本。v2 起默认引擎从 google/bing 换成 edge。</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public string SourceLang { get; set; } = "auto";
    public string TargetLang { get; set; } = "zh";
    /// <summary>打开时按检测结果决定目标语言。</summary>
    public bool AutoTarget { get; set; } = true;

    public string DefaultEngine { get; set; } = EngineIds.Edge;
    /// <summary>失败时依次回退的引擎链。</summary>
    public List<string> FallbackEngines { get; set; } = new() { EngineIds.Google };
    public Dictionary<string, EngineConfig> EngineConfigs { get; set; } = new();

    public bool ListenClipboard { get; set; } = true;
    public int ClipboardDebounceMs { get; set; } = 350;

    public string HotkeyTrigger { get; set; } = "Alt+D";
    public string HotkeyInput { get; set; } = "Alt+S";
    public string HotkeyOcr { get; set; } = "Alt+O";
    public string HotkeyMini { get; set; } = "Alt+M";

    public bool StayOnTop { get; set; } = true;
    public bool MiniMode { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool CloseToTray { get; set; } = true;
    public bool LaunchOnBoot { get; set; }
    /// <summary>首次运行是否弹出用法说明。看过一次就置 false。</summary>
    public bool ShowFirstRunHelp { get; set; } = true;

    public bool AutoCopy { get; set; }
    public bool SaveHistory { get; set; } = true;
    public int HistoryLimit { get; set; } = 300;

    public int CacheCapacity { get; set; } = 800;
    public int CacheTtlHours { get; set; } = 168;

    public int HttpTimeoutSecs { get; set; } = 15;
    public string? Proxy { get; set; }

    /// <summary>逐字段夹紧到安全范围。任何来自磁盘的值都视为不可信输入。</summary>
    public AppConfig Sanitized()
    {
        Migrate();
        Version = CurrentVersion;
        SourceLang = string.IsNullOrWhiteSpace(SourceLang) ? "auto" : SourceLang;
        // 目标语言不能是 auto，否则没东西可译。
        TargetLang = string.IsNullOrWhiteSpace(TargetLang) || TargetLang == "auto" ? "zh" : TargetLang;
        if (string.IsNullOrWhiteSpace(DefaultEngine)) DefaultEngine = EngineIds.Edge;
        // 回退链里不能有自己，否则会无限自我回退。
        FallbackEngines.RemoveAll(e => e == DefaultEngine);
        if (FallbackEngines.Count > 4) FallbackEngines = FallbackEngines.Take(4).ToList();

        ClipboardDebounceMs = Math.Clamp(ClipboardDebounceMs, 50, 3000);
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.3, 1.0) : 1.0;
        HistoryLimit = Math.Clamp(HistoryLimit, 0, 10_000);
        CacheCapacity = Math.Clamp(CacheCapacity, 16, 50_000);
        CacheTtlHours = Math.Clamp(CacheTtlHours, 1, 24 * 30);
        HttpTimeoutSecs = Math.Clamp(HttpTimeoutSecs, 3, 120);
        return this;
    }

    /// <summary>
    /// 配置版本迁移。
    /// </summary>
    /// <remarks>
    /// v1 的两个免 Key 默认引擎（google / bing）在 2026 年都失效了：
    /// google 在国内不通，bing 用的 auth 端点被微软下线（固定 404）。
    /// 也就是说 v1 配���好的用户一次翻译都没成功过。把默认换成现在可用的 Edge，
    /// 是纯改进，不会覆盖任何用户主动配置过的带 Key 引擎。
    /// </remarks>
    private void Migrate()
    {
        if (Version >= CurrentVersion) return;

        var explicitlyConfigured =
            DefaultEngine is EngineIds.DeepL or EngineIds.OpenAi or EngineIds.Custom;

        if (!explicitlyConfigured)
        {
            DefaultEngine = EngineIds.Edge;
            // Google 降级为备选：配了代理的机器上它仍然能用
            if (!FallbackEngines.Contains(EngineIds.Google)) FallbackEngines.Insert(0, EngineIds.Google);
            // bing 已死，从回退链里摘掉
            FallbackEngines.Remove(EngineIds.Bing);
        }
    }

    public EngineConfig EngineFor(string id) =>
        EngineConfigs.TryGetValue(id, out var c) ? c : new EngineConfig();

    /// <summary>翻译引擎链：主引擎 + 回退。</summary>
    public List<string> EngineChain()
    {
        var chain = new List<string> { DefaultEngine };
        chain.AddRange(FallbackEngines);
        return chain;
    }

    public TimeSpan CacheTtl => TimeSpan.FromHours(CacheTtlHours);
}

/// <summary>配置文件读写。写失败不抛到 UI，只返回错误字符串。</summary>
/// <summary>统一的 JSON 序列化设置：camelCase，属性名与前端保持一致。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 解析 JSON，失败时抛带分类的 <see cref="TranslateException"/>。
    /// 引擎返回 HTML 错误页是很常见的，直接让 JsonException 漏出去，
    /// 上层就分不清「服务端挂了」和「格式不对」。
    /// </summary>
    public static JsonDocument TryParseDoc(string body, string engineName)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw TranslateException.Parse($"{engineName} 响应不是合法 JSON：{ex.Message}");
        }
    }
}

public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public string Path => _path;

    /// <summary>读取配置。文件不存在或解析失败都返回默认配置，失败时把损坏文件留档。</summary>
    public AppConfig Load()
    {
        string raw;
        try { raw = File.ReadAllText(_path); }
        catch { return new AppConfig().Sanitized(); }

        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(raw, Json.Options);
            return (cfg ?? new AppConfig()).Sanitized();
        }
        catch (JsonException)
        {
            // 用户可能想手工抢救，不要直接删。
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* 尽力而为 */ }
            return new AppConfig().Sanitized();
        }
    }

    /// <summary>原子写入：同目录临时文件 + 替换。</summary>
    public void Save(AppConfig cfg)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(cfg, Json.Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 尽力而为 */ }
            throw;
        }
    }
}

/// <summary>翻译历史。环形上限的 JSON 文件，追加时顺带去重和裁剪。</summary>
public sealed class HistoryStore
{
    /// <summary>超长文本没有收藏价值，存下来只会让历史文件失控。</summary>
    private const int MaxTextLen = 4000;

    private readonly string _path;
    private readonly List<HistoryEntry> _entries = new();
    private int _limit;
    private long _nextId = 1;

    public HistoryStore(string path, int limit)
    {
        _path = path;
        _limit = Math.Clamp(limit, 1, 10_000);
        Load();
    }

    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public int Limit => _limit;

    private static string Fingerprint(HistoryEntry e) =>
        $"{e.From}\u0001{e.To}\u0001{e.SourceText.Trim()}\u0001{e.TargetText.Trim()}";

    private void Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("entries", out var list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var e = item.Deserialize<HistoryEntry>(Json.Options);
                    if (e is not null) _entries.Add(e);
                }
            }
            if (doc.RootElement.TryGetProperty("nextId", out var n) && n.TryGetInt64(out var v))
                _nextId = v;
        }
        catch
        {
            // 历史文件坏了不值得抢救。
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* 尽力而为 */ }
            return;
        }

        // 补上缺失的 id，避免重放历史后 id 冲突。
        foreach (var e in _entries.Where(e => e.Id >= _nextId))
            _nextId = e.Id + 1;
    }

    private void Persist()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new { nextId = _nextId, entries = _entries }, Json.Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>追加一条。原文译文完全相同的重复记录直接跳过。</summary>
    public long? Add(string source, string target, string from, string to, string engine)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return null;
        var entry = new HistoryEntry
        {
            Id = _nextId,
            SourceText = Truncate(source.Trim()),
            TargetText = Truncate(target.Trim()),
            From = from, To = to, Engine = engine,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var idx = _entries.FindIndex(e => Fingerprint(e) == Fingerprint(entry));
        if (idx >= 0)
        {
            // 只跳过非收藏的重复项；用户手动收藏的那条要保留。
            if (_entries[idx].Starred) return null;
            var existing = _entries[idx];
            existing.Timestamp = entry.Timestamp;
            existing.Engine = engine;
            _entries.RemoveAt(idx);
            _entries.Insert(0, existing);
            Trim();
            TryPersist();
            return existing.Id;
        }

        var id = _nextId++;
        _entries.Insert(0, entry);
        Trim();
        TryPersist();
        return id;
    }

    /// <summary>从最旧的往回删，收藏项跳过。剩下全是收藏时宁可超限也不删。</summary>
    private void Trim()
    {
        for (var i = _entries.Count - 1; i >= 0 && _entries.Count > _limit; i--)
        {
            if (!_entries[i].Starred) _entries.RemoveAt(i);
        }
    }

    public void SetLimit(int limit)
    {
        _limit = Math.Clamp(limit, 1, 10_000);
        Trim();
        TryPersist();
    }

    public List<HistoryEntry> Search(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return new List<HistoryEntry>(_entries);
        return _entries
            .Where(e => e.SourceText.ToLowerInvariant().Contains(q) || e.TargetText.ToLowerInvariant().Contains(q))
            .ToList();
    }

    public bool ToggleStar(long id)
    {
        var e = _entries.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("历史记录不存在");
        e.Starred = !e.Starred;
        TryPersist();
        return e.Starred;
    }

    public void Remove(long id)
    {
        var before = _entries.Count;
        _entries.RemoveAll(e => e.Id == id);
        if (_entries.Count == before) throw new InvalidOperationException("历史记录不存在");
        TryPersist();
    }

    /// <summary>只清未收藏的，收藏内容是用户明确留下的数据。</summary>
    public int ClearUnstarred()
    {
        var before = _entries.Count;
        _entries.RemoveAll(e => !e.Starred);
        TryPersist();
        return before - _entries.Count;
    }

    public string ExportJson() => JsonSerializer.Serialize(_entries, Json.Options);

    private void TryPersist()
    {
        try { Persist(); }
        catch (IOException) { /* 磁盘满之类的不该让翻译功能崩掉 */ }
    }

    private static string Truncate(string s) => s.Length <= MaxTextLen ? s : s[..MaxTextLen] + "…";
}

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public string SourceText { get; set; } = "";
    public string TargetText { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Engine { get; set; } = "";
    public long Timestamp { get; set; }
    public bool Starred { get; set; }
}
