using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lumo.Core;

namespace Lumo.App.Win;

/// <summary>
/// 截图 OCR 的执行端。
/// </summary>
/// <remarks>
/// 走 Windows 自带的 <c>Windows.Media.Ocr</c>，不捆绑任何模型文件 ——
/// 这是「低资源占用」最重要的一环：几百 MB 的 OCR 模型和它带来的常驻内存是矛盾的。
///
/// 脚本本身在 <see cref="OcrScript"/> 里（放 core 是为了能离线单测），
/// 这里只负责起进程、传参、解析输出。
/// </remarks>
internal static class Ocr
{
    /// <summary>必须用 Windows PowerShell 5.1。<c>pwsh</c> (PS7) 移除了 WinRT 投影，用了必然失败。</summary>
    private const string PowerShell = "powershell.exe";

    private static readonly string[] ArgList =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"];

    public static OcrResult Recognize(byte[] bmp, string targetLang)
    {
        // BMP 临时文件是必需的：WinRT 的 StorageFile::GetFileFromPathAsync 只认路径，
        // 没有公开的「从内存流喂给 OCR」的路径。
        var dir = Path.Combine(Path.GetTempPath(), "lumo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ocr-{Environment.ProcessId}-{Guid.NewGuid():N}.bmp");
        try
        {
            File.WriteAllBytes(path, bmp);
            return Run(OcrScript.Build(path, OcrScript.LanguageFor(targetLang)));
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件删不掉不影响功能 */ }
        }
    }

    public static IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            var stdout = Exec(OcrScript.ListLanguages, 15_000);
            var line = stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('['));
            if (line is null) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(line.Trim());
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static OcrResult Run(string script)
    {
        var stdout = Exec(script, 30_000, out var stderr);

        // PowerShell 会往 stdout 混警告行，只取最后一个非空的 JSON 行。
        var payload = stdout.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith('{') && l.EndsWith('}'));
        if (payload is null)
        {
            var err = stderr.Trim();
            if (err.Length > 300) err = err[..300];
            throw new InvalidOperationException(err.Length == 0
                ? "OCR 没有返回结果"
                : $"OCR 失败：{err}");
        }

        if (payload.Contains("NO_ENGINE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "系统没有可用的 OCR 引擎。请到「设置 → 时间和语言 → 语言和区域」补装语言包后重试。");
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var lines = root.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList()
            : new List<string>();
        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : string.Join("\n", lines);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("没有识别到文字。可能区域太小或对比度太低");
        }
        return new OcrResult(text.Trim(), lines);
    }

    private static string Exec(string script, int timeoutMs) => Exec(script, timeoutMs, out _);

    private static string Exec(string script, int timeoutMs, out string stderr)
    {
        var psi = new ProcessStartInfo(PowerShell, ArgList)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var p = Process.Start(psi) ??
                    throw new InvalidOperationException(
                        "无法启动 PowerShell，截图翻译不可用。系统里应该有 powershell.exe，请确认未被精简版系统移除。");
        var stdout = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 已经退出了 */ }
            throw new TimeoutException("OCR 超时");
        }
        return stdout;
    }
}

internal sealed record OcrResult(string Text, IReadOnlyList<string> Lines);
