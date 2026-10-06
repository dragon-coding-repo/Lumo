namespace Lumo.Core;

/// <summary>
/// OCR 的 PowerShell 脚本模板。
/// </summary>
/// <remarks>
/// 放在 core 而不是 App 层，是为了能在任意平台跑单元测试 ——
/// 这个脚本是纯字符串拼接，没有任何 Windows 依赖，却是最容易出静默错误的地方。
/// </remarks>
public static class OcrScript
{
    /// <summary>OCR 语言包。值是 Windows 的 BCP-47 标签。</summary>
    public static string LanguageFor(string targetLang) => Lang.Normalize(targetLang) switch
    {
        "zh" or "zh-Hant" => "zh-Hans",
        "en" => "en-US",
        "ja" => "ja-JP",
        "ko" => "ko-KR",
        "fr" => "fr-FR",
        "de" => "de-DE",
        "es" => "es-ES",
        "pt" => "pt-BR",
        "it" => "it-IT",
        "ru" => "ru-RU",
        "th" => "th-TH",
        "vi" => "vi-VN",
        _ => "en-US",
    };

    /// <summary>PowerShell 单引号字符串转义：内部单引号要写成两个。</summary>
    internal static string Escape(string s) => s.Replace("'", "''");

    /// <summary>
    /// 组装调用 Windows.Media.Ocr 的 PowerShell 脚本。
    /// </summary>
    /// <remarks>
    /// 用 <c>__LANG__</c> / <c>__PATH__</c> 这种占位符而不是 C# 字符串插值 ——
    /// 脚本里全是 PowerShell 的 <c>$</c> 和 <c>@{</c>，走插值的话转义规则会互相打架。
    /// 占位符替换是纯文本操作，没有歧义。
    /// </remarks>
    public static string Build(string bmpPath, string langTag) => Template
        .Replace("__PATH__", Escape(bmpPath))
        .Replace("__LANG__", Escape(langTag));

    /// <summary>列出已安装 OCR 语言包的脚本。</summary>
    public const string ListLanguages = """
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
        $langs = @()
        foreach ($l in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) { $langs += $l.LanguageTag }
        Write-Output ($langs | ConvertTo-Json -Compress)
        """;

    private const string Template = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        # WinRT 类型投影：PS 5.1 需要显式加载这些程序集和类型。
        # ⚠ 必须用 powershell.exe (5.1)，不能用 pwsh —— PS7 移除了 WinRT 投影。
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
        $null = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]

        # PowerShell 不能直接 await WinRT 的 IAsyncOperation，
        # 通过反射拿到 AsTask 桥接成 Task 再同步等待。
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]

        function Await($AsyncOp, [Type]$ResultType) {
            $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($AsyncOp))
            $task.Wait(-1) | Out-Null
            $task.Result
        }

        # 优先用请求的语言；系统没装这个语言包就退回用户已安装的语言。
        $lang = [Windows.Globalization.Language]::new('__LANG__')
        $engine = $null
        if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($lang)) {
            $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
        }
        if ($engine -eq $null) {
            $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        }
        if ($engine -eq $null) {
            Write-Output '{"error":"NO_ENGINE"}'
            exit 1
        }

        $file    = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync('__PATH__')) ([Windows.Storage.StorageFile])
        $stream  = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap  = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $result  = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $stream.Dispose()

        $lines = @()
        foreach ($line in $result.Lines) { $lines += $line.Text }

        Write-Output (@{ text = $result.Text; lines = $lines } | ConvertTo-Json -Compress -Depth 3)
        """;
}
