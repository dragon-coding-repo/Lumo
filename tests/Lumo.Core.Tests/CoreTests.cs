using Lumo.Core;
using Lumo.Core.Engines;
using Xunit;

namespace Lumo.Core.Tests;

public class CacheTests
{
    private static TranslateRequest Req(string text, string to = "en") =>
        new() { Text = text, From = "auto", To = to };

    [Fact]
    public void MissThenHit()
    {
        var c = new TranslationCache(100, TimeSpan.FromHours(1));
        var k = TranslationCache.KeyOf("google", Req("hello"));
        Assert.Null(c.Get(k));
        c.Put(k, "你好");
        Assert.Equal("你好", c.Get(k));
        var s = c.Stats();
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.Misses);
    }

    [Fact]
    public void KeyIncludesEngineAndDirection()
    {
        var r = Req("hi");
        Assert.NotEqual(TranslationCache.KeyOf("google", r), TranslationCache.KeyOf("deepl", r));
        Assert.Equal(TranslationCache.KeyOf("google", r), TranslationCache.KeyOf("google", r));
    }

    [Fact]
    public void KeyDiffersByTargetLanguage() =>
        Assert.NotEqual(
            TranslationCache.KeyOf("g", Req("hi", "en")),
            TranslationCache.KeyOf("g", Req("hi", "zh")));

    [Fact]
    public void KeyIgnoresSurroundingWhitespace() =>
        Assert.Equal(
            TranslationCache.KeyOf("g", Req("hi")),
            TranslationCache.KeyOf("g", Req("  hi  ")));

    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        const int cap = 16;  // 容量下限被 clamp 到 16
        var c = new TranslationCache(cap, TimeSpan.FromHours(1));
        for (var i = 0; i < cap; i++) c.Put($"k{i}", $"v{i}");

        // 触碰 k0，让它从队尾变成最新
        Assert.Equal("v0", c.Get("k0"));
        c.Put("new", "v");

        Assert.Null(c.Get("k1"));                 // 最久未使用的应被淘汰
        Assert.Equal("v0", c.Get("k0"));          // 刚用过的不能被淘汰
        Assert.Equal("v", c.Get("new"));
        Assert.Equal(cap, c.Stats().Entries);
    }

    [Fact]
    public void NeverGrowsPastCapacity()
    {
        const int cap = 16;
        var c = new TranslationCache(cap, TimeSpan.FromHours(1));
        for (var i = 0; i < 1000; i++)
        {
            c.Put($"k{i}", $"v{i}");
            Assert.True(c.Stats().Entries <= cap, $"第 {i} 次写入后超容");
        }
    }

    [Fact]
    public void CapacityAndTtlAreClamped()
    {
        var tiny = new TranslationCache(0, TimeSpan.FromSeconds(1));
        tiny.Put("k", "v");
        // TTL 下限 1 分钟，立即查询应当还能命中
        Assert.Equal("v", tiny.Get("k"));
    }

    [Fact]
    public void ReinsertUpdatesValueWithoutDuplicating()
    {
        var c = new TranslationCache(10, TimeSpan.FromHours(1));
        c.Put("k", "v1");
        c.Put("k", "v2");
        Assert.Equal("v2", c.Get("k"));
        Assert.Equal(1, c.Stats().Entries);
    }

    [Fact]
    public void ClearZeroesCounters()
    {
        var c = new TranslationCache(10, TimeSpan.FromHours(1));
        c.Put("a", "1");
        c.Get("a");
        c.Get("a");
        c.Clear();
        var s = c.Stats();
        Assert.Equal((0L, 0L, 0, s.Bytes, 0.0), (s.Hits, s.Misses, s.Entries, s.Bytes, s.HitRate));
    }

    [Fact]
    public void HitRateIsZeroWhenNoRequests()
    {
        var s = new TranslationCache(10, TimeSpan.FromHours(1)).Stats();
        Assert.Equal(0.0, s.HitRate);
    }
}

