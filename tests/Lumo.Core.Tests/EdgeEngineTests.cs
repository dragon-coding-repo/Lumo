using System.Text;
using Lumo.Core;
using Lumo.Core.Engines;
using Xunit;

namespace Lumo.Core.Tests;

/// <summary>
/// Edge 引擎。端点是 2026-08 换过的，用真实响应样本钉住请求构造和解析。
/// </summary>
public class EdgeEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    [Fact]
    public void UsesTheNewNoAuthEndpoint()
    {
        var call = new EdgeEngine(new EngineConfig()).Build(Req("en", "zh", "hello"));
        // 旧端点 translate/auth 已被微软下线（固定 404），这里必须用 translatetext
        Assert.StartsWith("https://edge.microsoft.com/translate/translatetext", call.Url);
        Assert.DoesNotContain("translate/auth", call.Url);
    }

    [Fact]
    public void SendsBrowserUserAgent()
    {
        // 这个端点强制要求浏览器 UA，缺了会 403
        var call = new EdgeEngine(new EngineConfig()).Build(Req("en", "zh", "hello"));
        Assert.True(call.Headers.TryGetValue("user-agent", out var ua));
        Assert.Contains("Mozilla/5.0", ua);
    }

    [Fact]
    public void BodyIsStringArrayNotObject()
    {
        // 这个接口的请求体是 ["text"]，写成 {"text": ...} 会被拒
        var call = new EdgeEngine(new EngineConfig()).Build(Req("en", "zh", "hello"));
        Assert.Equal("[\"hello\"]", call.Body);
    }

    [Fact]
    public void AutoSourceOmitsFromParameter()
    {
        var call = new EdgeEngine(new EngineConfig()).Build(Req("auto", "zh", "hello"));
        Assert.DoesNotContain("from=", call.Url);
    }

    [Fact]
    public void ExplicitSourceIsSent()
    {
        var call = new EdgeEngine(new EngineConfig()).Build(Req("en", "zh", "hello"));
        Assert.Contains("from=en", call.Url);
    }

    [Fact]
    public void TargetIsMappedToEdgeCode()
    {
        var zh = new EdgeEngine(new EngineConfig()).Build(Req("en", "zh", "x"));
        Assert.Contains("to=zh-Hans", zh.Url);
        var ja = new EdgeEngine(new EngineConfig()).Build(Req("en", "ja", "x"));
        Assert.Contains("to=ja", ja.Url);
    }

    [Fact]
    public void RejectsEmptyAndOversizedText()
    {
        var e = new EdgeEngine(new EngineConfig());
        Assert.Throws<TranslateException>(() => e.Build(Req("en", "zh", "  ")));
        Assert.Throws<TranslateException>(() => e.Build(Req("en", "zh", new string('a', 20001))));
    }

    // ↓ 下面是真实接口的响应样本（我实测抓的）

    [Fact]
    public void ParsesRealResponse()
    {
        const string body =
            """[{"translations":[{"text":"大家好，这是一次翻译测试。","to":"zh-Hans","sentLen":{"srcSentLen":[40],"transSentLen":[13]}}]}]""";
        var r = new EdgeEngine(new EngineConfig()).Parse(body);
        Assert.Equal("大家好，这是一次翻译测试。", r.Text);
        Assert.Null(r.DetectedFrom);
    }

    [Fact]
    public void ParsesAutoDetectedLanguage()
    {
        // 不传 from 时服务端会回 detectedLanguage
        const string body =
            """[{"detectedLanguage":{"language":"zh-Hans","score":1.0},"translations":[{"text":"The weather is nice today.","to":"en","sentLen":{"srcSentLen":[7],"transSentLen":[26]}}]}]""";
        var r = new EdgeEngine(new EngineConfig()).Parse(body);
        Assert.Equal("The weather is nice today.", r.Text);
        // 内部码要归一化：服务端给 zh-Hans，内部用 zh
        Assert.Equal("zh", r.DetectedFrom);
    }

    [Fact]
    public void NormalizesEdgeLanguageCodes()
    {
        Assert.Equal("zh", EdgeEngine.NormalizeEdgeCode("zh-Hans"));
        Assert.Equal("zh", EdgeEngine.NormalizeEdgeCode("zh-CHS"));
        Assert.Equal("zh-Hant", EdgeEngine.NormalizeEdgeCode("zh-CHT"));
        Assert.Equal("en", EdgeEngine.NormalizeEdgeCode("en"));
        Assert.Null(EdgeEngine.NormalizeEdgeCode(null));
    }

    [Fact]
    public void RejectsEmptyArray()
    {
        var e = new EdgeEngine(new EngineConfig());
        Assert.Throws<TranslateException>(() => e.Parse("[]"));
        Assert.Throws<TranslateException>(() => e.Parse("""[{"translations":[]}]"""));
    }

    [Fact]
    public void InvalidJsonBecomesParseError()
    {
        var e = new EdgeEngine(new EngineConfig());
        var ex = Assert.Throws<TranslateException>(() => e.Parse("<html>blocked</html>"));
        Assert.Equal("parse", ex.Kind);
    }

    [Fact]
    public void IsRegisteredAndIsTheDefault()
    {
        Assert.NotNull(EngineFactory.Create(EngineIds.Edge, new EngineConfig()));
        Assert.Equal(EngineIds.Edge, new AppConfig().DefaultEngine);
        // 免 Key
        Assert.False(new EdgeEngine(new EngineConfig()).RequiresKey);
    }

    [Fact]
    public void AppearsFirstInEngineList()
    {
        // 默认引擎排在第一个，设置页的默认选中项就是它
        Assert.Equal(EngineIds.Edge, EngineFactory.Available[0].Id);
    }
}

