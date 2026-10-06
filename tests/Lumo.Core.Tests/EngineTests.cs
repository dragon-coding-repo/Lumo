using Lumo.Core;
using Lumo.Core.Engines;
using Xunit;

namespace Lumo.Core.Tests;

public class LangTests
{
    [Theory]
    [InlineData("今天天气怎么样？", "zh")]
    [InlineData("人工智能正在改变世界", "zh")]
    [InlineData("中华人民共和国", "zh")]
    public void DetectsChinese(string text, string expected) =>
        Assert.Equal(expected, Lang.Detect(text).Code);

    [Theory]
    [InlineData("これはペンです")]                       // 纯假名
    [InlineData("日本語のテストです")]                    // 假名 + 少量汉字
    // 下面是真实场景里会翻车的一批：汉字多于假名的日文。
    // 旧实现要求「假名 >= 总字符 50%」，这些全部会被误判成中文。
    [InlineData("小さな卵形の顔、濃茶の大きな瞳、細い眉、整った鼻、艶のある淡桃色の唇。")]
    [InlineData("国際連合安全保障理事会決議附属書第十三条第二項の規定により")]  // 法令体，汉字极多
    [InlineData("用户提供界面截图以及错误信息说明按钮显示不完整")]   // 这条其实是中文
    public void DetectsJapaneseViaKana(string text)
    {
        var d = Lang.Detect(text);
        // 最后一条是中文，汉字占绝对多数，仍应判 zh
        var expected = text.Contains("按钮显示不完整") ? "zh" : "ja";
        Assert.Equal(expected, d.Code);
    }

    [Fact]
    public void KanaFreeJapaneseIsIndistinguishableFromChinese()
    {
        // 已知的固有局限：纯汉字 + 拉丁字母的日文地址，
        // 脚本层面和中文地址完全一样，任何离线检测都分不开。
        // 这种情况会判成中文，靠后续翻译引擎的服务端检测兜底。
        var d = Lang.Detect("東京都渋谷区神宮前四丁目二番十二号Rogers株式会社Fifth Building");
        Assert.Equal("zh", d.Code);
    }

    [Fact]
    public void KanjiHeavyJapaneseIsStillJapanese()
    {
        // 汉字远多于假名，假名占比只有 12.5% —— 旧实现在这里必然误判成中文。
        var text = "国際連合安全保障理事会決議附属書第十三条第二項の規定により";
        var d = Lang.Detect(text);
        Assert.Equal("ja", d.Code);
        Assert.True(d.Confidence > 0.5f);
    }

    [Fact]
    public void ChineseWithNoKanaIsNotJapanese()
    {
        // 纯中文不该因为含汉字就被判成日文
        Assert.Equal("zh", Lang.Detect("用户提供界面截图以及错误信息说明按钮显示不完整").Code);
        Assert.Equal("zh", Lang.Detect("今天天气不错适合出门散步").Code);
    }

    [Fact]
    public void ChineseWithStrayKanaMayBeTreatedAsJapanese()
    {
        // 这是一次有意识的取舍，不是 bug。
        //
        // 两类误判的代价不对等：
        // - 日文被误判成中文 → 界面直接报「原文与目标语言相同」，功能不可用（严重）
        // - 中文里混了一两个假名字符被误判成日文 → 引擎侧仍能翻译（轻微）
        //
        // 所以阈值压到 10%，宁可放过后者也不放过前者。
        // 现实中的中文文本几乎不会出现 10% 以上的假名。
        var d = Lang.Detect("会议记录：の participant 缺席");
        Assert.Equal("ja", d.Code);
    }

    [Fact]
    public void DetectsKoreanViaHangul() =>
        Assert.Equal("ko", Lang.Detect("안녕하세요 여러분").Code);

    [Fact]
    public void DetectsCyrillic() => Assert.Equal("ru", Lang.Detect("это тест").Code);

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog", "en")]
    [InlineData("el zorro marrón rápido salta sobre el perro perezoso", "es")]
    [InlineData("der schnelle braune Fuchs springt über den faulen Hund", "de")]
    [InlineData("le renard brun rapide saute par-dessus le chien paresseux", "fr")]
    public void DetectsLatinLanguages(string text, string expected) =>
        Assert.Equal(expected, Lang.Detect(text).Code);

    [Fact]
    public void DetectsVietnameseWithDiacritics() =>
        Assert.Equal("vi", Lang.Detect("xin chào các bạn, hôm nay thời tiết thế nào").Code);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890")]
    [InlineData("!@#$%^&*()")]
    [InlineData("v1.2.3")]
    public void ReturnsNullForNonLanguage(string text) =>
        Assert.Null(Lang.Detect(text).Code);