public class ConfigTests
{
    private static string TempDir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"lumo-cfg-{tag}-{Environment.ProcessId}");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void DefaultsAreValid()
    {
        var c = new AppConfig().Sanitized();
        Assert.Equal(AppConfig.CurrentVersion, c.Version);
        Assert.Equal("zh", c.TargetLang);
        Assert.Equal(EngineIds.Edge, c.DefaultEngine);
    }

    [Fact]
    public void SanitizeRejectsAutoTarget() =>
        Assert.Equal("zh", new AppConfig { TargetLang = "auto" }.Sanitized().TargetLang);

    [Theory]
    [InlineData(5.0, 1.0)]
    [InlineData(0.0, 0.3)]
    [InlineData(0.65, 0.65)]
    [InlineData(double.NaN, 1.0)]
    public void SanitizeClampsOpacity(double input, double expected) =>
        Assert.Equal(expected, new AppConfig { Opacity = input }.Sanitized().Opacity, 3);

    [Fact]
    public void SanitizeClampsNumbers()
    {
        var s = new AppConfig
        {
            ClipboardDebounceMs = 99999, HistoryLimit = -5,
            CacheCapacity = 999999, CacheTtlHours = 0, HttpTimeoutSecs = 1,
        }.Sanitized();
        Assert.Equal(3000, s.ClipboardDebounceMs);
        Assert.Equal(0, s.HistoryLimit);
        Assert.Equal(50_000, s.CacheCapacity);
        Assert.Equal(1, s.CacheTtlHours);
        Assert.Equal(3, s.HttpTimeoutSecs);
    }

    [Fact]
    public void SanitizeRemovesSelfFromFallback()
    {
        var s = new AppConfig
        {
            DefaultEngine = EngineIds.Google,
            FallbackEngines = new() { EngineIds.Google, EngineIds.Bing, EngineIds.Google },
        }.Sanitized();
        Assert.Equal(new[] { EngineIds.Bing }, s.FallbackEngines);
    }

    [Fact]
    public void EngineChainStartsWithDefault()
    {
        var c = new AppConfig { DefaultEngine = EngineIds.DeepL, FallbackEngines = new() { EngineIds.Google } };
        Assert.Equal(new[] { EngineIds.DeepL, EngineIds.Google }, c.EngineChain());
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new ConfigStore(Path.Combine(TempDir("roundtrip"), "config.json"));
        var cfg = new AppConfig { TargetLang = "ja", DefaultEngine = EngineIds.Bing };
        cfg.EngineConfigs[EngineIds.DeepL] = new EngineConfig { Key = "k" };
        store.Save(cfg);

        var loaded = store.Load();
        Assert.Equal("ja", loaded.TargetLang);
        Assert.Equal(EngineIds.Bing, loaded.DefaultEngine);
        Assert.Equal("k", loaded.EngineConfigs[EngineIds.DeepL].Key);
    }

    [Fact]
    public void MissingFileReturnsDefaults()
    {
        var store = new ConfigStore(Path.Combine(TempDir("missing"), "nope.json"));
        Assert.Equal(EngineIds.Edge, store.Load().DefaultEngine);
    }

    [Fact]
    public void CorruptFileIsBackedUpNotLost()
    {
        var path = Path.Combine(TempDir("corrupt"), "config.json");
        File.WriteAllText(path, "{ this is not json");
        var cfg = new ConfigStore(path).Load();
        Assert.Equal(EngineIds.Edge, cfg.DefaultEngine);
        Assert.True(File.Exists(path + ".corrupt"), "损坏原文应留档");
    }

    [Fact]
    public void PartialConfigFillsMissingFields()
    {
        var path = Path.Combine(TempDir("partial"), "config.json");
        File.WriteAllText(path, """{"version":1,"targetLang":"fr"}""");
        var c = new ConfigStore(path).Load();
        Assert.Equal("fr", c.TargetLang);
        Assert.Equal(EngineIds.Edge, c.DefaultEngine);
        Assert.Equal(800, c.CacheCapacity);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var path = Path.Combine(TempDir("unknown"), "config.json");
        File.WriteAllText(path, """{"version":1,"targetLang":"de","futureField":123}""");
        Assert.Equal("de", new ConfigStore(path).Load().TargetLang);
    }

    [Fact]
    public void SaveLeavesNoTempFile()
    {
        var dir = TempDir("tmpfile");
        var store = new ConfigStore(Path.Combine(dir, "config.json"));
        store.Save(new AppConfig());
        Assert.True(File.Exists(Path.Combine(dir, "config.json")));
        Assert.False(File.Exists(Path.Combine(dir, "config.json.tmp")));
    }
}