public class ConfigMigrationTests
{
    private static ConfigStore StoreFor(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"lumo-mig-{tag}-{Environment.ProcessId}");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return new ConfigStore(Path.Combine(d, "config.json"));
    }

    [Fact]
    public void V1DefaultConfigMigratesToEdge()
    {
        // v1 存下来的就是默认配置，而那两个引擎都已失效
        var store = StoreFor("v1default");
        File.WriteAllText(store.Path, """{"version":1,"defaultEngine":"google","fallbackEngines":["bing"]}""");
        var cfg = store.Load();
        Assert.Equal(EngineIds.Edge, cfg.DefaultEngine);
        Assert.DoesNotContain(EngineIds.Bing, cfg.FallbackEngines);
        Assert.Contains(EngineIds.Google, cfg.FallbackEngines);   // 降级为备选
        Assert.Equal(AppConfig.CurrentVersion, cfg.Version);
    }

    [Fact]
    public void V1WithDeadBingMigratesToEdge()
    {
        var store = StoreFor("v1bing");
        File.WriteAllText(store.Path, """{"version":1,"defaultEngine":"bing"}""");
        Assert.Equal(EngineIds.Edge, store.Load().DefaultEngine);
    }

    [Fact]
    public void UserConfiguredKeyedEngineIsNotOverwritten()
    {
        // 用户主动配了 DeepL / OpenAI / 自定义，那是明确选择，不能动
        foreach (var id in new[] { EngineIds.DeepL, EngineIds.OpenAi, EngineIds.Custom })
        {
            var store = StoreFor("v1keep-" + id);
            File.WriteAllText(store.Path, $$"""{"version":1,"defaultEngine":"{{id}}"}""");
            Assert.Equal(id, store.Load().DefaultEngine);
        }
    }

    [Fact]
    public void V2ConfigIsLeftAlone()
    {
        // 已经是新版本的用户不该被迁移覆盖 —— 比如他主动选了 Google + 代理
        var store = StoreFor("v2");
        File.WriteAllText(store.Path, """{"version":2,"defaultEngine":"google"}""");
        Assert.Equal(EngineIds.Google, store.Load().DefaultEngine);
    }
}

/// <summary>
/// Content-Type 双发回归。
/// </summary>
/// <remarks>
/// 之前 <c>TranslateEngineBase</c> 往 <c>request.Headers</c> 里塞 content-type，
/// 同时 <c>StringContent</c> 又自带一个，于是 .NET 发出畸形/重复的内容头，
/// 服务端模型绑定失败 —— Edge 端点返回 400 "The input was not valid"。
/// 所有走 body 的引擎（Edge / DeepL / OpenAI / 自定义）全部中招。
/// 这些测试确保 content-type 永远不会回到 Headers 里。
/// </remarks>
public class ContentTypeTests
{
    private static TranslateRequest Req(string from, string to) =>
        new() { Text = "Hello", From = from, To = to };