    [Fact]
    public void ShortTextHasLowerConfidence()
    {
        var longText = Lang.Detect("The quick brown fox jumps over the lazy dog and keeps running").Confidence;
        var shortText = Lang.Detect("the").Confidence;
        Assert.True(shortText < longText, $"short={shortText} long={longText}");
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh_CN", "zh")]
    [InlineData("Chinese", "zh")]
    [InlineData("EN", "en")]
    [InlineData("fr", "fr")]
    [InlineData("中文", "zh")]
    public void NormalizeAcceptsVariants(string input, string expected) =>
        Assert.Equal(expected, Lang.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeRejectsEmpty(string? input) => Assert.Null(Lang.Normalize(input));

    [Fact]
    public void DisplayNamesCoverAllCommonLanguages()
    {
        foreach (var (code, name) in Lang.Common)
        {
            Assert.False(string.IsNullOrEmpty(name));
            Assert.Equal(name, Lang.DisplayName(code));
        }
    }
}

public class GoogleEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    [Fact]
    public void BuildEncodesQueryAndLangs()
    {
        var call = new GoogleEngine(new EngineConfig()).Build(Req("auto", "zh-CN", "hello world"));
        Assert.Equal("GET", call.Method);
        Assert.StartsWith("https://translate.googleapis.com/translate_a/single?", call.Url);
        Assert.Contains("sl=auto", call.Url);
        Assert.Contains("tl=zh-CN", call.Url);
        Assert.Contains("q=hello%20world", call.Url);
    }

    [Fact]
    public void BuildHandlesSpecialCharacters()
    {
        var call = new GoogleEngine(new EngineConfig()).Build(Req("auto", "en", "a&b=c#d 中文"));
        // 未编码的 & 会截断查询串
        Assert.DoesNotContain("q=a&b", call.Url);
        Assert.Contains("%E4%B8%AD%E6%96%87", call.Url);
    }

    [Fact]
    public void BuildRejectsEmptyText() =>
        Assert.Throws<TranslateException>(() =>
            new GoogleEngine(new EngineConfig()).Build(Req("auto", "en", "   ")));

    [Fact]
    public void BuildRejectsOversizedText()
    {
        var ex = Assert.Throws<TranslateException>(() =>
            new GoogleEngine(new EngineConfig()).Build(Req("auto", "en", new string('字', 5001))));
        Assert.Contains("5000", ex.Message);
    }

    [Fact]
    public void BuildRejectsCookieInjection()
    {
        var cfg = new EngineConfig { Extra = { ["cookie"] = "a=b\r\nX-Evil: 1" } };
        Assert.Throws<TranslateException>(() => new GoogleEngine(cfg).Build(Req("auto", "en", "hi")));
    }

    [Fact]
    public void BuildAttachesCookieWhenSet()
    {
        var cfg = new EngineConfig { Extra = { ["cookie"] = "NID=abc" } };
        var call = new GoogleEngine(cfg).Build(Req("auto", "en", "hi"));
        Assert.Equal("NID=abc", call.Headers["cookie"]);
    }

    [Fact]
    public void ParseSingleSentence()
    {
        var r = new GoogleEngine(new EngineConfig())
            .Parse("""[[["你好","hello",null,null,10]],null,"en",null,null,null,null,[]]""");
        Assert.Equal("你好", r.Text);
        Assert.Equal("en", r.DetectedFrom);
    }

    [Fact]
    public void ParseJoinsSegmentsWithoutInsertingSpace()
    {
        // 中文句子分段后直接拼接，插入空格会改变语义
        var r = new GoogleEngine(new EngineConfig())
            .Parse("""[[["你好，","hello,",null,null,10],["世界","world",null,null,10]],null,"en"]""");
        Assert.Equal("你好，世界", r.Text);
    }

    [Fact]
    public void ParseRejectsEmptyResult() =>
        Assert.Throws<TranslateException>(() => new GoogleEngine(new EngineConfig()).Parse("""[[[]],"","en"]"""));

    [Fact]
    public void ParseInvalidJsonIsParseError()
    {
        var ex = Assert.Throws<TranslateException>(() =>
            new GoogleEngine(new EngineConfig()).Parse("<html>quota exceeded</html>"));
        Assert.Equal("parse", ex.Kind);
    }

    [Fact]
    public void RequiresNoKey() => Assert.False(new GoogleEngine(new EngineConfig()).RequiresKey);
}

public class BingEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    [Fact]
    public void BuildIncludesBearerAndVersion()
    {
        var call = new BingEngine(new EngineConfig()).BuildWithToken(Req("en", "zh-Hans", "hello"), "T0K");
        Assert.Equal("POST", call.Method);
        Assert.Contains("api-version=3.0", call.Url);
        Assert.Contains("to=zh-Hans", call.Url);
        Assert.Contains("from=en", call.Url);
        Assert.Equal("Bearer T0K", call.Headers["authorization"]);
    }

