namespace Lumo.Core;

/// <summary>语言检测结果。<see cref="Confidence"/> 是启发式打分，不是概率。</summary>
public sealed record Detection(string? Code, float Confidence);

/// <summary>
/// 语言检测。完全离线、纯字符统计，微秒级延迟，不引入任何模型权重。
/// </summary>
/// <remarks>
/// 策略：先按文字系统分流，再在同文字系统内用字符特征 + 停用词打分消歧。
/// 文本很短时（1-3 个字符）置信度会明显下降，调用方应据此决定是否走 <c>auto</c> 目标语言推断。
/// </remarks>
public static class Lang
{
    private enum Script
    {
        Han, Kana, Hangul, Cyrillic, Arabic, Greek, Hebrew, Thai, Devanagari,
        Latin, Digit, Space, Other,
    }

    /// <summary>Script 的变体数量。加变体时这里要同步改。</summary>
    private const int ScriptCount = 13;

    // 端点一律用 \uXXXX 显式转义写进源码，不用字符字面量。
    // 踩过的坑：把 U+F900 写成 U+8C48，汉字范围被撑到 FAFF，
    // 整个谚文区 AC00-D7AF 被吞进去，韩语被误判成中文。转义后一眼可查。
    private static Script ScriptOf(char c) => c switch
    {
        // CJK 统一表意文字 + 扩展A + 兼容汉字
        >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF'
            => Script.Han,

        // 平假名 / 片假名 / 语音符号
        >= '\u3040' and <= '\u309F'
            or >= '\u30A0' and <= '\u30FF'
            or >= '\u31F0' and <= '\u31FF'
            => Script.Kana,

        // 谚文音节 / 谚文字母 / 谚文兼容字母
        >= '\uAC00' and <= '\uD7AF'
            or >= '\u1100' and <= '\u11FF'
            or >= '\u3130' and <= '\u318F'
            => Script.Hangul,

        // 西里尔字母 + 西里尔补充
        >= '\u0400' and <= '\u04FF'
            or >= '\u0500' and <= '\u052F'
            => Script.Cyrillic,

        // 阿拉伯文。阿拉伯-印度数字 0600-0669 也在此区间内，一并归为阿拉伯。
        >= '\u0600' and <= '\u06FF'
            or >= '\u0750' and <= '\u077F'
            or >= '\uFB50' and <= '\uFDFF'
            => Script.Arabic,

        // 希腊字母
        >= '\u0370' and <= '\u03FF'
            or >= '\u1F00' and <= '\u1FFF'
            => Script.Greek,

        // 希伯来文
        >= '\u0590' and <= '\u05FF'
            => Script.Hebrew,

        // 泰文
        >= '\u0E00' and <= '\u0E7F'
            => Script.Thai,

        // 天城文
        >= '\u0900' and <= '\u097F'
            => Script.Devanagari,

        // 拉丁字母 + 扩展
        >= '\u0041' and <= '\u005A'
            or >= '\u0061' and <= '\u007A'
            or >= '\u00C0' and <= '\u024F'
            => Script.Latin,

        // ASCII 数字
        >= '\u0030' and <= '\u0039'
            => Script.Digit,

        _ when char.IsWhiteSpace(c) => Script.Space,
        _ => Script.Other,
    };


    private static readonly (string Code, string[] Words)[] StopWords =
    {
        ("en", new[]{"the","and","is","of","to","in","that","it","you","for","with","this","are","was","have","not"}),
        ("es", new[]{"de","la","que","el","en","los","las","del","por","con","para","una","es","se","no","su"}),
        ("fr", new[]{"le","la","les","de","des","du","et","est","un","une","que","qui","dans","pour","pas","ce"}),
        ("de", new[]{"der","die","das","und","ist","ein","eine","nicht","mit","von","zu","den","für","auf","es","sich"}),
        ("pt", new[]{"de","que","não","uma","para","com","como","mais","por","dos","das","ao","ele","ela","são"}),
        ("it", new[]{"di","che","il","la","non","per","con","una","sono","come","anche","questo","gli","del","della"}),
        ("nl", new[]{"de","het","een","van","en","is","dat","niet","met","op","voor","aan","zijn","er","maar"}),
        ("pl", new[]{"nie","jest","się","na","to","że","do","za","jak","ale","od","po","przez","który"}),
        ("tr", new[]{"bir","bu","için","ile","de","da","çok","olarak","daha","ne","var","gibi","kadar","sonra"}),
        ("id", new[]{"yang","dan","di","untuk","dengan","ini","dari","tidak","akan","pada","adalah","itu","oleh","sebagai"}),
        ("vi", new[]{"của","và","các","có","được","trong","người","một","những","cho","không","này","với","là"}),
    };

    private static readonly (string Code, char[] Chars)[] Diacritics =
    {
        ("fr", "çœèêûîïùÿ".ToCharArray()),
        ("de", "ßäöü".ToCharArray()),
        ("es", "ñ¿¡".ToCharArray()),
        ("pt", "ãõâê".ToCharArray()),
        ("pl", "łżźćńśąę".ToCharArray()),
        ("tr", "ğışıçöü".ToCharArray()),
        ("vi", "ơưạảấầẩẫậắằẳẵặếềểễệịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ".ToCharArray()),
    };