public class HistoryTests
{
    private static HistoryStore Store(string tag, int limit) =>
        new(Path.Combine(Dir(tag), "history.json"), limit);

    private static string Dir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"lumo-hist-{tag}-{Environment.ProcessId}");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void AddAndList()
    {
        var s = Store("add", 10);
        var id = s.Add("hello", "你好", "en", "zh", "google");
        Assert.NotNull(id);
        Assert.Single(s.Entries);
        Assert.Equal("hello", s.Entries[0].SourceText);
    }

    [Fact]
    public void EmptyEntriesRejected()
    {
        var s = Store("empty", 10);
        Assert.Null(s.Add("", "你好", "en", "zh", "g"));
        Assert.Null(s.Add("hello", "  ", "en", "zh", "g"));
        Assert.Empty(s.Entries);
    }

    [Fact]
    public void DuplicateDoesNotCreateSecondEntry()
    {
        var s = Store("dup", 10);
        s.Add("hello", "你好", "en", "zh", "google");
        s.Add("hello", "你好", "en", "zh", "bing");
        Assert.Single(s.Entries);
    }

    [Fact]
    public void DuplicateUpdatesEngine()
    {
        var s = Store("dup2", 10);
        s.Add("hello", "你好", "en", "zh", "google");
        s.Add("hello", "你好", "en", "zh", "deepl");
        Assert.Equal("deepl", s.Entries[0].Engine);
    }

    [Fact]
    public void LimitTrimsOldest()
    {
        var s = Store("limit", 3);
        for (var i = 0; i < 10; i++) s.Add($"src{i}", $"dst{i}", "en", "zh", "g");
        Assert.Equal(3, s.Entries.Count);
        Assert.Equal("src9", s.Entries[0].SourceText);
    }

    [Fact]
    public void StarredEntriesSurviveTrimming()
    {
        var s = Store("star", 2);
        var id = s.Add("keep me", "保留", "en", "zh", "g");
        s.ToggleStar(id!.Value);
        for (var i = 0; i < 10; i++) s.Add($"filler{i}", $"填充{i}", "en", "zh", "g");
        Assert.Contains(s.Entries, e => e.SourceText == "keep me");
    }

    [Fact]
    public void ToggleStarFlips()
    {
        var s = Store("toggle", 10);
        var id = s.Add("x", "y", "en", "zh", "g")!.Value;
        Assert.True(s.ToggleStar(id));
        Assert.False(s.ToggleStar(id));
    }

    [Fact]
    public void SearchMatchesBothSides()
    {
        var s = Store("search", 10);
        s.Add("hello world", "你好世界", "en", "zh", "g");
        s.Add("goodbye", "再见", "en", "zh", "g");
        Assert.Single(s.Search("hello"));
        Assert.Single(s.Search("再见"));
        Assert.Equal(2, s.Search("").Count);
        Assert.Empty(s.Search("zzz"));
    }

    [Fact]
    public void SearchIsCaseInsensitive()
    {
        var s = Store("case", 10);
        s.Add("Hello", "你好", "en", "zh", "g");
        Assert.Single(s.Search("hello"));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var s = Store("del", 10);
        var a = s.Add("a", "甲", "en", "zh", "g")!.Value;
        s.Add("b", "乙", "en", "zh", "g");
        s.Remove(a);
        Assert.Single(s.Entries);
        Assert.Equal(1, s.ClearUnstarred());
        Assert.Empty(s.Entries);
    }

    [Fact]
    public void ClearKeepsStarred()
    {
        var s = Store("clear", 10);
        var id = s.Add("a", "甲", "en", "zh", "g")!.Value;
        s.ToggleStar(id);
        s.Add("b", "乙", "en", "zh", "g");
        s.ClearUnstarred();
        Assert.Single(s.Entries);
        Assert.Equal("a", s.Entries[0].SourceText);
    }

    [Fact]
    public void RemoveMissingIdThrows()
    {
        var s = Store("missing", 10);
        Assert.Throws<InvalidOperationException>(() => s.Remove(999));
        Assert.Throws<InvalidOperationException>(() => s.ToggleStar(999));
    }

    [Fact]
    public void SurvivesReopenWithUniqueIds()
    {
        var path = Path.Combine(Dir("persist"), "history.json");
        var s = new HistoryStore(path, 10);
        s.Add("a", "甲", "en", "zh", "g");
        // 用同一个路径重开，验证落盘和 id 单调性。
        // 注意不能用 Store()/Dir() 重算路径 —— 它们每次都会清空目录。
        var reopened = new HistoryStore(path, 10);
        Assert.Single(reopened.Entries);
        var newId = reopened.Add("b", "乙", "en", "zh", "g")!.Value;
        Assert.NotEqual(1, newId);
    }

    [Fact]
    public void SetLimitTrimsImmediately()
    {
        var s = Store("setlimit", 10);
        for (var i = 0; i < 10; i++) s.Add($"s{i}", $"t{i}", "en", "zh", "g");
        s.SetLimit(4);
        Assert.Equal(4, s.Entries.Count);
        Assert.Equal("s9", s.Entries[0].SourceText);
    }

    [Fact]
    public void SetLimitIsClamped()
    {
        var s = Store("clamp", 10);
        s.SetLimit(0);
        Assert.Equal(1, s.Limit);
        s.SetLimit(int.MaxValue);
        Assert.Equal(10_000, s.Limit);
    }

    [Fact]
    public void LongTextIsTruncated()
    {
        var s = Store("trunc", 10);
        s.Add(new string('a', 4500), "译文", "en", "zh", "g");
        Assert.True(s.Entries[0].SourceText.Length <= 4001);
        Assert.EndsWith("…", s.Entries[0].SourceText);
    }

    [Fact]
    public void ExportProducesValidJson()
    {
        var s = Store("export", 10);
        s.Add("a", "甲", "en", "zh", "g");
        using var doc = System.Text.Json.JsonDocument.Parse(s.ExportJson());
        Assert.Single(doc.RootElement.EnumerateArray().ToList());
    }

}

