using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumo.Core.Engines;

/// <summary>Google 翻译（translate.googleapis.com 的公开端点）。</summary>
/// <remarks>
/// 走匿名 <c>client=gtx</c> 通道，不需要 Key。官方没有为这个端点提供稳定性承诺，
/// 所以额外做了 Cookie 注入（提高配额）和失败回退。
/// </remarks>
public sealed class GoogleEngine : TranslateEngineBase
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private readonly EngineConfig _config;

    public GoogleEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.Google;
    public override string DisplayName => "Google 翻译";
    public override bool RequiresKey => false;

    public override HttpCall Build(TranslateRequest req)
    {
        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");
        if (text.Length > 5000) throw TranslateException.Config("Google 免费端点单次上限 5000 字符，请分段翻译");

        // 校验 cookie：请求头不能含换行，否则构成请求头注入。
        var cookie = _config.Get("cookie");
        if (cookie is not null)
        {
            if (cookie.Contains('\n') || cookie.Contains('\r'))
                throw TranslateException.Config("cookie 含非法换行符");
            cookie = cookie.Trim();
            if (cookie.Length == 0) cookie = null;
        }

        var url = $"{Endpoint}?client=gtx&sl={Uri.EscapeDataString(req.From)}" +
                  $"&tl={Uri.EscapeDataString(req.To)}&dt=t&dt=bd&dt=ex&q={Uri.EscapeDataString(text)}";

        var call = HttpCall.Get(url);
        if (cookie is not null) call = call.WithHeader("cookie", cookie);
        return call;
    }

    public override TranslateResponse Parse(string body)
    {
        // 响应是嵌套数组：
        // [[["译文","原文",null,null,10]],null,"en",...]
        using var doc = Json.TryParseDoc(body, "Google");

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw TranslateException.Parse("Google 响应缺少译文段");

        var sb = new System.Text.StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }

        var text = sb.ToString();
        if (text.Length == 0) throw TranslateException.Parse("Google 返回了空译文");

        var detected = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 &&
                       root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString()
            : null;

        return new TranslateResponse { Text = text, DetectedFrom = detected };
    }
}

/// <summary>微软必应翻译。匿名取 JWT 后调 Translator v3，token 带缓存复用。</summary>
public sealed class BingEngine : TranslateEngineBase
{
    private const string AuthUrl = "https://edge.microsoft.com/translate/auth";
    private const string ApiBase = "https://api.cognitive.microsofttranslator.com/translate";
    private const string ApiVersion = "3.0";
    // 官方给 10 分钟有效期，缓存 9 分钟留 1 分钟余量。
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(9);

    private readonly EngineConfig _config;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public BingEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.Bing;
    public override string DisplayName => "微软必应";
    public override bool RequiresKey => false;

    public override HttpCall Build(TranslateRequest req) => BuildWithToken(req, "TOKEN_PLACEHOLDER");

    /// <summary>带真实 token 构造请求。抽出来是为了能离线单测。</summary>
    public HttpCall BuildWithToken(TranslateRequest req, string token)
    {
        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");
        if (text.Length > 5000) throw TranslateException.Config("必应单次上限 5000 字符");
        if (string.IsNullOrWhiteSpace(token)) throw TranslateException.Parse("必应令牌为空");

        var query = $"api-version={ApiVersion}&to={Uri.EscapeDataString(req.To)}";
        if (req.From is not (null or "" or "auto"))
            query += $"&from={Uri.EscapeDataString(req.From)}";

        return HttpCall.Post($"{ApiBase}?{query}")
            .WithHeader("authorization", $"Bearer {token}")
            .WithJsonBody(new { Text = text });
    }