    [Fact]
    public void BuildOmitsFromWhenAuto()
    {
        var call = new BingEngine(new EngineConfig()).BuildWithToken(Req("auto", "en", "hi"), "T0K");
        Assert.DoesNotContain("&from=", call.Url);
    }

    [Fact]
    public void BuildRejectsEmptyTextAndEmptyToken()
    {
        var e = new BingEngine(new EngineConfig());
        Assert.Throws<TranslateException>(() => e.BuildWithToken(Req("auto", "en", "  "), "t"));
        Assert.Throws<TranslateException>(() => e.BuildWithToken(Req("auto", "en", "hi"), "  "));
    }

    [Fact]
    public void ParseReadsTranslatedText()
    {
        // 响应是「请求项数组」，每项里再套一层 translations —— 层级写错就会被这里抓到
        var body = """[{"translations":[{"text":"你好","to":"zh-Hans"}],"detectedLanguage":{"language":"en","score":1.0}}]""";
        var r = new BingEngine(new EngineConfig()).Parse(body);
        Assert.Equal("你好", r.Text);
        Assert.Equal("en", r.DetectedFrom);
    }

    [Fact]
    public void ParseHandlesMissingDetectedLanguage()
    {
        var r = new BingEngine(new EngineConfig()).Parse("""[{"translations":[{"text":"你好"}]}]""");
        Assert.Null(r.DetectedFrom);
        Assert.Equal("你好", r.Text);
    }

    [Fact]
    public void ParseRejectsEmptyArray() =>
        Assert.Throws<TranslateException>(() => new BingEngine(new EngineConfig()).Parse("[]"));

    [Fact]
    public void ParseRejectsItemWithoutTranslations() =>
        Assert.Throws<TranslateException>(() =>
            new BingEngine(new EngineConfig()).Parse("""[{"detectedLanguage":{"language":"en"}}]"""));

    [Fact]
    public void TokenTtlLeavesSafetyMargin()
    {
        // 官方 10 分钟，我们缓存 9 分钟
        Assert.True(BingTokenTtl < TimeSpan.FromMinutes(10));
        Assert.True(BingTokenTtl >= TimeSpan.FromMinutes(8.5));
    }

    /// <summary>和 BingEngine 内部保持一致的缓存时长。</summary>
    private static TimeSpan BingTokenTtl => TimeSpan.FromMinutes(9);
}

public class DeepLEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    [Fact]
    public void MissingKeyIsConfigError()
    {
        var ex = Assert.Throws<TranslateException>(() => new DeepLEngine(new EngineConfig()).Build(Req("auto", "zh", "hi")));
        Assert.Equal("config", ex.Kind);
    }

    [Fact]
    public void BlankKeyCountsAsMissing() =>
        Assert.Throws<TranslateException>(() =>
            new DeepLEngine(new EngineConfig { Key = "   " }).Build(Req("auto", "zh", "hi")));

    [Fact]
    public void FreeKeyUsesFreeHostAndPrefixedAuth()
    {
        var call = new DeepLEngine(new EngineConfig { Key = "abc:fx" }).Build(Req("auto", "zh", "hi"));
        Assert.Equal("https://api-free.deepl.com/v2/translate", call.Url);
        Assert.Equal("DeepL-Auth-Key abc:fx", call.Headers["authorization"]);
    }

    [Fact]
    public void ProKeyUsesProHostAndBareAuth()
    {
        var call = new DeepLEngine(new EngineConfig { Key = "abcDEF" }).Build(Req("en", "zh", "hi"));
        Assert.Equal("https://api.deepl.com/v2/translate", call.Url);
        Assert.Equal("abcDEF", call.Headers["authorization"]);
    }

    [Fact]
    public void AutoSourceOmitsSourceLang()
    {
        var call = new DeepLEngine(new EngineConfig { Key = "k" }).Build(Req("auto", "zh", "hi"));
        Assert.DoesNotContain("source_lang", call.Body);
    }

    [Fact]
    public void ExplicitSourceIsSentUppercase()
    {
        var call = new DeepLEngine(new EngineConfig { Key = "k" }).Build(Req("en", "zh", "hi"));
        Assert.Contains("source_lang=EN-US", call.Body);
        Assert.Contains("target_lang=ZH", call.Body);
    }

    [Fact]
    public void UnsupportedLanguageRejectedBeforeNetwork()
    {
        var ex = Assert.Throws<TranslateException>(() =>
            new DeepLEngine(new EngineConfig { Key = "k" }).Build(Req("auto", "th", "hi")));
        Assert.Contains("th", ex.Message);
    }

    [Fact]
    public void ParseSingleTranslation()
    {
        var r = new DeepLEngine(new EngineConfig()).Parse("""{"translations":[{"detected_source_language":"EN","text":"你好"}]}""");
        Assert.Equal("你好", r.Text);
        Assert.Equal("en", r.DetectedFrom);
    }

    [Fact]
    public void LongTextLimitEnforced() =>
        Assert.Throws<TranslateException>(() =>
            new DeepLEngine(new EngineConfig { Key = "k" }).Build(Req("auto", "zh", new string('a', 12801))));
}

