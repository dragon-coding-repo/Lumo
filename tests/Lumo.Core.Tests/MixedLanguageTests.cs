using Lumo.Core;
using Lumo.Core.Engines;
using Xunit;

namespace Lumo.Core.Tests;

/// <summary>
/// 混合语言。检测器只能给出一个主导语言，短路逻辑必须额外判断
/// 「文本里是不是几乎没有别的语言」，否则整段会被丢掉。
/// </summary>
public class MixedLanguageTests
{
    [Fact]
    public void PureTextIsRecognizedAsPure()
    {
        Assert.True(Lang.IsPurely("今天的天气非常好，适合出门散步。", "zh"));
        Assert.True(Lang.IsPurely("The weather is nice today.", "en"));
        Assert.True(Lang.IsPurely("今日は天気がいいですね。", "ja"));
        Assert.True(Lang.IsPurely("El tiempo está muy bueno hoy.", "es"));
    }

    [Fact]
    public void ChineseWithForeignTermsIsNotPure()
    {
        // 用户实测的场景：中文占主导，但夹着英文和西语
        var text = "这个工具支持 Windows 原生 OCR，内存占用只有 30 MB，支持 el español 和 français。";
        Assert.Equal("zh", Lang.Detect(text).Code);   // 检测确实会判成 zh
        Assert.False(Lang.IsPurely(text, "zh"));     // 但不能因此短路
    }

    [Fact]
    public void MixedCjkWesternIsNotPure()
    {
        Assert.False(Lang.IsPurely("我们发布了 Lumo，it uses native OCR，中のフォントも很清楚。", "zh"));
        Assert.False(Lang.IsPurely("我们发布了 Lumo，it uses native OCR，中のフォントも很清楚。", "ja"));
    }

    [Fact]
    public void PureTextStaysPureRegardlessOfLength()
    {
        // 长文本的纯度判断同样成立，不能只看长度
        Assert.True(Lang.IsPurely(string.Concat(Enumerable.Repeat("今天的天气非常好。", 100)), "zh"));
    }

    [Fact]
    public void BrandNameInChineseTextIsNotPurity()
    {
        // 「请打开 iPhone 的设置界面」外来字符约 40%，会被判为不纯。
        // 这是有意的取舍：宁可多发一次请求，也不承担「混合语言整段被丢掉」的风险。
        Assert.False(Lang.IsPurely("请打开 iPhone 的设置界面。", "zh"));
    }

    [Fact]
    public void JapaneseToleratesKanji()
    {
        // 日文本来汉字占比就高，不能把汉字当「外来字符」
        Assert.True(Lang.IsPurely("東京渋谷区の住所です。", "ja"));
    }

    [Fact]
    public void EmptyAndAutoAreNotPure()
    {
        Assert.False(Lang.IsPurely("", "zh"));
        Assert.False(Lang.IsPurely("   ", "zh"));
        Assert.False(Lang.IsPurely("你好", "auto"));
    }

    [Fact]
    public void PureDigitsAndPunctuationAreNotPure()
    {
        // 纯数字/符号没有语言归属，不该触发短路判断
        Assert.False(Lang.IsPurely("12345", "zh"));
    }

    [Fact]
    public async Task MixedTextIsNotShortCircuited()
    {
        // 核心回归：混合语言必须真的发出去
        var t = new Translator(new AppConfig());
        var text = "这个工具支持 Windows 原生 OCR，内存占用只有 30 MB，支持 el español。";
        var r = await t.TranslateAsync(text, "auto", "zh");
        Assert.NotEqual("none", r.Engine);
        Assert.NotNull(r.TargetText);
    }

    [Fact]
    public async Task PureSameLanguageStillShortCircuits()
    {
        // 纯中文译成中文仍然跳过，省一次请求
        var t = new Translator(new AppConfig());
        var r = await t.TranslateAsync("今天的天气非常好，适合出门散步。", "zh", "zh");
        Assert.Equal("none", r.Engine);
        Assert.Null(r.TargetText);
    }
}

public class MixedLanguageNoteTests
{
    [Fact]
    public async Task MixedTextCarriesAnExplanatoryNote()
    {
        // 翻译 API 的源语言只能有一个，混排文本会被整段按主导语言处理。
        // 用户需要知道这一点，以及可以手动指定源语言。
        var t = new Translator(new AppConfig());
        var r = await t.TranslateAsync(
            "这个工具支持 Windows 原生 OCR，内存占用只有 30 MB，支持 el español。", "auto", "zh");
        // 现在走的是分段翻译，提示变成「N 段已翻译，M 段保留原文」
        Assert.NotNull(r.Note);
        Assert.Contains("混合语言", r.Note);
        Assert.Contains("段已翻译", r.Note);
        Assert.Equal("mixed", r.Engine);
    }