    public override TranslateResponse Parse(string body)
    {
        // Translator v3 返回的是「请求项数组」，每项里再套一层 translations。
        using var doc = Json.TryParseDoc(body, "必应");

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw TranslateException.Parse("必应返回了空结果");

        var item = doc.RootElement[0];
        if (!item.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
            throw TranslateException.Parse("必应返回了空译文");

        var text = translations[0].GetProperty("text").GetString();
        if (string.IsNullOrEmpty(text)) throw TranslateException.Parse("必应返回了空译文");

        string? detected = null;
        if (item.TryGetProperty("detectedLanguage", out var lang) &&
            lang.TryGetProperty("language", out var l))
            detected = l.GetString();

        return new TranslateResponse { Text = text, DetectedFrom = detected };
    }

    public override async Task<TranslateResponse> TranslateAsync(
        HttpClient client, TranslateRequest req, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(client, ct).ConfigureAwait(false);
        return await SendAsync(client, req, token, ct).ConfigureAwait(false);
    }

    private async Task<TranslateResponse> SendAsync(HttpClient client, TranslateRequest req, string token, CancellationToken ct)
    {
        var call = BuildWithToken(req, token);
        using var message = new HttpRequestMessage(HttpMethod.Post, call.Url);
        foreach (var (k, v) in call.Headers)
            message.Headers.TryAddWithoutValidation(k, v);
        message.Content = new StringContent(call.Body ?? "{}", Encoding.UTF8);

        using var resp = await SendAsync(client, message, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw TranslateException.Http((int)resp.StatusCode, body);
        return Parse(body);
    }

    private async Task<string> GetTokenAsync(HttpClient client, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

            using var resp = await client.GetAsync(AuthUrl, ct).ConfigureAwait(false);
            var text = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (!resp.IsSuccessStatusCode)
            {
                // 必须带上状态码。这个端点失败时经常返回空 body，
                // 只写「取必应令牌失败：」的话用户看到的就是一个空壳，什么都判断不出来。
                var detail = string.IsNullOrWhiteSpace(text) ? "（服务未返回内容）" : text;
                if (detail.Length > 200) detail = detail[..200];
                throw new TranslateException("http", $"取必应令牌失败：HTTP {(int)resp.StatusCode} {detail}");
            }
            if (text.Length == 0) throw TranslateException.Parse("必应返回了空令牌");

            _token = text;
            _tokenExpiry = DateTimeOffset.UtcNow + TokenTtl;
            return text;
        }
        catch (HttpRequestException ex)
        {
            throw TranslateException.Network($"取必应令牌失败：{ex.Message}");
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}

/// <summary>DeepL。免费版与专业版共用逻辑，只是 host 不同。</summary>
public sealed class DeepLEngine : TranslateEngineBase
{
    private const string FreeHost = "https://api-free.deepl.com/v2/translate";
    private const string ProHost = "https://api.deepl.com/v2/translate";

    private readonly EngineConfig _config;
    public DeepLEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.DeepL;
    public override string DisplayName => "DeepL";
    public override bool RequiresKey => true;

    public override HttpCall Build(TranslateRequest req)
    {
        var key = _config.Key?.Trim();
        if (string.IsNullOrEmpty(key)) throw TranslateException.MissingKey;

        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");
        if (text.Length > 12800) throw TranslateException.Config("DeepL 单次上限 12800 字符");

        if (ToDeepLCode(req.To) is not { } target)
            throw TranslateException.Config($"DeepL 不支持目标语言 `{req.To}`");

        string? source = null;
        if (req.From is not (null or "" or "auto"))
        {
            if (ToDeepLCode(req.From) is not { } s)
                throw TranslateException.Config($"DeepL 不支持源语言 `{req.From}`");
            source = s;
        }

        // 免费版 key 以 `:fx` 结尾，据此选 host。
        var isFree = key.EndsWith(":fx", StringComparison.Ordinal);
        var url = isFree ? FreeHost : ProHost;
        var auth = isFree ? $"DeepL-Auth-Key {key}" : key;

        var pairs = new List<(string, string)>
        {
            ("text", text),
            ("target_lang", target),
            ("preserve_formatting", "1"),
        };
        if (source is not null) pairs.Add(("source_lang", source));

        return HttpCall.Post(url).WithHeader("authorization", auth).WithFormBody(pairs.ToArray());
    }

    public override TranslateResponse Parse(string body)
    {
        using var doc = Json.TryParseDoc(body, "DeepL");
        if (!doc.RootElement.TryGetProperty("translations", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw TranslateException.Parse("DeepL 返回了空结果");

        var first = list[0];
        var text = first.GetProperty("text").GetString();
        if (string.IsNullOrEmpty(text)) throw TranslateException.Parse("DeepL 返回了空译文");

        string? detected = null;
        if (first.TryGetProperty("detected_source_language", out var d))
            detected = d.GetString()?.ToLowerInvariant();

        return new TranslateResponse { Text = text, DetectedFrom = detected };
    }

    /// <summary>映射到 DeepL 的语言码。DeepL 只支持约 30 种语言。</summary>
    internal static string? ToDeepLCode(string code) => code switch
    {
        "zh" or "zh-Hans" => "ZH",
        "zh-Hant" => "ZH-HANT",
        "en" => "EN-US",
        "ja" => "JA",
        "ko" => "KO",
        "fr" => "FR",
        "de" => "DE",
        "es" => "ES",
        "pt" => "PT-PT",
        "it" => "IT",
        "ru" => "RU",
        "uk" => "UK",
        "nl" => "NL",
        "pl" => "PL",
        "tr" => "TR",
        "vi" => "VI",
        "id" => "ID",
        "ar" => "AR",
        "el" => "EL",
        _ => null,
    };
}

/// <summary>OpenAI 兼容的 Chat Completions 引擎。DeepSeek / Kimi / Ollama 等都能接。</summary>
public sealed class OpenAiEngine : TranslateEngineBase
{
    private const string DefaultBase = "https://api.openai.com/v1";
    private const string DefaultModel = "gpt-4o-mini";
    private const int MaxCharsHint = 8000;

    private readonly EngineConfig _config;
    public OpenAiEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.OpenAi;
    public override string DisplayName => "OpenAI 兼容";
    public override bool RequiresKey => true;

    public string BaseUrl => string.IsNullOrWhiteSpace(_config.BaseUrl) ? DefaultBase : _config.BaseUrl.TrimEnd('/');
    public string Model => _config.Get("model") ?? DefaultModel;

    public override HttpCall Build(TranslateRequest req)
    {
        var key = _config.Key?.Trim();
        if (string.IsNullOrEmpty(key)) throw TranslateException.MissingKey;

        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");
        if (text.Length > MaxCharsHint)
            throw TranslateException.Config($"文本超过 {MaxCharsHint} 字符，建议分段翻译以免超出模型上下文");

        var target = Lang.DisplayName(req.To);
        var system =
            $"You are a professional translator. Translate the user's text into {target}. " +
            "Output only the translation, with no explanations, no quotes, and no notes. " +
            "Preserve the original formatting, line breaks, and any placeholders like {{var}}.";

        var user = req.From is null or "" or "auto"
            ? text
            : $"Translate from {Lang.DisplayName(req.From)} into {target}:\n\n{text}";

        var temperature = float.TryParse(_config.Get("temperature"), out var t) ? t : 0.2f;
        var isLocal = _config.Get("local") is "1" or "true";

        var call = HttpCall.Post($"{BaseUrl}/chat/completions")
            .WithHeader("authorization", isLocal ? "Bearer local" : $"Bearer {key}")
            .WithJsonBody(new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
                temperature,
                stream = false,
            });
        return call;
    }

    public override TranslateResponse Parse(string body)
    {
        using var doc = Json.TryParseDoc(body, "OpenAI 兼容引擎");
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw TranslateException.Parse("响应不是标准 OpenAI 格式（缺少 choices）");

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var text = CleanOutput(content ?? "");
        if (text.Length == 0) throw TranslateException.Parse("模型返回了空内容");

        return new TranslateResponse
        {
            Text = text,
            Note = "由大模型翻译，可能存在偏差，建议核对",
        };
    }

    /// <summary>剥掉模型爱加的外围包装：代码块、引号、"译文："之类的前缀。</summary>
    public static string CleanOutput(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            var end = t.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) t = t[..end];
            t = t.Trim();
        }
        foreach (var prefix in new[] { "译文：", "译文:", "翻译：", "翻译:", "Translation:", "translation:" })
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                t = t[prefix.Length..].Trim();
                break;
            }
        }
        if (t.Length >= 2)
        {
            var first = t[0];
            var last = t[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                t = t[1..^1];
        }
        return t.Trim();
    }
}

/// <summary>自定义 HTTP 引擎。用占位符模板描述任意翻译 API，不需要改一行 C#。</summary>
public sealed class CustomEngine : TranslateEngineBase
{
    private readonly EngineConfig _config;
    public CustomEngine(EngineConfig config) => _config = config;