    [Fact]
    public void WithHeaderRoutesContentTypeToItsOwnField()
    {
        var call = HttpCall.Post("https://x.test").WithHeader("content-type", "application/json");
        Assert.False(call.Headers.ContainsKey("content-type"),
            "content-type 不能留在请求头里，会和服务端期望的内容头冲突");
        Assert.Equal("application/json", call.ContentType);
    }

    [Fact]
    public void WithHeaderIsCaseInsensitiveForContentType()
    {
        foreach (var name in new[] { "content-type", "Content-Type", "CONTENT-TYPE" })
        {
            var call = HttpCall.Post("https://x.test").WithHeader(name, "application/json");
            Assert.False(call.Headers.ContainsKey(name));
        }
    }

    [Fact]
    public void NoEnginePutsContentTypeInRequestHeaders()
    {
        // 逐个引擎跑一遍，确保没有一个把 content-type 漏进 Headers
        foreach (var id in EngineFactory.Available.Select(e => e.Id))
        {
            var engine = EngineFactory.Create(id, new EngineConfig { Key = "k" });
            if (engine is null) continue;
            HttpCall call;
            try { call = engine.Build(Req("en", "zh")); }
            catch (TranslateException) { continue; }   // 缺 Key 之类，跳过
            Assert.False(call.Headers.ContainsKey("content-type"),
                $"引擎 {id} 把 content-type 放进了请求头，会触发 400");
        }
    }

    [Fact]
    public void BodyEnginesSetContentTypeProperly()
    {
        // 自定义引擎只有配了 body 模板才会有 body，这里得给上
        var configs = new Dictionary<string, EngineConfig>
        {
            [EngineIds.Edge] = new() { Key = "k" },
            [EngineIds.DeepL] = new() { Key = "k" },
            [EngineIds.OpenAi] = new() { Key = "k" },
            [EngineIds.Custom] = new()
            {
                Key = "k",
                Extra = { ["url"] = "https://api.test/t", ["body"] = """{"q":"{{text}}"}""" },
            },
        };

        foreach (var (id, cfg) in configs)
        {
            var call = EngineFactory.Create(id, cfg)!.Build(Req("en", "zh"));
            Assert.NotNull(call.Body);
            Assert.False(string.IsNullOrEmpty(call.ContentType), $"引擎 {id} 有 body 却没设 ContentType");
        }
    }

    [Fact]
    public void BodyBytesAreUtf8()
    {
        var call = HttpCall.Post("https://x.test").WithJsonBody(new[] { "你好" });
        Assert.Equal(call.Body!.Length, call.BodyBytes.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(call.Body), call.BodyBytes);
    }

    [Fact]
    public void GetOnlyCallsHaveNoBodyOrContentType()
    {
        var call = new GoogleEngine(new EngineConfig()).Build(Req("en", "zh"));
        Assert.Null(call.Body);
        Assert.Null(call.ContentType);
    }
}

/// <summary>
/// 验证「真正发出去的请求」长什么样。
/// </summary>
/// <remarks>
/// 之前只测了 HttpCall 的字段结构，结果 content-type 双发这个 bug 漏了过去：
/// 结构测试全绿，但实际请求是坏的。这里直接检查 HttpRequestMessage。
/// </remarks>
public class OutgoingRequestTests
{
    private static HttpRequestMessage BuildMessage(string engineId, EngineConfig? cfg = null)
    {
        var engine = EngineFactory.Create(engineId, cfg ?? new EngineConfig { Key = "k" })!;
        var req = new TranslateRequest { Text = "Hello world", From = "en", To = "zh" };
        return TranslateEngineBase.CreateMessage(engine.Build(req));
    }