    private static readonly HashSet<string> LatinLangs =
        new() { "en", "es", "fr", "de", "pt", "it", "nl", "pl", "tr", "id", "vi" };

    public static Detection Detect(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new Detection(null, 0f);

        var counts = new int[ScriptCount];
        var total = 0;
        foreach (var c in trimmed)
        {
            var s = ScriptOf(c);
            counts[(int)s]++;
            if (s is not (Script.Space or Script.Other)) total++;
        }
        if (total == 0) return new Detection(null, 0f);

        int N(Script s) => counts[(int)s];

        // 数字占比过高通常是金额、版本号、编号，没有语言归属。
        if (N(Script.Digit) * 2 >= total) return new Detection(null, 0f);

        // 用手工切分而不是 string.Split 的委托重载 —— 后者只有 char/c char[] 版本。
        var words = new List<string>();
        foreach (var token in trimmed.ToLowerInvariant().Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new System.Text.StringBuilder(token.Length);
            foreach (var ch in token)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) words.Add(sb.ToString());
        }

        // 1) 日文。关键判断是「汉字里混了假名」—— 中文永远不出现假名，
        //    所以只要 CJK 字符里假名占到一定比例就一定是日文。
        //    之前这里要求假名 >= 总字符数的 50%，太脆弱：正常日文假名常在 45% 上下，
        //    汉字稍多一点的文章就掉进下面的汉字分支，被误判成中文。
        //    现在改成「假名 / (假名+汉字) >= 10%」，这个线怎么写都碰不到中文：
        //    中文文本里出现 10% 以上的假名在实践中不存在。
        //    （10% 这条线是为了覆盖法令/地址这类汉字极多的正式日文。）
        var kana = N(Script.Kana);
        var han = N(Script.Han);
        if (kana > 0 && kana * 10 >= kana + han)
        {
            var share = (float)kana / (kana + han);
            return new Detection("ja", 0.55f + share * 0.35f);
        }

        // 2) 谚文优先于汉字。
        if (N(Script.Hangul) > 0 && N(Script.Hangul) * 4 >= total)
            return new Detection("ko", 0.6f + (float)N(Script.Hangul) / total * 0.35f);

        // 3) 汉字。走到这里基本可以确定是中文了。
        if (N(Script.Han) > 0)
            return new Detection("zh", 0.5f + (float)N(Script.Han) / total * 0.45f);

        // 4) 单一文字系统直接判定
        foreach (var (script, code) in new[]
                 {
                     (Script.Hangul, "ko"), (Script.Cyrillic, "ru"), (Script.Arabic, "ar"),
                     (Script.Greek, "el"), (Script.Hebrew, "he"), (Script.Thai, "th"),
                     (Script.Devanagari, "hi"),
                 })
        {
            if (N(script) * 2 >= total)
                return new Detection(code, 0.6f + (float)N(script) / total * 0.35f);
        }

        // 5) 拉丁字母 —— 打分消歧
        var latin = N(Script.Latin);
        if (latin * 3 >= total)
        {
            if (latin == 0) return new Detection(null, 0f);

            string? bestCode = null;
            var best = 0f;
            var runnerUp = 0f;

            void Offer(string code, float score)
            {
                if (score > runnerUp) runnerUp = score;
                if (bestCode is null || score > best)
                {
                    bestCode = code;
                    best = score;
                }
            }

            foreach (var (code, chars) in Diacritics)
            {
                var set = new HashSet<char>(chars);
                var hits = trimmed.Count(c => set.Contains(c));
                if (hits == 0) continue;
                Offer(code, Math.Min(hits / (float)latin, 1f) * 3f);
            }

            foreach (var (code, stops) in StopWords)
            {
                var hits = words.Count(w => stops.Contains(w));
                if (hits == 0) continue;
                // 短文本里一个停用词就很有说服力，长文本里需要更多。
                var expected = words.Count switch { <= 3 => 1.5f, <= 12 => 3f, _ => 6f };
                Offer(code, Math.Min(hits / expected, 1f) * 2f);
            }

            if (bestCode is not null)
            {
                // 与第二名拉开差距才给高置信度，否则标低，避免下游误判。
                var margin = Math.Max(best - runnerUp, 0f);
                var conf = Math.Min(0.35f + best * 0.15f + margin * 0.12f, 0.95f);
                return new Detection(bestCode, conf);
            }
            return new Detection("en", 0.3f);
        }

