using Lumo.Core;
using Xunit;

namespace Lumo.Core.Tests;

public class MixedTranslatorTests
{
    // ── 切分 ──

    [Fact]
    public void SplitsByScriptMergingAdjacentRuns()
    {
        var runs = MixedTranslator.Split("你好 world 朋友");
        Assert.Equal(3, runs.Count);
        Assert.Equal(TextScript.Cjk, runs[0].Script);
        Assert.Equal(TextScript.Latin, runs[1].Script);
        Assert.Equal(TextScript.Cjk, runs[2].Script);
    }

    [Fact]
    public void PunctuationAndSpacesAttachToNeighbouringRun()
    {
        // 空格标点是中性的，不能自己成段，否则每个词都会被切开
        var runs = MixedTranslator.Split("It uses OCR, and it works. 这个很好。");
        Assert.Equal(2, runs.Count);
        Assert.Equal(TextScript.Latin, runs[0].Script);
        Assert.Equal("It uses OCR, and it works. ", runs[0].Text);
        Assert.Equal(TextScript.Cjk, runs[1].Script);
    }

    [Fact]
    public async Task PreservesNewlinesAndIndentation()
    {
        var original = "第一行\n    第二行 It works well here.";
        var result = await RunAsync(original, "zh");
        // 换行和缩进必须原样保留
        Assert.Contains("\n    第二行", result);
    }

    [Fact]
    public void CyrillicIsItsOwnScript()
    {
        var runs = MixedTranslator.Split("hello Привет мир");
        Assert.Contains(runs, r => r.Script == TextScript.Cyrillic);
    }

    [Fact]
    public void AccentedLatinIsLatinNotOther()
    {
        // 西语/法语带变音符号，必须归到拉丁，否则会被当"其他文字"按字数判断
        var runs = MixedTranslator.Split("El español y français también");
        Assert.Single(runs);
        Assert.Equal(TextScript.Latin, runs[0].Script);
    }

    [Fact]
    public void JapaneseKanaIsSeparateFromHan()
    {
        // 假名必须独立成类：目标语言是中文时，含假名的片段是日语，必须翻译。
        var runs = MixedTranslator.Split("小さな卵形 hello");
        // 「小さな卵形」= 小 + さな(假名) + 卵形 合成一个完整日文块
        Assert.Equal(runs.Count, 3);
        Assert.Equal(TextScript.Kana, runs[0].Script);
        Assert.Equal("小さな卵形", runs[0].Text);
        Assert.Equal(TextScript.Latin, runs[^1].Script);
        Assert.Equal("hello", runs[^1].Text);
    }

    // ── 翻译决策 ──

    [Fact]
    public void TargetLanguageRunsAreKept()
    {
        Assert.False(MixedTranslator.ShouldTranslate(
            new TextRun("这个工具很好。", TextScript.Cjk), "zh"));
    }

    [Fact]
    public void ShortLatinFragmentsAreKeptAsTerms()
    {
        // 单词/术语单独送去翻必然错译（Windows → 窗户）
        foreach (var term in new[] { "Windows", "Lumo", "OCR", "API", "Google", "Adelante" })
            Assert.False(MixedTranslator.ShouldTranslate(
                new TextRun(term, TextScript.Latin), "zh"), $"「{term}」不该单独翻");
    }

    [Fact]
    public void LoneFunctionWordsAreKept()
    {
        // el / y / of 单独出现时翻译只会得到「该」「和」「的」，毫无意义
        foreach (var w in new[] { "el", "y", "of", "the", "de", "la", "y", "un" })
            Assert.False(MixedTranslator.ShouldTranslate(
                new TextRun(w, TextScript.Latin), "zh"), $"功能词「{w}」不该单独翻");
    }

    [Fact]
    public void LongLatinFragmentsAreTranslated()
    {
        Assert.True(MixedTranslator.ShouldTranslate(
            new TextRun("It uses the native OCR engine", TextScript.Latin), "zh"));
        Assert.True(MixedTranslator.ShouldTranslate(
            new TextRun("El español y francés también soportados", TextScript.Latin), "zh"));
    }