    [Fact]
    public async Task SingleLanguageTextHasNoSuchNote()
    {
        var t = new Translator(new AppConfig());
        var r = await t.TranslateAsync("The weather is nice today.", "auto", "zh");
        Assert.Null(r.Note);
    }
}

/// <summary>
/// 低置信度检测必须让位给服务端。
/// </summary>
/// <remarks>
/// 用户实测场景：「buscar conocimiento」不含西语功能词，本地检测猜成英语，
/// 硬发 <c>from=en</c> 后服务端原样返回 —— 表现为「翻译了但没变」。
/// 猜错源语言的代价比不知道源语言大得多，所以低置信度一律交给服务端。
/// </remarks>
public class LowConfidenceDetectionTests
{
    [Fact]
    public void WordWithoutFunctionWordsIsLowConfidence()
    {
        var d = Lang.Detect("buscar conocimiento");
        // 猜成什么不重要，重要的是不能高置信度 —— 高置信度才会被硬发给服务端
        Assert.True(d.Confidence < 0.5f, $"置信度 {d.Confidence} 不该高到可以硬发 from");
    }

    [Fact]
    public void ClearCasesKeepHighConfidence()
    {
        Assert.True(Lang.Detect("The quick brown fox jumps over the lazy dog").Confidence >= 0.5f);
        Assert.True(Lang.Detect("今天天气不错").Confidence >= 0.5f);
    }

    [Fact]
    public void CamelCaseDetectionIsNotOverbroad()
    {
        // 首字母大写不等于驼峰。Qué / C'est / Windows 都是普通词，
        // 之前的驼峰判据会把它们误判成专名导致不翻译。
        Assert.False(MixedTranslator.IsCamelCase("Qué"));
        Assert.False(MixedTranslator.IsCamelCase("C'est"));
        Assert.False(MixedTranslator.IsCamelCase("Windows"));
        Assert.False(MixedTranslator.IsCamelCase("Research"));
        Assert.True(MixedTranslator.IsCamelCase("arXiv"));
        Assert.True(MixedTranslator.IsCamelCase("PowerShell"));
        Assert.True(MixedTranslator.IsCamelCase("GitHub"));
    }

    [Theory]
    [InlineData("OCR", true)]
    [InlineData("PDF", true)]
    [InlineData("DOAJ", true)]
    [InlineData("PDF，", true)]        // 带标点仍应识别为缩写
    [InlineData("Google Scholar", true)]
    [InlineData("arXiv", true)]
    [InlineData("Windows", true)]
    [InlineData("el mar", false)]
    [InlineData("Qué bueno!", false)]
    [InlineData("buscar conocimiento", false)]
    [InlineData("idée", false)]
    [InlineData("open access", false)]
    public void ProperNounDetection(string seg, bool expected) =>
        Assert.Equal(expected, MixedTranslator.LooksLikeProperNoun(seg));

    [Fact]
    public void JapaneseKanaIsSplitFromHan()
    {
        var runs = MixedTranslator.Split("心情有点像“旅の途中”。问：“まだです。”");
        var kana = runs.Where(r => r.Script == TextScript.Kana).Select(r => r.Text).ToList();
        // 假名必须和相邻汉字合成完整短语，不能把助词「の」单独切出来
        Assert.Contains(kana, k => k.Contains("旅の途中"));
        Assert.Contains(kana, k => k.Contains("まだです"));
        Assert.DoesNotContain(kana, k => k.Trim() == "の");
    }

    [Fact]
    public void HanBeforeKanaIsMergedIntoJapaneseRun()
    {
        // 「論文、見つかった」：漢字单独发给服务端会被当成中文原样返回，
        // 必须带上后面的假名一起发，服务端才认得出是日语。
        var runs = MixedTranslator.Split("我笑着回答：“まだです。”");
        Assert.Contains(runs, r => r.Script == TextScript.Kana && r.Text.Contains("まだです"));
    }

    [Fact]
    public void CjkPunctuationIsNeutral()
    {
        // 引号标点自己成段会把片段切碎
        foreach (var ch in new[] { '“', '”', '‘', '’', '。', '，', '！', '？', '「', '」', '¡', '¿' })
            Assert.Equal(TextScript.Neutral, MixedTranslator.Classify(ch));
    }

    [Fact]
    public void ChineseRunNextToKanaRunIsNotMistakenForJapanese()
    {
        // 「问」后面隔着引号才有假名，不算紧邻，仍应当中文保留
        var runs = MixedTranslator.Split("我问：“まだです。”");
        var han = runs.FirstOrDefault(r => r.Text.Contains("问"));
        Assert.NotNull(han);
        Assert.Equal(TextScript.Cjk, han!.Script);
    }
}
