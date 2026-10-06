using System.Text;
using System.Text.Json;
using Lumo.Core;

namespace Lumo.Core.Engines;

/// <summary>
/// 微软 Edge 内置翻译。
/// </summary>
/// <remarks>
/// <para>
/// <b>端点历史（这里踩过坑，务必留意）：</b>
/// <list type="bullet">
/// <item>旧版走 <c>https://edge.microsoft.com/translate/auth</c> 取匿名 JWT，
/// 再调 Translator v3。这个 auth 端点<b>已被微软于 2026 年 8 月下线</b>，
/// 现在固定返回 404。最初实现用的就是它，所以表现为「取必应令牌失败：HTTP 404」。</item>
/// <item>现役端点是 Edge 浏览器的内置翻译接口
/// <c>https://edge.microsoft.com/translate/translatetext</c>，
/// <b>不需要鉴权</b>，但请求头必须带浏览器 UA，否则被拒。</item>
/// </list>
/// </para>
/// <para>
/// 选它做默认引擎的理由：<c>edge.microsoft.com</c> 在国内可直连，
/// 不需要 API Key，也不像 Google 那样容易被阻断。
/// </para>
/// </remarks>
public sealed class EdgeEngine : TranslateEngineBase
{
    private const string Endpoint = "https://edge.microsoft.com/translate/translatetext";

    /// <summary>这个端点强制要求浏览器 UA，没有就会 403。</summary>
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    private readonly EngineConfig _config;

    public EdgeEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.Edge;
    public override string DisplayName => "微软 Edge";
    public override bool RequiresKey => false;

    public override HttpCall Build(TranslateRequest req)
    {
        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");
        // 端点单次上限约 5 万字符，但实际用不到；留个保守值并给出可操作的提示。
        if (text.Length > 20000) throw TranslateException.Config("单次最多 20000 字符，请分段翻译");

        var query = new StringBuilder("?api-version=3.0");
        // 不传 from 时由服务端自动检测，响应里会带 detectedLanguage。
        if (req.From is not (null or "" or "auto"))
            query.Append("&from=").Append(Uri.EscapeDataString(ToEdgeCode(req.From)));

        query.Append("&to=").Append(Uri.EscapeDataString(ToEdgeCode(req.To)));

        // 请求体是字符串数组，不是对象 —— 这一点和旧接口不一样，容易写错。
        return HttpCall.Post(Endpoint + query)
            .WithHeader("content-type", "application/json")
            .WithHeader("user-agent", BrowserUa)
            .WithHeader("origin", "https://www.microsoft.com")
            .WithHeader("referer", "https://www.microsoft.com/")
            .WithJsonBody(new[] { text });
    }

    public override TranslateResponse Parse(string body)
    {
        using var doc = Json.TryParseDoc(body, "微软 Edge");

        // 响应：[{"detectedLanguage":{...},"translations":[{"text":"...","to":"..."}]}]
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw TranslateException.Parse("Edge 返回了空结果");

        var item = doc.RootElement[0];
        if (!item.TryGetProperty("translations", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw TranslateException.Parse("Edge 返回了空译文");

        var text = list[0].GetProperty("text").GetString();
        if (string.IsNullOrEmpty(text)) throw TranslateException.Parse("Edge 返回了空译文");

        // 自动检测时服务端才会给这个字段
        string? detected = null;
        if (item.TryGetProperty("detectedLanguage", out var lang) &&
            lang.TryGetProperty("language", out var l))
            detected = NormalizeEdgeCode(l.GetString());

        return new TranslateResponse { Text = text, DetectedFrom = detected };
    }

    /// <summary>映射到 Edge 使用的语言码（和 Bing Translator v3 一套）。</summary>
    internal static string ToEdgeCode(string code) => code switch
    {
        "zh" or "zh-Hans" => "zh-Hans",
        "zh-Hant" => "zh-Hant",
        "en" => "en",
        "ja" => "ja",
        "ko" => "ko",
        "fr" => "fr",
        "de" => "de",
        "es" => "es",
        "pt" => "pt",
        "it" => "it",
        "ru" => "ru",
        "uk" => "uk",
        "nl" => "nl",
        "pl" => "pl",
        "tr" => "tr",
        "vi" => "vi",
        "id" => "id",
        "th" => "th",
        "ar" => "ar",
        "he" => "he",
        "el" => "el",
        "hi" => "hi",
        _ => code,
    };

    /// <summary>把服务端返回的码归一化回 Lumo 内部用的码。</summary>
    internal static string? NormalizeEdgeCode(string? code) => code switch
    {
        null => null,
        "zh-Hans" or "zh-CHS" => "zh",
        "zh-Hant" or "zh-CHT" => "zh-Hant",
        _ => code,
    };
}