public class BmpTests
{
    /// <summary>纯色 BGRA 图。每个像素是 B,G,R,A 四个字节。</summary>
    private static byte[] Solid(byte b, byte g, byte r, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void SignatureAndLengthAgree()
    {
        var bmp = Bmp.EncodeBgra(Solid(1,1,1, 4, 4), 4, 4);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
    }

    [Fact]
    public void Declares24BitBiRgb()
    {
        var bmp = Bmp.EncodeBgra(Solid(0,0,0, 2, 2), 2, 2);
        Assert.Equal(1, BitConverter.ToUInt16(bmp, 26));   // planes
        Assert.Equal(24, BitConverter.ToUInt16(bmp, 28));  // bpp
        Assert.Equal(0, BitConverter.ToInt32(bmp, 30));    // BI_RGB
    }

    [Fact]
    public void PixelOffsetPointsPastBothHeaders()
    {
        var bmp = Bmp.EncodeBgra(Solid(0,0,0, 4, 4), 4, 4);
        var offset = BitConverter.ToInt32(bmp, 10);
        Assert.Equal(54, offset);
        var imageSize = BitConverter.ToInt32(bmp, 34);
        Assert.Equal(bmp.Length, offset + imageSize);
    }

    [Fact]
    public void ConvertsChannelOrderAndFlipsRows()
    {
        // 2x2：上排红，下排绿。像素是 BGRA 序，所以红 = B0 G0 R255。
        var px = new byte[16];
        new byte[] { 0, 0, 255, 255 }.CopyTo(px, 0);      // 上左：红
        new byte[] { 0, 0, 0, 255 }.CopyTo(px, 4);        // 上右：黑
        new byte[] { 0, 255, 0, 255 }.CopyTo(px, 8);      // 下左：绿
        new byte[] { 0, 0, 0, 255 }.CopyTo(px, 12);       // 下右：黑

        var bmp = Bmp.EncodeBgra(px, 2, 2);
        // 2 像素 = 6 字节，但 BMP 要求每行 4 字节对齐，实际行距是 8
        const int off = 54, row = 8;
        // 第一数据行 = 图像最后一行 = 绿，且通道序为 BGR
        Assert.Equal(new byte[] { 0, 255, 0 }, bmp[off..(off + 3)]);
        // 第二数据行 = 图像第一行 = 红
        Assert.Equal(new byte[] { 0, 0, 255 }, bmp[(off + row)..(off + row + 3)]);
    }

    [Fact]
    public void DropsAlphaChannel()
    {
        var bmp = Bmp.EncodeBgra(Solid(10, 20, 30, 1, 1), 1, 1);
        Assert.Equal(new byte[] { 10, 20, 30 }, bmp[54..57]);
        Assert.Equal(0, bmp[57]);  // 对齐填充
    }

    [Fact]
    public void PadsRowsToFourBytes()
    {
        var bmp = Bmp.EncodeBgra(Solid(7,7,7, 3, 2), 3, 2);
        Assert.Equal(54 + 12 * 2, bmp.Length);
        Assert.Equal(new byte[] { 0, 0, 0 }, bmp[63..66]);
    }

    [Fact]
    public void RejectsBadInput()
    {
        Assert.Throws<TranslateException>(() => Bmp.EncodeBgra(Array.Empty<byte>(), 0, 4));
        Assert.Throws<TranslateException>(() => Bmp.EncodeBgra(Array.Empty<byte>(), 4, 0));
        Assert.Throws<TranslateException>(() => Bmp.EncodeBgra(new byte[10], 4, 4));
    }

    [Fact]
    public void HandlesNonSquareImage()
    {
        var bmp = Bmp.EncodeBgra(Solid(1,1,1, 7, 3), 7, 3);
        Assert.Equal(54 + 24 * 3, bmp.Length);
        Assert.Equal(7, BitConverter.ToInt32(bmp, 18));
        Assert.Equal(3, BitConverter.ToInt32(bmp, 22));
    }

    [Fact]
    public void ToleratesExtraTrailingPixels()
    {
        var px = Solid(5,5,5, 2, 2).Concat(new byte[64]).ToArray();
        Assert.Equal(54 + 8 * 2, Bmp.EncodeBgra(px, 2, 2).Length);
    }
}

public class RegionTests
{
    [Fact]
    public void RejectsDegenerateSizes()
    {
        Assert.False(new Region(0, 0, 4, 4).IsPlausible);
        Assert.False(new Region(0, 0, 100, 2).IsPlausible);
        Assert.True(new Region(0, 0, 100, 40).IsPlausible);
    }