    public override string Id => EngineIds.Custom;
    public override string DisplayName => "自定义 HTTP";
    public override bool RequiresKey => false;

    public override HttpCall Build(TranslateRequest req)
    {
        var url = _config.Get("url");
        if (string.IsNullOrEmpty(url)) throw TranslateException.Config("自定义引擎缺少 url 配置");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw TranslateException.Config("url 必须以 http:// 或 https:// 开头");

        var text = req.Text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");

        var method = (_config.Get("method") ?? "POST").Trim().ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH"))
            throw TranslateException.Config($"不支持的 HTTP 方法 `{method}`");

        // URL 里的占位符用编码后的值，body 里的用原始值。
        var urlVars = new (string Key, string Value)[]
        {
            ("text", Uri.EscapeDataString(text)),
            ("from", Uri.EscapeDataString(req.From)),
            ("to", Uri.EscapeDataString(req.To)),
            ("key", Uri.EscapeDataString(_config.Key ?? "")),
        };
        var rendered = url;
        foreach (var (k, v) in urlVars) rendered = rendered.Replace("{{" + k + "}}", v);

        var call = new HttpCall(method, rendered);

        if (_config.Get("headers") is { } headers)
        {
            foreach (var line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Trim().Length == 0) continue;
                var idx = line.IndexOf(':');
                if (idx < 0)
                    throw TranslateException.Config($"请求头格式应为 `名称: 值`，收到 `{line.Trim()}`");
                var name = line[..idx].Trim();
                if (!ValidHeaderName(name))
                    throw TranslateException.Config($"非法请求头名 `{name}`：只允许 RFC 7230 的 token 字符");
                var value = line[(idx + 1)..].Trim();
                foreach (var (k, v) in urlVars) value = value.Replace("{{" + k + "}}", v);
                // 必须走 WithHeader —— 它会把 content-type 路由到 ContentType 字段。
                // 直接写 call.Headers[...] 会让用户自定义的 Content-Type 落进请求头，
                // 和内容头冲突，服务端会返回 400。
                call.WithHeader(name, value);
            }
        }