        return new Detection(null, 0f);
    }

    /// <summary>
    /// 某个语言码对应的「文字系统指纹」：出现哪些字符算这个语言。
    /// </summary>
    private static bool BelongsTo(string code, Script script) => code switch
    {
        "zh" => script == Script.Han,
        "zh-Hant" => script == Script.Han,
        "ja" => script is Script.Han or Script.Kana,
        "ko" => script == Script.Hangul,
        "ru" or "uk" => script == Script.Cyrillic,
        "ar" => script == Script.Arabic,
        "el" => script == Script.Greek,
        "he" => script == Script.Hebrew,
        "th" => script == Script.Thai,
        "hi" => script == Script.Devanagari,
        _ => script == Script.Latin,
    };

    /// <summary>
    /// 文本是否「纯粹」是某一种语言。
    /// </summary>
    /// <remarks>
    /// 用来决定能不能跳过翻译。检测器只会给出一个主导语言，
    /// 而「中文里夹着英文术语和西语」的文本检测结果必然是 zh ——
    /// 如果仅凭检测结果就短路，整段会被丢掉，里面该翻的部分根本没发出去。
    ///
    /// 所以判据不能是「检测结果 == 目标语言」，而必须是
    /// 「文本里几乎没有别的语言系统的字符」。
    /// 日文算半个自己人：日文文本里汉字占比本来就高。
    /// </remarks>
    public static bool IsPurely(string text, string code)
    {
        if (string.IsNullOrWhiteSpace(text) || code is "auto" or "") return false;

        var own = 0;
        var foreign = 0;
        foreach (var c in text)
        {
            var s = ScriptOf(c);
            if (s is Script.Space or Script.Digit or Script.Other) continue;
            if (BelongsTo(code, s)) own++;
            else foreign++;
        }

        if (own + foreign == 0) return false;

        // 阈值刻意放宽到 15%：宁可多发一次请求，也不要把该翻的整段丢掉。
        // 两类误判的代价完全不对等 ——
        //   误判为「不纯」→ 多一次网络请求，浪费几十毫秒和一点配额
        //   误判为「纯」  → 混合语言整段不翻译，功能直接不可用
        //
        // 15% 意味着「纯中文 + 一个 iPhone」也会照发（外来字符约 40%）。
        // 这是有意的取舍：省一次调用不值得拿功能可用性去换。
        return foreign * 100 <= (own + foreign) * 15;
    }

    public static string DetectOr(string text, string fallback) => Detect(text).Code ?? fallback;

    /// <summary>归一化语言输入：接受 <c>zh-CN</c>、<c>Chinese</c>、<c>中文</c> 等等。</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant().Replace('_', '-');
        var code = s switch
        {
            "chinese" or "中文" or "简体中文" or "简中" or "mandarin" => "zh",
            "traditional" or "繁体中文" or "繁體中文" or "繁中" => "zh-Hant",
            "japanese" or "日本語" or "日语" => "ja",
            "korean" or "한국어" or "韩语" or "韩文" => "ko",
            "english" or "英文" or "英语" => "en",
            "french" or "français" => "fr",
            "german" or "deutsch" => "de",
            "spanish" or "español" => "es",
            "portuguese" or "português" => "pt",
            "italian" or "italiano" => "it",
            "russian" or "русский" => "ru",
            "ukrainian" or "українська" => "uk",
            "dutch" or "nederlands" => "nl",
            "polish" or "polski" => "pl",
            "turkish" or "türkçe" => "tr",
            "vietnamese" or "tiếng việt" => "vi",
            "indonesian" or "bahasa indonesia" => "id",
            "thai" or "ไทย" => "th",
            "arabic" or "العربية" => "ar",
            "hebrew" => "he",
            "greek" => "el",
            "hindi" or "हिन्दी" => "hi",
            "auto" or "detect" or "自动检测" or "自动" => "auto",
            _ => s.Split('-')[0],
        };
        return string.IsNullOrEmpty(code) ? null : code;
    }

    public static string DisplayName(string code) => code switch
    {
        "zh" or "zh-Hans" => "简体中文",
        "zh-Hant" => "繁体中文",
        "en" => "英语",
        "ja" => "日语",
        "ko" => "韩语",
        "fr" => "法语",
        "de" => "德语",
        "es" => "西班牙语",
        "pt" => "葡萄牙语",
        "it" => "意大利语",
        "ru" => "俄语",
        "uk" => "乌克兰语",
        "nl" => "荷兰语",
        "pl" => "波兰语",
        "tr" => "土耳其语",
        "vi" => "越南语",
        "id" => "印尼语",
        "th" => "泰语",
        "ar" => "阿拉伯语",
        "he" => "希伯来语",
        "el" => "希腊语",
        "hi" => "印地语",
        "auto" => "自动检测",
        _ => code,
    };

    public static readonly (string Code, string Name)[] Common =
    {
        ("auto", "自动检测"), ("zh", "简体中文"), ("zh-Hant", "繁体中文"), ("en", "英语"),
        ("ja", "日语"), ("ko", "韩语"), ("fr", "法语"), ("de", "德语"), ("es", "西班牙语"),
        ("pt", "葡萄牙语"), ("it", "意大利语"), ("ru", "俄语"), ("uk", "乌克兰语"),
        ("nl", "荷兰语"), ("pl", "波兰语"), ("tr", "土耳其语"), ("vi", "越南语"),
        ("id", "印尼语"), ("th", "泰语"), ("ar", "阿拉伯语"), ("he", "希伯来语"),
        ("el", "希腊语"), ("hi", "印地语"),
    };
}
