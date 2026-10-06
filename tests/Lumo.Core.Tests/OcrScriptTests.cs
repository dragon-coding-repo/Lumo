using Lumo.Core;
using Xunit;

namespace Lumo.Core.Tests;

public class OcrScriptTests
{
    [Theory]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hans")]
    [InlineData("en", "en-US")]
    [InlineData("ja", "ja-JP")]
    [InlineData("ko", "ko-KR")]
    [InlineData("de", "de-DE")]
    [InlineData("th", "th-TH")]
    [InlineData("auto", "en-US")]
    [InlineData("", "en-US")]
    [InlineData("hi", "en-US")]  // 印地语没有 OCR 包，回落英文
    public void LanguageMapping(string target, string expected) =>
        Assert.Equal(expected, OcrScript.LanguageFor(target));

    [Fact]
    public void EmbedsPathAndLanguage() =>
        Assert.Contains(@"C:\Users\me\AppData\Local\Temp\a.bmp", OcrScript.Build(@"C:\Users\me\AppData\Local\Temp\a.bmp", "zh-Hans"));

    [Fact]
    public void EscapesSingleQuotesInPath()
    {
        // 用户目录带单引号（O'Brien）不能破坏脚本。
        var script = OcrScript.Build(@"C:\Users\O'Brien\x.bmp", "en-US");
        Assert.Contains("O''Brien", script);
    }

    [Fact]
    public void EscapesSingleQuotesInLanguage()
    {
        var script = OcrScript.Build("x.bmp", "en'US");
        Assert.Contains("en''US", script);
    }

    [Fact]
    public void NoPlaceholdersRemain()
    {
        var script = OcrScript.Build(@"C:\tmp\a b.bmp", "zh-Hans");
        Assert.DoesNotContain("__PATH__", script);
        Assert.DoesNotContain("__LANG__", script);
    }

    [Theory]
    [InlineData("GetFileFromPathAsync")]
    [InlineData("BitmapDecoder]::CreateAsync")]
    [InlineData("GetSoftwareBitmapAsync")]
    [InlineData("RecognizeAsync")]
    public void HasAllAwaitBridges(string call) =>
        Assert.Contains(call, OcrScript.Build("x.bmp", "en-US"));

    [Fact]
    public void FallsBackWhenLanguageUnsupported() =>
        Assert.Contains("IsLanguageSupported", OcrScript.Build("x.bmp", "th-TH"));

    [Fact]
    public void FallsBackToUserProfileLanguages() =>
        Assert.Contains("TryCreateFromUserProfileLanguages", OcrScript.Build("x.bmp", "th-TH"));

    [Fact]
    public void ReportsNoEngineDistinctly() =>
        Assert.Contains("NO_ENGINE", OcrScript.Build("x.bmp", "en-US"));

    [Fact]
    public void UsesPowershell51CompatibleSyntax()
    {
        var s = OcrScript.Build("x.bmp", "en-US");
        // PS7 才有但 5.1 不支持的写法
        Assert.DoesNotContain("ForEach-Object -Parallel", s);
        // PS 5.1 需要显式加载 WinRT 桥接程序集
        Assert.Contains("System.Runtime.WindowsRuntime", s);
    }

    [Fact]
    public void LoadsAllRequiredWinRtTypes()
    {
        var s = OcrScript.Build("x.bmp", "en-US");
        foreach (var t in new[]
                 {
                     "Windows.Storage.StorageFile",
                     "Windows.Graphics.Imaging.BitmapDecoder",
                     "Windows.Media.Ocr.OcrEngine",
                     "Windows.Globalization.Language",
                 })
            Assert.Contains(t, s);
    }

    [Fact]
    public void OutputIsValidJsonShape() =>
        // 脚本最后输出 @{{text=...; lines=...}} | ConvertTo-Json
        Assert.Contains("ConvertTo-Json -Compress", OcrScript.Build("x.bmp", "en-US"));

    [Fact]
    public void UsesSingleQuotedPowerShellStrings() =>
        // 路径含反斜杠，必须用单引号；双引号会让 PowerShell 尝试插值
        Assert.Contains("GetFileFromPathAsync('C:\\tmp\\a.bmp')", OcrScript.Build(@"C:\tmp\a.bmp", "en-US"));

    [Fact]
    public void ListLanguagesScriptIsPowershell51Compatible()
    {
        Assert.Contains("AvailableRecognizerLanguages", OcrScript.ListLanguages);
        Assert.DoesNotContain("ForEach-Object -Parallel", OcrScript.ListLanguages);
    }

    [Fact]
    public void ScriptIsDeterministic()
    {
        var a = OcrScript.Build("x.bmp", "en-US");
        var b = OcrScript.Build("x.bmp", "en-US");
        Assert.Equal(a, b);
    }
}