        if (_config.Get("body") is { } bodyTemplate)
        {
            var body = bodyTemplate;
            body = body.Replace("{{text}}", text);
            body = body.Replace("{{key}}", _config.Key ?? "");
            body = body.Replace("{{from}}", req.From);
            body = body.Replace("{{to}}", req.To);
            call.Body = body;
            // 用户在自定义头里显式指定了 content-type 就尊重它，否则补 JSON 默认值。
            // 必须写 ContentType 字段而不是 Headers —— 写 Headers 会触发 400。
            if (string.IsNullOrEmpty(call.ContentType))
                call.ContentType = "application/json";
        }

        return call;
    }

    public override TranslateResponse Parse(string body)
    {
        // 模板里写 {{json:data.translations.0.text}} 时按路径取值。
        if (_config.Get("result") is { } spec &&
            spec.StartsWith("{{json:", StringComparison.Ordinal) &&
            spec.EndsWith("}}", StringComparison.Ordinal))
        {
            var path = spec[7..^2].Trim();
            if (path.Length == 0) throw TranslateException.Config("result 路径为空");

            using var doc = Json.TryParseDoc(body, "自定义引擎");
            var node = doc.RootElement;
            foreach (var seg in path.Split('.'))
            {
                if (seg.Length == 0) continue;
                if (node.ValueKind == JsonValueKind.Array)
                {
                    if (!int.TryParse(seg, out var idx) || idx < 0 || idx >= node.GetArrayLength())
                        throw TranslateException.Parse($"响应中找不到路径 `{path}`");
                    node = node[idx];
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(seg, out var next))
                {
                    node = next;
                }
                else
                {
                    throw TranslateException.Parse($"响应中找不到路径 `{path}`");
                }
            }

            var text = node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString();
            if (text.Length == 0) throw TranslateException.Parse("指定路径的值是空的");
            return new TranslateResponse { Text = text, Note = $"自定义引擎，路径 `{path}`" };
        }

        // 没配 result 就把整段响应当译文（适用于纯文本接口）。
        var t = body.Trim();
        if (t.Length == 0) throw TranslateException.Parse("响应为空");
        return new TranslateResponse { Text = t };
    }

    /// <summary>请求头名只允许 RFC 7230 的 token 字符。</summary>
    internal static bool ValidHeaderName(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
}

public static class EngineFactory
{
    public static TranslateEngineBase? Create(string id, EngineConfig config) => id switch
    {
        EngineIds.Edge => new EdgeEngine(config.Clone()),
        EngineIds.Google => new GoogleEngine(config.Clone()),
        EngineIds.Bing => new BingEngine(config.Clone()),
        EngineIds.DeepL => new DeepLEngine(config.Clone()),
        EngineIds.OpenAi => new OpenAiEngine(config.Clone()),
        EngineIds.Custom => new CustomEngine(config.Clone()),
        _ => null,
    };

    public static IReadOnlyList<EngineInfo> Available { get; } = new[]
    {
        new EngineInfo(EngineIds.Edge, "微软 Edge（推荐，国内可用）", false),
        new EngineInfo(EngineIds.Google, "Google 翻译（国内通常不通）", false),
        new EngineInfo(EngineIds.DeepL, "DeepL", true),
        new EngineInfo(EngineIds.OpenAi, "OpenAI 兼容", true),
        new EngineInfo(EngineIds.Custom, "自定义 HTTP", false),
    };
}