    /// <summary>
    /// 安全地判断请求头里有没有 content-type。
    /// </summary>
    /// <remarks>
    /// 不能直接用 <c>m.Headers.Contains("content-type")</c> ——
    /// .NET 禁止在请求头集合里查询内容头，会直接抛 InvalidOperationException。
    /// 这也正是之前 <c>TryAddWithoutValidation</c> 没能正确拦截、
    /// 导致 400 漏过去的原因之一。
    /// </remarks>
    private static bool HasContentTypeInRequestHeaders(HttpRequestMessage m)
    {
        try { return m.Headers.Contains("content-type"); }
        catch (InvalidOperationException) { return false; }   // .NET 判定它不是请求头
    }

    private static bool RequestHeadersContainContentTypeText(HttpRequestMessage m) =>
        m.Headers.ToString().Contains("content-type", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(EngineIds.Edge)]
    [InlineData(EngineIds.DeepL)]
    [InlineData(EngineIds.OpenAi)]
    public void ContentTypeIsNeverInRequestHeaders(string id)
    {
        using var m = BuildMessage(id);
        Assert.False(HasContentTypeInRequestHeaders(m),
            $"{id}: content-type 出现在请求头里会和服务端期望冲突（Edge 实测返回 400）");
        Assert.False(RequestHeadersContainContentTypeText(m),
            $"{id}: 请求头文本里不该出现 content-type");
    }

    [Theory]
    // 各引擎的编码方式本来就不同：Edge/OpenAI 走 JSON，DeepL 走表单。
    // 这里断言「实际发出去的类型」和「引擎声明的类型」一致，而不是写死 JSON。
    [InlineData(EngineIds.Edge, "application/json")]
    [InlineData(EngineIds.OpenAi, "application/json")]
    [InlineData(EngineIds.DeepL, "application/x-www-form-urlencoded")]
    public void ContentTypeIsSetOnTheContent(string id, string expected)
    {
        using var m = BuildMessage(id);
        Assert.NotNull(m.Content);
        Assert.NotNull(m.Content!.Headers.ContentType);
        Assert.Equal(expected, m.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", m.Content.Headers.ContentType.CharSet);
    }

    [Fact]
    public void ContentTypeAppearsExactlyOnce()
    {
        // 双发是这次的根因，必须保证只出现一次
        using var m = BuildMessage(EngineIds.Edge);
        var inHeaders = HasContentTypeInRequestHeaders(m) || RequestHeadersContainContentTypeText(m);
        var inContent = m.Content?.Headers.ContentType is not null;
        Assert.True(inContent);
        Assert.False(inHeaders);
        Assert.True(inHeaders ^ inContent, "content-type 必须且只能出现在一处");
    }

    [Fact]
    public void BodyMatchesContentLength()
    {
        // StringContent 会把 charset 一起写进 Content-Type；
        // ByteArrayContent + 显式 ContentType 才是正确做法。
        using var m = BuildMessage(EngineIds.Edge);
        Assert.Equal("""["Hello world"]""", m.Content!.ReadAsStringAsync().Result);
        Assert.Equal(15L, m.Content.Headers.ContentLength);
    }

    [Fact]
    public void CustomEngineAlsoRoutesContentTypeProperly()
    {
        var cfg = new EngineConfig
        {
            Key = "k",
            Extra = { ["url"] = "https://api.test/t", ["body"] = """{"q":"{{text}}"}""" },
        };
        using var m = BuildMessage(EngineIds.Custom, cfg);
        Assert.False(HasContentTypeInRequestHeaders(m));
        Assert.Equal("application/json", m.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void CustomEngineRespectsUserSuppliedContentType()
    {
        var cfg = new EngineConfig
        {
            Extra =
            {
                ["url"] = "https://api.test/t",
                ["body"] = """{"q":"{{text}}"}""",
                ["headers"] = "Content-Type: application/x-www-form-urlencoded",
            },
        };
        using var m = BuildMessage(EngineIds.Custom, cfg);
        Assert.Equal("application/x-www-form-urlencoded", m.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void GetOnlyRequestHasNoContent()
    {
        var engine = new GoogleEngine(new EngineConfig());
        var req = new TranslateRequest { Text = "Hello", From = "en", To = "zh" };
        using var m = TranslateEngineBase.CreateMessage(engine.Build(req));
        Assert.Null(m.Content);
    }
}