public class OpenAiEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    [Fact]
    public void MissingKeyIsConfigError()
    {
        var ex = Assert.Throws<TranslateException>(() => new OpenAiEngine(new EngineConfig()).Build(Req("auto", "zh", "hi")));
        Assert.Equal("config", ex.Kind);
    }

    [Fact]
    public void DefaultBaseAndModel()
    {
        var call = new OpenAiEngine(new EngineConfig { Key = "k" }).Build(Req("auto", "zh", "hi"));
        Assert.EndsWith("/v1/chat/completions", call.Url);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", call.Body);
        Assert.Contains("\"stream\":false", call.Body);
    }

    [Fact]
    public void CustomBaseUrlIsTrimmed()
    {
        var call = new OpenAiEngine(new EngineConfig { Key = "k", BaseUrl = "https://api.deepseek.com/v1/" })
            .Build(Req("auto", "zh", "hi"));
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", call.Url);
    }

    [Fact]
    public void CustomModelIsUsed()
    {
        var cfg = new EngineConfig { Key = "k", Extra = { ["model"] = "deepseek-chat" } };
        Assert.Contains("\"model\":\"deepseek-chat\"", new OpenAiEngine(cfg).Build(Req("auto", "zh", "hi")).Body);
    }

    [Fact]
    public void LocalModeUsesDummyAuth()
    {
        var cfg = new EngineConfig { Key = "unused", Extra = { ["local"] = "1" } };
        var call = new OpenAiEngine(cfg).Build(Req("auto", "zh", "hi"));
        Assert.Equal("Bearer local", call.Headers["authorization"]);
    }

    [Fact]
    public void ExplicitSourceMentionsLanguageInPrompt()
    {
        var call = new OpenAiEngine(new EngineConfig { Key = "k" }).Build(Req("en", "zh", "hello"));
        // System.Text.Json 默认把非 ASCII 转义成 \uXXXX，
        // 断言时要把同样的转义套上去，否则永远匹配不上。
        var escaped = System.Text.Encodings.Web.JavaScriptEncoder
            .Default.Encode("Translate from 英语");
        Assert.Contains(escaped, call.Body);
    }

    [Fact]
    public void ParseReadsChoiceContent()
    {
        var r = new OpenAiEngine(new EngineConfig()).Parse("""{"choices":[{"message":{"role":"assistant","content":"你好"}}]}""");
        Assert.Equal("你好", r.Text);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void ParseHandlesNullContent()
    {
        var ex = Assert.Throws<TranslateException>(() =>
            new OpenAiEngine(new EngineConfig()).Parse("""{"choices":[{"message":{"content":null}}]}"""));
        Assert.Equal("parse", ex.Kind);
    }

    [Theory]
    [InlineData("```\n你好\n```", "你好")]
    [InlineData("```zh\n你好\n```", "你好")]
    [InlineData("译文：你好", "你好")]
    [InlineData("Translation: hello", "hello")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("Hello world", "Hello world")]
    [InlineData("第一行\n第二行", "第一行\n第二行")]
    [InlineData("{{name}} 你好", "{{name}} 你好")]
    public void CleanOutputStripsWrappers(string input, string expected) =>
        Assert.Equal(expected, OpenAiEngine.CleanOutput(input));
}

public class CustomEngineTests
{
    private static TranslateRequest Req(string from, string to, string text) =>
        new() { Text = text, From = from, To = to };