    [Fact]
    public void PixelsDoesNotOverflow() =>
        // 100000 x 100000 在 int 下必然溢出，必须用 long
        Assert.Equal(10_000_000_000L, new Region(0, 0, 100_000, 100_000).Pixels);

    [Fact]
    public void FlagsOversizedRegions()
    {
        Assert.False(new Region(0, 0, 1920, 1080).IsOversized);
        Assert.True(new Region(0, 0, 5000, 5000).IsOversized);
    }
}

public class TranslatorTests
{
    [Fact]
    public void EmptyTextRejected()
    {
        var t = new Translator(new AppConfig());
        var ex = Assert.ThrowsAsync<TranslateException>(() => t.TranslateAsync("   ", "auto", "zh")).Result;
        Assert.Equal("config", ex.Kind);
    }

    [Fact]
    public async Task SameLanguageShortCircuitsWithoutNetwork()
    {
        var t = new Translator(new AppConfig { SourceLang = "zh", TargetLang = "zh" });
        var out_ = await t.TranslateAsync("你好世界", "zh", "zh");
        // 不该有任何网络请求，Attempts 为空
        Assert.Empty(out_.Attempts);
        Assert.Equal("none", out_.Engine);
        Assert.Null(out_.TargetText);
        Assert.Contains("相同", out_.Note);
    }