    [Fact]
    public void TwoWordFragmentsFollowProperNounRule()
    {
        // 判据已从「长度」改为「像不像专名」。
        // 两个小写词是普通词组，该翻；两个大写词是专名，不该翻。
        Assert.True(MixedTranslator.ShouldTranslate(
            new TextRun("open access", TextScript.Latin), "zh"));
        Assert.False(MixedTranslator.ShouldTranslate(
            new TextRun("Google Scholar", TextScript.Latin), "zh"));
    }

    [Fact]
    public void OtherScriptsUseCharacterCount()
    {
        Assert.False(MixedTranslator.ShouldTranslate(
            new TextRun("При", TextScript.Cyrillic), "zh"));
        Assert.True(MixedTranslator.ShouldTranslate(
            new TextRun("Это тест", TextScript.Cyrillic), "zh"));
    }

    [Fact]
    public void EnglishTargetKeepsLatinAndTranslatesCjk()
    {
        Assert.False(MixedTranslator.ShouldTranslate(
            new TextRun("This is a test", TextScript.Latin), "en"));
        Assert.True(MixedTranslator.ShouldTranslate(
            new TextRun("这是一个测试用例", TextScript.Cjk), "en"));
    }

    // ── 端到端（用假翻译器，不联网）──

    private static async Task<string> RunAsync(string text, string target)
    {
        var r = await MixedTranslator.TranslateAsync(
            text, target, (seg, _, _) => Task.FromResult<string?>("«" + seg + "»"));
        return r.Text;
    }

    [Fact]
    public async Task OnlyNonTargetLanguageRunsGetTranslated()
    {
        var out_ = await RunAsync("你好 world of translators 你好", "zh");
        Assert.Equal("你好 «world of translators» 你好", out_);
    }

    [Fact]
    public async Task WhitespaceAroundTranslatedRunIsPreserved()
    {
        var out_ = await RunAsync("前 it uses native ocr 后", "zh");
        Assert.Equal("前 «it uses native ocr» 后", out_);
    }

    [Fact]
    public async Task ReportsCounts()
    {
        var r = await MixedTranslator.TranslateAsync(
            "你好 Lumo 这个工具 it works very well indeed 结束",
            "zh", (s, _, _) => Task.FromResult<string?>("«" + s + "»"));
        Assert.Equal(1, r.TranslatedRuns);
        Assert.True(r.DidAnything);
    }

    [Fact]
    public async Task PureTextProducesNoTranslation()
    {
        Func<string, string, CancellationToken, Task<string?>> echo =
            (seg, tgt, ct) => Task.FromResult<string?>("X");
        var r = await MixedTranslator.TranslateAsync("这整段都是中文，不需要翻译。", "zh", echo);
        Assert.False(r.DidAnything);
        Assert.Equal("这整段都是中文，不需要翻译。", r.Text);
    }

    [Fact]
    public async Task FailedSegmentFallsBackToOriginal()
    {
        // 一个片段翻不了不能毁掉整篇
        Func<string, string, CancellationToken, Task<string?>> alwaysFails =
            (seg, tgt, ct) => Task.FromResult<string?>(null);
        var r = await MixedTranslator.TranslateAsync("前 it works very well 后", "zh", alwaysFails);
        Assert.Equal("前 it works very well 后", r.Text);
        Assert.False(r.DidAnything);
    }

    [Fact]
    public async Task EmptyAndWhitespaceAreSafe()
    {
        Assert.Equal("", await RunAsync("", "zh"));
        Assert.Equal("   ", await RunAsync("   ", "zh"));
    }

    [Fact]
    public void ClassifyHandlesEdgeCases()
    {
        Assert.Equal(TextScript.Neutral, MixedTranslator.Classify(' '));
        Assert.Equal(TextScript.Neutral, MixedTranslator.Classify('。'));
        Assert.Equal(TextScript.Neutral, MixedTranslator.Classify('5'));
        Assert.Equal(TextScript.Latin, MixedTranslator.Classify('A'));
        Assert.Equal(TextScript.Latin, MixedTranslator.Classify('ñ'));
        Assert.Equal(TextScript.Cjk, MixedTranslator.Classify('中'));
        Assert.Equal(TextScript.Kana, MixedTranslator.Classify('あ'));
        Assert.Equal(TextScript.Cyrillic, MixedTranslator.Classify('д'));
    }
}