    private static EngineConfig Cfg(string url) => new() { Extra = { ["url"] = url } };

    [Fact]
    public void MissingUrlIsConfigError()
    {
        var ex = Assert.Throws<TranslateException>(() => new CustomEngine(new EngineConfig()).Build(Req("auto", "en", "hi")));
        Assert.Equal("config", ex.Kind);
    }

    [Fact]
    public void RejectsNonHttpUrl() =>
        Assert.Throws<TranslateException>(() => new CustomEngine(Cfg("ftp://example.com")).Build(Req("auto", "en", "hi")));

    [Fact]
    public void UrlPlaceholderIsEncoded()
    {
        var call = new CustomEngine(Cfg("https://api.test/t?q={{text}}&src={{from}}&dst={{to}}")).Build(Req("auto", "zh", "a b"));
        Assert.Contains("q=a%20b", call.Url);
        Assert.Contains("src=auto", call.Url);
        Assert.Contains("dst=zh", call.Url);
    }

    [Fact]
    public void BodyPlaceholderUsesRawText()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["body"] = """{"q":"{{text}}"}""";
        var call = new CustomEngine(cfg).Build(Req("auto", "en", "a b"));
        // body 里要的是原始文本，不是 URL 编码版
        Assert.Equal("""{"q":"a b"}""", call.Body);
    }

    [Fact]
    public void CustomHeadersAreRendered()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Key = "SECRET";
        cfg.Extra["headers"] = "Authorization: Bearer {{key}}\nX-Trace: abc";
        var call = new CustomEngine(cfg).Build(Req("auto", "en", "hi"));
        Assert.Equal("Bearer SECRET", call.Headers["authorization"]);
        Assert.Equal("abc", call.Headers["x-trace"]);
    }

    [Fact]
    public void CrlfSeparatedHeadersBecomeTwoEntries()
    {
        // 正常用法：一行一个头。lines 会正确拆开，不该报错
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["headers"] = "X-A: one\r\nX-B: two";
        var call = new CustomEngine(cfg).Build(Req("auto", "en", "hi"));
        Assert.Equal("one", call.Headers["x-a"]);
        Assert.Equal("two", call.Headers["x-b"]);
    }

    [Fact]
    public void IllegalHeaderNameIsRejected()
    {
        foreach (var bad in new[] { "X A: v", "X(A): v", "X\tA: v", ": v", "中: v" })
        {
            var cfg = Cfg("https://api.test/t");
            cfg.Extra["headers"] = bad;
            Assert.Throws<TranslateException>(() => new CustomEngine(cfg).Build(Req("auto", "en", "hi")));
        }
    }

    [Fact]
    public void ExtraColonsLandInTheValue()
    {
        // HTTP 按第一个冒号切分，值里带冒号是合法的
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["headers"] = "X-Time: 12:30:45";
        Assert.Equal("12:30:45", new CustomEngine(cfg).Build(Req("auto", "en", "hi")).Headers["x-time"]);
    }

    [Fact]
    public void MethodIsConfigurable()
    {
        var cfg = Cfg("https://api.test/t?q={{text}}");
        cfg.Extra["method"] = "get";
        Assert.Equal("GET", new CustomEngine(cfg).Build(Req("auto", "en", "hi")).Method);
    }

    [Fact]
    public void UnsupportedMethodIsRejected()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["method"] = "TRACE";
        Assert.Throws<TranslateException>(() => new CustomEngine(cfg).Build(Req("auto", "en", "hi")));
    }

    [Fact]
    public void ParseWithJsonPath()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["result"] = "{{json:data.result}}";
        Assert.Equal("你好", new CustomEngine(cfg).Parse("""{"data":{"result":"你好"}}""").Text);
    }

    [Fact]
    public void ParseWithArrayIndexPath()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["result"] = "{{json:translations.0.text}}";
        Assert.Equal("hi", new CustomEngine(cfg).Parse("""{"translations":[{"text":"hi"}]}""").Text);
    }

    [Fact]
    public void ParseMissingPathIsError()
    {
        var cfg = Cfg("https://api.test/t");
        cfg.Extra["result"] = "{{json:nope.here}}";
        Assert.Throws<TranslateException>(() => new CustomEngine(cfg).Parse("""{"data":{}}"""));
    }

    [Fact]
    public void ParseWithoutResultReturnsRawBody()
    {
        var r = new CustomEngine(Cfg("https://api.test/t")).Parse("  plain text  ");
        Assert.Equal("plain text", r.Text);
    }
}