    [Fact]
    public async Task SameLanguageStillShortCircuitsWithoutAutoCopy()
    {
        // 默认配置（AutoCopy=false）不该发请求
        var t = new Translator(new AppConfig());
        var out_ = await t.TranslateAsync("你好世界", "zh", "zh");
        Assert.Equal("none", out_.Engine);
        Assert.Empty(out_.Attempts);
    }

    [Theory]
    [InlineData("zh", "en")]
    [InlineData("ja", "en")]
    [InlineData("en", "zh")]
    [InlineData("ru", "zh")]
    public void OppositeMappingCoversMainPairs(string from, string expected) =>
        Assert.Equal(expected, Translator.OppositeOf(from));

    [Fact]
    public void UndetectableLanguageDoesNotGuess() =>
        Assert.Null(Translator.OppositeOf("hi"));

    [Fact]
    public void UpdateConfigPreservesDefaults()
    {
        var t = new Translator(new AppConfig());
        t.UpdateConfig(new AppConfig { CacheCapacity = 500 });
        Assert.Equal(500, t.Config.CacheCapacity);
    }

    [Fact]
    public void ClearCacheResetsStats()
    {
        var t = new Translator(new AppConfig());
        t.ClearCache();
        Assert.Equal(0, t.CacheStats().Entries);
    }

    [Fact]
    public void AllListedEnginesConstructible()
    {
        foreach (var info in EngineFactory.Available)
        {
            var engine = EngineFactory.Create(info.Id, new EngineConfig());
            Assert.NotNull(engine);
            Assert.Equal(info.Id, engine!.Id);
            Assert.False(string.IsNullOrEmpty(engine.DisplayName));
        }
    }

    [Fact]
    public void UnknownEngineIdReturnsNull() => Assert.Null(EngineFactory.Create("does-not-exist", new EngineConfig()));
}

public class ErrorAggregationTests
{
    /// <summary>空 body 的失败要给出能操作的说法，而不是「取必应令牌失败：」。</summary>
    [Fact]
    public void EmptyErrorGetsActionableText()
    {
        var method = typeof(Translator).GetMethod("Describe",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var ex = new TranslateException("http", "取必应令牌失败：");
        var text = (string)method!.Invoke(null, new object[] { ex })!;
        Assert.Contains("服务无响应", text);
        Assert.DoesNotContain("：\r", text);
    }

    [Fact]
    public void NonEmptyErrorIsPreserved()
    {
        var method = typeof(Translator).GetMethod("Describe",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var ex = new TranslateException("http", "HTTP 429：Too Many Requests");
        var text = (string)method!.Invoke(null, new object[] { ex })!;
        Assert.Contains("429", text);
        Assert.Contains("Too Many Requests", text);
    }

    [Fact]
    public void SingleFailureNamesTheEngine()
    {
        var method = typeof(Translator).GetMethod("SummarizeFailures",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var failures = new List<(string, TranslateException)>
        {
            ("bing", new TranslateException("http", "取必应令牌失败：")),
        };
        var text = (string)method!.Invoke(null, new object[] { failures })!;
        Assert.Contains("bing", text);
        Assert.Contains("服务无响应", text);
    }

    [Fact]
    public void MultipleFailuresListEveryEngine()
    {
        // 用户实测场景：Google 先失败被吞掉，只看到 Bing 的空壳报错
        var method = typeof(Translator).GetMethod("SummarizeFailures",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var failures = new List<(string, TranslateException)>
        {
            ("google", new TranslateException("network", "请求超时")),
            ("bing", new TranslateException("http", "取必应令牌失败：")),
        };
        var text = (string)method!.Invoke(null, new object[] { failures })!;
        Assert.Contains("google", text);
        Assert.Contains("请求超时", text);
        Assert.Contains("bing", text);
        Assert.Contains("全部 2 个引擎都失败", text);
        Assert.Contains("设置", text);   // 给出下一步
    }
}
