using System.Diagnostics;
using System.Net;
using Lumo.Core.Engines;

namespace Lumo.Core;

/// <summary>一次翻译的完整结果，含「最终是谁干的」这类元信息。</summary>
public sealed record TranslateOutcome
{
    public required string SourceText { get; init; }
    public string? TargetText { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required string Engine { get; init; }
    public bool Cached { get; init; }
    public string? Note { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>主引擎失败时尝试过的引擎，供 UI 提示。</summary>
    public IReadOnlyList<string> Attempts { get; init; } = Array.Empty<string>();
}

/// <summary>翻译编排层：检测语言 → 查缓存 → 依次尝试引擎 → 回退。</summary>
public sealed class Translator
{
    private readonly Lock _gate = new();
    private AppConfig _config;
    private TranslationCache _cache;
    private HttpClient _client;

    public Translator(AppConfig config)
    {
        _config = config.Sanitized();
        _cache = new TranslationCache(_config.CacheCapacity, _config.CacheTtl);
        _client = BuildHttpClient(_config);
    }

    private static HttpClient BuildHttpClient(AppConfig cfg)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        if (!string.IsNullOrWhiteSpace(cfg.Proxy) &&
            Uri.TryCreate(cfg.Proxy, UriKind.Absolute, out var proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.HttpTimeoutSecs, 3, 120)),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    public const string UserAgent = "Lumo/1.0 (Windows; translate client)";

    public AppConfig Config
    {
        get { lock (_gate) return _config; }
    }

    /// <summary>替换配置。缓存保留不清空——换语言方向不该丢已翻译的词条。</summary>
    public void UpdateConfig(AppConfig config)
    {
        lock (_gate)
        {
            var sanitized = config.Sanitized();
            var proxyChanged = sanitized.Proxy != _config.Proxy ||
                               sanitized.HttpTimeoutSecs != _config.HttpTimeoutSecs;
            if (proxyChanged)
            {
                _client.Dispose();
                _client = BuildHttpClient(sanitized);
            }
            _config = sanitized;
            _cache = new TranslationCache(sanitized.CacheCapacity, sanitized.CacheTtl);
        }
    }

    public (long Hits, long Misses, int Entries, int Bytes, double HitRate) CacheStats()
    {
        lock (_gate) return _cache.Stats();
    }

    public void ClearCache()
    {
        lock (_gate) _cache.Clear();
    }

    public Detection Detect(string text) => Lang.Detect(text);

    public async Task<TranslateOutcome> TranslateAsync(
        string text, string? from, string? to, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) throw TranslateException.Config("待翻译文本为空");

        AppConfig cfg;
        HttpClient client;
        TranslationCache cache;
        lock (_gate)
        {
            cfg = _config;
            client = _client;
            cache = _cache;
        }

        // 1) 定源语言
        //    检测置信度不足时**不猜**，改用 auto 交给服务端判断。
        //    这很关键：我的检测是启发式的（靠字符范围 + 停用词），
        //    服务端是训练过的模型。像「buscar conocimiento」这种不含
        //    功能词的西语句子，本地会猜成英语；猜错之后硬发 from=en，
        //    服务拿到「按英语翻译的西语」，只能原样返回 —— 表现为"翻译了但没变"。
        var source = string.IsNullOrEmpty(from) || from == "auto"
            ? DetectOrDeferToServer(text)
            : Lang.Normalize(from) ?? from;

        // 2) 定目标语言
        string target;
        if (string.IsNullOrEmpty(to) || to == "auto")
        {
            target = cfg.AutoTarget
                ? OppositeOf(source) ?? cfg.TargetLang
                : cfg.TargetLang;
        }
        else
        {
            target = Lang.Normalize(to) ?? to;
        }

        // 短路条件比「检测结果 == 目标语言」严格得多。
        // 原文全是目标语言时跳过请求没毛病；但混合语言（中英日西混排，
        // 检测必然落到主导语言）如果照跳，整段会被丢掉，里面该翻的部分
        // 根本不会发出去。所以额外要求文本「几乎不含」别的语言系统。
        var looksSameLanguage = source != "auto" && source == target;
        if (looksSameLanguage && !cfg.AutoCopy && Lang.IsPurely(text, source))
        {
            return new TranslateOutcome
            {
                SourceText = text, TargetText = null, Source = source, Target = target,
                Engine = "none", Note = "原文与目标语言相同，未发送请求",
            };
        }

        // 混合语言且检测结果和目标相同：不能把「猜错的源语言」强加给服务。
        // 比如「中文为主 + 夹一段英文」，检测必然是 zh，硬发 from=zh to=zh-Hans
        // 服务端会原样返回 —— 请求发出了但等于没发。改成不指定源语言，
        // 让服务逐段自己判断。
        var sendAutoSource = looksSameLanguage;

        // 混合语言提示。翻译 API 把整段当**一种**语言处理（源语言只能有一个），
        // 所以「中文为主 + 夹一段西语」这种文本，服务端会按中文理解并原样返回。
        // 这不是程序没发请求，而是单次调用做不到分段翻译。
        // 与其让用户对着不变的译文纳闷，不如说清楚，并指出可以手动指定源语言。
        var mixedNote = source != "auto" && !Lang.IsPurely(text, source)
            ? $"检测到多种语言混排（已按{Lang.DisplayName(source)}处理）。" +
              "整段翻译只能选一种源语言，如果正文其实是外语，请在左上手动指定。"
            : null;

        var req = new TranslateRequest
        {
            Text = text,
            From = sendAutoSource ? "auto" : source,
            To = target,
        };

        var sw = Stopwatch.StartNew();
        var attempts = new List<string>();

        // 混合语言：整段交给服务只能得到「按主导语言处理」的结果（多半原样返回）。
        // 改成按语言切段，只翻非目标语言的成规模片段，其余原样保留。
        // 单次 API 调用做不到分段翻译，这是唯一的办法。
        if (sendAutoSource || mixedNote is not null)
        {
            var mixed = await MixedTranslator.TranslateAsync(
                text, target, TranslateSegmentAsync, ct).ConfigureAwait(false);
            if (mixed.DidAnything)
            {
                sw.Stop();
                return new TranslateOutcome
                {
                    SourceText = text, TargetText = mixed.Text,
                    Source = source, Target = target,
                    Engine = "mixed",
                    Note = $"混合语言：{mixed.TranslatedRuns} 段已翻译，{mixed.KeptRuns} 段保留原文（术语/品牌名）",
                    ElapsedMs = sw.ElapsedMilliseconds, Attempts = attempts,
                };
            }
        }

        // 3) 查缓存
        foreach (var engineId in cfg.EngineChain())
        {
            var hit = cache.Get(TranslationCache.KeyOf(engineId, req));
            if (hit is not null)
            {
                return new TranslateOutcome
                {
                    SourceText = text, TargetText = hit, Source = source, Target = target,
                    Engine = engineId, Cached = true, Note = "来自本地缓存",
                    ElapsedMs = sw.ElapsedMilliseconds, Attempts = attempts,
                };
            }
        }

        // 4) 逐个引擎尝试
        //    记下每一次的失败原因，而不是只留最后一个：
        //    只报最后一条的话，用户看到的是「取必应令牌失败：」，
        //    完全不知道主引擎 Google 为什么先挂了。
        var failures = new List<(string Engine, TranslateException Error)>();
        foreach (var engineId in cfg.EngineChain())
        {
            var engine = EngineFactory.Create(engineId, cfg.EngineFor(engineId));
            if (engine is null)
            {
                failures.Add((engineId, new TranslateException("unknown_engine", $"未找到翻译引擎 `{engineId}`")));
                continue;
            }
            attempts.Add(engineId);

            try
            {
                var resp = await engine.TranslateAsync(client, req, ct).ConfigureAwait(false);
                sw.Stop();

                if (string.IsNullOrEmpty(resp.Text))
                    resp = resp with { Note = resp.Note ?? "引擎返回空结果" };

                // 只有成功的结果才进缓存。
                if (!string.IsNullOrEmpty(resp.Text))
                    cache.Put(TranslationCache.KeyOf(engineId, req), resp.Text!);

                return new TranslateOutcome
                {
                    SourceText = text, TargetText = resp.Text,
                    Source = resp.DetectedFrom ?? source, Target = target,
                    Engine = engineId,
                    // 引擎自己的备注优先；没有就补上混合语言提示
                    Note = resp.Note ?? mixedNote,
                    ElapsedMs = sw.ElapsedMilliseconds, Attempts = attempts,
                };
            }
            catch (TranslateException ex)
            {
                // 鉴权和配置类错误换引擎也没用，直接返回。
                if (ex.IsAuthError || (ex.Kind == "config" && ex.Message.Contains("API Key")))
                    throw;
                failures.Add((engineId, ex));
            }
        }

        if (failures.Count == 0)
            throw new TranslateException("unknown_engine", "没有可用的翻译引擎");

        throw new TranslateException(
            failures[0].Error.Kind,
            SummarizeFailures(failures),
            failures.Any(f => f.Error.IsAuthError));
    }

    /// <summary>
    /// 检测源语言；置信度不够就返回 <c>auto</c>，让服务端自己判断。
    /// </summary>
    /// <remarks>
    /// 本地检测的准确率对长文本还行，对短文本很不靠谱 —— 短文本往往不含
    /// 足够的功能词来消歧。而「猜错源语言」的代价比「不知道源语言」大得多：
    /// 猜错会硬发一个错误的 from，服务端只能原样返回；返回 auto 则由服务端
    /// 用训练过的模型判断，结果只会更好。
    /// </remarks>
    private static string DetectOrDeferToServer(string text)
    {
        var d = Lang.Detect(text);
        if (d.Code is null) return "auto";
        return d.Confidence >= MinDetectConfidence ? d.Code : "auto";
    }

    /// <summary>低于这个置信度就放弃本地判断，交给服务端。</summary>
    private const float MinDetectConfidence = 0.5f;

    /// <summary>
    /// 分段翻译时用来翻单个片段。
    /// </summary>
    /// <remarks>
    /// 片段已经确定是「非目标语言」，所以不传 from，让服务自己判断。
    /// 失败返回 null，调用方会保留原文 —— 一个片段翻不了不该毁掉整篇。
    /// </remarks>
    private async Task<string?> TranslateSegmentAsync(string segment, string target, CancellationToken ct)
    {
        try
        {
            var outcome = await TranslateAsync(segment, "auto", target, ct).ConfigureAwait(false);
            return outcome.TargetText;
        }
        catch (TranslateException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把多个引擎的失败聚合成一条可读的信息。
    /// </summary>
    /// <remarks>
    /// 网络类错误（超时、被墙、DNS）往往返回空 body，只显示「取必应令牌失败：」
    /// 这种空壳对用户毫无意义。这里带上引擎名，并对空消息给出可操作的提示。
    /// </remarks>
    private static string SummarizeFailures(List<(string Engine, TranslateException Error)> failures)
    {
        if (failures.Count == 1)
        {
            var (only, err) = failures[0];
            var detail = Describe(err);
            return detail.Length == 0
                ? $"引擎 {only} 翻译失败（服务无响应）"
                : $"引擎 {only} 翻译失败：{detail}";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"全部 {failures.Count} 个引擎都失败了：");
        for (var i = 0; i < failures.Count; i++)
        {
            var (engine, err) = failures[i];
            sb.Append(i == 0 ? "\n· " : "\n· ");
            sb.Append(engine).Append('：').Append(Describe(err));
        }
        sb.Append("\n可以在「设置」里换一个引擎，或检查网络/代理配置。");
        return sb.ToString();
    }

    /// <summary>把引擎返回的原始错误整理成一句能看懂的话。</summary>
    private static string Describe(TranslateException err)
    {
        // 很多服务失败时返回空 body，结果就是「XX失败：」后面什么都没有。
        // 原始消息以冒号结尾 => 冒号后无内容 => 对用户毫无信息量，补一句可操作的。
        var msg = err.Message.Trim();
        var uninformative = msg.Length == 0
                            || msg.EndsWith('：')
                            || msg.EndsWith(':')
                            || msg == err.GetType().Name;
        if (uninformative)
            return "服务无响应（可能是网络不通、被墙或服务已失效）";

        if (msg.Length > 200) msg = msg[..200] + "…";
        return msg;
    }

    /// <summary>「自动决定目标语言」用的对照表。只覆盖高频组合，不做穷举。</summary>
    internal static string? OppositeOf(string source) => source switch
    {
        "zh" or "zh-Hant" or "ja" or "ko" or "vi" or "th" or "id" => "en",
        "en" or "fr" or "de" or "es" or "pt" or "it" or "nl" or "pl" or "tr" or "ru" or "uk" or "ar" or "he" => "zh",
        // 检测不出来就不要乱猜，沿用配置里的目标语言。
        _ => null,
    };
}

/// <summary>屏幕区域（物理像素）。</summary>
public readonly record struct Region(int X, int Y, int Width, int Height)
{
    /// <summary>面积过小的框选多半是误操作，直接拒绝，省掉一次无意义的 OCR。</summary>
    public bool IsPlausible => Width >= 24 && Height >= 12;

    /// <summary>像素总数。用 long 避免 4K 多屏拼接时溢出。</summary>
    public long Pixels => (long)Width * Height;

    /// <summary>超过这个尺寸就该提示缩小范围——OCR 耗时和面积基本线性相关。</summary>
    public bool IsOversized => Pixels > 12_000_000;
}

/// <summary>BMP 编码。纯计算，任意平台可测。</summary>
public static class Bmp
{
    private const int FileHeader = 14;
    private const int InfoHeader = 40;

    /// <summary>
    /// 把 BGRA 像素编码成 24 位 BMP。
    /// 选 24 位而不是 32 位：体积少 25%，而 OCR 只用亮度信息，多出来那个字节是纯浪费。
    /// </summary>
    public static byte[] EncodeBgra(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0) throw TranslateException.Config("图像尺寸无效");
        var expected = width * height * 4;
        if (pixels.Length < expected)
            throw TranslateException.Config($"像素数据不足：需要 {expected} 字节，实际 {pixels.Length}");

        // 每行按 4 字节对齐。BMP 要求每条扫描线从 4 字节边界开始。
        var rowSize = (width * 3 + 3) / 4 * 4;
        var imageSize = rowSize * height;
        var pixelOffset = FileHeader + InfoHeader;
        var total = pixelOffset + imageSize;

        var outBuf = new byte[total];

        // BITMAPFILEHEADER
        outBuf[0] = (byte)'B';
        outBuf[1] = (byte)'M';
        WriteU32(outBuf, 2, total);
        WriteU32(outBuf, 6, 0);
        WriteU32(outBuf, 10, pixelOffset);

        // BITMAPINFOHEADER
        WriteU32(outBuf, 14, 40);
        WriteU32(outBuf, 18, width);
        WriteU32(outBuf, 22, height);   // 正数 = 行序自下而上
        WriteU16(outBuf, 26, 1);        // planes
        WriteU16(outBuf, 28, 24);       // bpp
        WriteU32(outBuf, 30, 0);        // BI_RGB
        WriteU32(outBuf, 34, imageSize);
        WriteU32(outBuf, 38, 2835);     // 72 DPI
        WriteU32(outBuf, 42, 2835);
        WriteU32(outBuf, 46, 0);
        WriteU32(outBuf, 50, 0);

        // 像素：BGRA → BGR，行序翻转
        // 关键：每写完一行必须按 rowSize（而不是 width*3）跳到下一行，
        // 否则 4 字节对齐的填充会被下一行的像素覆盖。
        for (var outRow = 0; outRow < height; outRow++)
        {
            var srcRow = height - 1 - outRow;
            var srcBase = srcRow * width * 4;
            var dst = pixelOffset + outRow * rowSize;
            for (var x = 0; x < width; x++)
            {
                outBuf[dst++] = pixels[srcBase + x * 4];
                outBuf[dst++] = pixels[srcBase + x * 4 + 1];
                outBuf[dst++] = pixels[srcBase + x * 4 + 2];
            }
            // 行尾补齐字节保持 0（outBuf 已整体清零）。
        }
        return outBuf;
    }

    private static void WriteU32(byte[] b, int o, int v) =>
        BitConverter.TryWriteBytes(b.AsSpan(o, 4), v);

    private static void WriteU16(byte[] b, int o, int v) =>
        BitConverter.TryWriteBytes(b.AsSpan(o, 2), (ushort)v);
}
