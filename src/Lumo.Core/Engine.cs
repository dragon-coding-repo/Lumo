using System.Text;

namespace Lumo.Core;

/// <summary>一次翻译请求。</summary>
public sealed record TranslateRequest
{
    public required string Text { get; init; }
    /// <summary><c>auto</c> 表示让引擎自己检测源语言。</summary>
    public string From { get; init; } = "auto";
    public string To { get; init; } = "zh";
}

/// <summary>一次翻译的结果。</summary>
public sealed record TranslateResponse
{
    /// <summary>译文。<c>null</c> 表示判定无需翻译（例如原文已是目标语言）。</summary>
    public string? Text { get; init; }
    /// <summary>引擎识别出的源语言，引擎不返回时为 <c>null</c>。</summary>
    public string? DetectedFrom { get; init; }
    /// <summary>附加信息，UI 用来显示「来自缓存」「大模型翻译可能有偏差」之类。</summary>
    public string? Note { get; init; }
}

/// <summary>构造好的 HTTP 调用。所有引擎归一化成这一种形状。</summary>
public sealed class HttpCall
{
    public HttpCall(string method, string url)
    {
        Method = method;
        Url = url;
    }

    public string Method { get; }
    public string Url { get; }

    /// <summary>
    /// 请求头。<b>不要</b>把 content-type 放进来 —— 它是内容头，不是请求头。
    /// </summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>内容类型，单独一路发往 <c>HttpContent.Headers</c>。</summary>
    public string? ContentType { get; set; }

    public string? Body { get; set; }

    public static HttpCall Get(string url) => new("GET", url);
    public static HttpCall Post(string url) => new("POST", url);

    public HttpCall WithHeader(string name, string value)
    {
        // content-type 走 ContentType 字段。放这里会导致 .NET 同时在请求头和
        // 内容头里各发一份，服务端模型绑定失败（实测 Edge 返回 400
        // "The input was not valid"），而且所有走 body 的引擎都会中招。
        if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
        {
            ContentType = value;
            return this;
        }
        Headers[name] = value;
        return this;
    }

    public HttpCall WithJsonBody<T>(T value)
    {
        Body = System.Text.Json.JsonSerializer.Serialize(value);
        return WithHeader("content-type", "application/json");
    }

    /// <summary>body 发送时用的 UTF-8 字节内容。</summary>
    public byte[] BodyBytes => Body is null ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(Body);

    public HttpCall WithFormBody(params (string Key, string Value)[] pairs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in pairs)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        Body = sb.ToString();
        return WithHeader("content-type", "application/x-www-form-urlencoded");
    }
}

/// <summary>引擎配置项。UI 据此渲染设置面板，不需要硬编码任何引擎名。</summary>
public sealed class EngineConfig
{
    public string? Key { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();

    public string? Get(string key) => Extra.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public EngineConfig Clone() => new()
    {
        Key = Key,
        BaseUrl = BaseUrl,
        Extra = new Dictionary<string, string>(Extra),
    };
}

public static class EngineIds
{
    /// <summary>微软 Edge 内置翻译。免 Key，国内可直连，当前默认引擎。</summary>
    public const string Edge = "edge";
    public const string Google = "google";
    /// <summary>已废弃：微软 2026-08 下线了免费 auth 端点，保留 id 仅为兼容旧配置。</summary>
    public const string Bing = "bing";
    public const string DeepL = "deepl";
    public const string OpenAi = "openai";
    public const string Custom = "custom";
}

public sealed record EngineInfo(string Id, string Name, bool RequiresKey);

/// <summary>统一错误类型。面向 UI 的文案在这里统一，Rust/前端各自归类。</summary>
public class TranslateException : Exception
{
    public string Kind { get; }
    /// <summary>401/403 通常意味着 Key 无效，UI 可以据此引导去设置页。</summary>
    public bool IsAuthError { get; }

    public TranslateException(string kind, string message, bool isAuth = false) : base(message)
    {
        Kind = kind;
        IsAuthError = isAuth;
    }

    public static TranslateException Config(string msg) => new("config", msg);
    public static TranslateException Parse(string msg) => new("parse", msg);
    public static TranslateException Network(string msg) => new("network", msg);
    public static TranslateException MissingKey => new("config", "该引擎需要先配置 API Key");
    public static TranslateException Http(int status, string body) =>
        new("http", $"HTTP {status}：{Trim(body)}", status is 401 or 403);

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}

/// <summary>翻译引擎的抽象接口。只描述「怎么构造请求」和「怎么读响应」两件纯函数的事。</summary>
public interface ITranslateEngine
{
    string Id { get; }
    string DisplayName { get; }
    bool RequiresKey { get; }

    /// <summary>构造 HTTP 调用。必须是纯函数：同输入同输出，不碰网络、不读时钟。</summary>
    HttpCall Build(TranslateRequest req);

    /// <summary>解析响应体。必须是纯函数，便于用固定样本做回归测试。</summary>
    TranslateResponse Parse(string body);
}

/// <summary>共享的引擎基类，负责 HTTP 往返。引擎一般只需要实现两个纯函数。</summary>
/// <remarks>
/// 刻意不用「默认接口方法」来共享实现 —— DIM 的 override 规则相当微妙
/// （实现类要覆写必须同时标 <c>virtual</c> 和 <c>override</c>），用抽象基类更不容易踩坑。
/// </remarks>
public abstract class TranslateEngineBase : ITranslateEngine
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract bool RequiresKey { get; }
    public abstract HttpCall Build(TranslateRequest req);
    public abstract TranslateResponse Parse(string body);

    /// <summary>完整流程：构造请求 → 发送 → 解析。只有中间这一步是不确定的。</summary>
    public virtual async Task<TranslateResponse> TranslateAsync(
        HttpClient client, TranslateRequest req, CancellationToken ct = default)
    {
        using var message = CreateMessage(Build(req));
        using var resp = await SendAsync(client, message, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw TranslateException.Http((int)resp.StatusCode, body);
        return Parse(body);
    }

    /// <summary>
    /// 把 <see cref="HttpCall"/> 变成真正的 <see cref="HttpRequestMessage"/>。
    /// </summary>
    /// <remarks>
    /// 抽出来是为了能单测「实际发出的请求长什么样」—— 光测 HttpCall 的结构
    /// 是不够的，之前就因为只测结构、没测真正发出的消息，
    /// content-type 双发这个 bug 漏了过去。
    /// <para>
    /// 关键点：content-type 必须走 <c>Content.Headers</c>。
    /// 往 <c>Request.Headers</c> 里塞一份、再让 StringContent 自带一份，
    /// .NET 会发出畸形的内容头，服务端模型绑定失败（Edge 端点返回 400
    /// "The input was not valid"），所有走 body 的引擎全部中招。
    /// </para>
    /// </remarks>
    public static HttpRequestMessage CreateMessage(HttpCall call)
    {
        var message = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
        foreach (var (k, v) in call.Headers)
            message.Headers.TryAddWithoutValidation(k, v);
        if (call.Body is not null)
        {
            message.Content = new ByteArrayContent(call.BodyBytes);
            if (!string.IsNullOrEmpty(call.ContentType))
            {
                var ct = new System.Net.Http.Headers.MediaTypeHeaderValue(call.ContentType);
                if (ct.CharSet is null) ct.CharSet = "utf-8";
                message.Content.Headers.ContentType = ct;
            }
        }
        return message;
    }

    /// <summary>发请求并把 HTTP 层异常翻译成 <see cref="TranslateException"/>。</summary>
    protected static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslateException("network", "请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw TranslateException.Network($"网络请求失败：{ex.Message}");
        }
    }
}
