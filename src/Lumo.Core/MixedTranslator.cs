using System.Text;

namespace Lumo.Core;

/// <summary>文本片段的粗分类。</summary>
public enum TextScript
{
    /// <summary>空白、标点、数字 —— 中性字符，附着在相邻片段上，本身不构成片段。</summary>
    Neutral,
    /// <summary>表意文字（汉字）。</summary>
    Cjk,
    /// <summary>日文假名。<b>必须和汉字分开</b> —— 目标语言是中文时，
    /// 「旅の途中」里混着假名，是日语，不该被当成「已经是中文」保留。</summary>
    Kana,
    /// <summary>拉丁字母（含带变音符号的欧洲语言）。</summary>
    Latin,
    /// <summary>西里尔字母。</summary>
    Cyrillic,
    /// <summary>阿拉伯、希伯来、希腊、泰文、天城文等其他有独立字母表的文字。</summary>
    Other,
}

/// <summary>一段连续的同类文本。</summary>
public readonly record struct TextRun(string Text, TextScript Script)
{
    /// <summary>去掉首尾空白后的内容。</summary>
    public string Trimmed => Text.Trim();

    /// <summary>首尾空白 —— 拼回原文时必须原样保留，否则缩进和换行会丢。</summary>
    public string Leading => Text[..(Text.Length - Text.TrimStart().Length)];

    public string Trailing
    {
        get
        {
            var t = Text.TrimEnd();
            return Text[t.Length..];
        }
    }

    /// <summary>片段里是否含假名 —— 有就说明是日语，不是中文。</summary>
    public bool ContainsKana => Text.Any(c => c is >= '\u3040' and <= '\u30FF');

    /// <summary>词数（按空格切，西文用）。</summary>
    public int WordCount =>
        Trimmed.Length == 0 ? 0 : Trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// 混合语言的分段翻译。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>翻译 API 的源语言只能有一个。「中文为主 + 夹一段西语」
/// 这种文本，整段交给服务会按主导语言理解并原样返回 —— 该翻的部分根本没被翻。
/// 这是所有翻译 API 的共同限制，单次调用无法绕过。
/// </para>
/// <para>
/// <b>做法：</b>按文字系统把文本切成段，只翻「非目标语言且成规模」的片段，
/// 其余原样保留，最后按原顺序拼回。空格和标点按中性字符处理，附着在相邻片段上，
/// 所以换行和缩进能完整保留。
/// </para>
/// <para>
/// <b>关键取舍：</b>短片段不翻。<c>Windows</c>、<c>Lumo</c>、<c>OCR</c> 这类
/// 单词单独送去翻译会被译成「窗户」「光明」之类的错译。判据是词数/字数，
/// 而不是"是不是外语" —— 专有名词和术语留在原文才是对的。
/// </para>
/// </remarks>
public static class MixedTranslator
{
    /// <summary>非拉丁文字片段至少要这么多字符才值得翻。</summary>
    private const int MinOtherChars = 4;

    /// <summary>单字符把一个字符归到某类。</summary>
    public static TextScript Classify(char c)
    {
        var o = (int)c;
        return o switch
        {
            // 中性：空白、标点、数字、符号
            _ when char.IsWhiteSpace(c) => TextScript.Neutral,
            >= 0x30 and <= 0x39 => TextScript.Neutral,          // 0-9
            >= 0x21 and <= 0x2F => TextScript.Neutral,          // ! " # $ % & ' ( ) * + , - . /
            >= 0x3A and <= 0x40 => TextScript.Neutral,          // : ; < = > ? @
            >= 0x5B and <= 0x60 => TextScript.Neutral,          // [ \ ] ^ _ `
            >= 0x7B and <= 0x7E => TextScript.Neutral,          // { | } ~
            >= 0xA0 and <= 0xBF => TextScript.Neutral,          // Latin-1 符号 ¡ ¿ ° « » …
            >= 0x2000 and <= 0x206F => TextScript.Neutral,      // 通用标点 “ ” ‘ ’ — …
            >= 0x3000 and <= 0x303F => TextScript.Neutral,      // CJK 标点 。、！？（）「」
            >= 0xFF01 and <= 0xFF20 => TextScript.Neutral,      // 全角 ！？「」（）
            >= 0xFF3B and <= 0xFF40 => TextScript.Neutral,      // 全角 ；＜＝＞？
            >= 0xFF5B and <= 0xFF65 => TextScript.Neutral,      // 全角 ｛｜｝～

            >= 0x4E00 and <= 0x9FFF => TextScript.Cjk,
            >= 0x3400 and <= 0x4DBF => TextScript.Cjk,
            >= 0xF900 and <= 0xFAFF => TextScript.Cjk,
            >= 0x3040 and <= 0x30FF => TextScript.Kana,          // 平假名 / 片假名
            >= 0xAC00 and <= 0xD7AF => TextScript.Cjk,          // 谚文
            >= 0x1100 and <= 0x11FF => TextScript.Cjk,          // 谚文字母

            >= 0x41 and <= 0x5A => TextScript.Latin,
            >= 0x61 and <= 0x7A => TextScript.Latin,
            >= 0xC0 and <= 0x24F => TextScript.Latin,          // 带变音符号的欧洲语言
            >= 0x1E00 and <= 0x1EFF => TextScript.Latin,        // 拉丁扩展附加

            >= 0x0400 and <= 0x04FF => TextScript.Cyrillic,

            _ => TextScript.Other,
        };
    }

    /// <summary>
    /// 把文本切成同类片段。中性字符附着在所属片段上，首尾的中性字符独立成段。
    /// </summary>
    public static List<TextRun> Split(string text)
    {
        var runs = new List<TextRun>();
        if (text.Length == 0) return runs;

        var sb = new StringBuilder();
        var current = TextScript.Neutral;

        foreach (var c in text)
        {
            var s = Classify(c);
            if (s == TextScript.Neutral)
            {
                // 中性字符跟着当前片段走
                if (current == TextScript.Neutral) sb.Append(c);
                else sb.Append(c);
                continue;
            }

            if (s == current && sb.Length > 0 && current != TextScript.Neutral)
            {
                sb.Append(c);
                continue;
            }

            if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), current));
            sb.Clear();
            sb.Append(c);
            current = s;
        }
        if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), current));

        // 把相邻的同类片段（被中性字符隔开的）合并掉，减少无谓的请求
        var merged = new List<TextRun>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && merged[^1].Script == r.Script && r.Script != TextScript.Neutral)
                merged[^1] = new TextRun(merged[^1].Text + r.Text, r.Script);
            else
                merged.Add(r);
        }

        var joined = MergeKanaParticles(merged);

        // 二级切分：一个 CJK 片段内部可能还是「中+日」混排的
        // （例如「心情有点像“旅の途中”。」）。整段丢给服务，它会按中文理解并原样返回。
        // 所以把片段里的日文子串单独拎出来翻，中文部分原样保留。
        var refined = new List<TextRun>();
        foreach (var r in joined)
            refined.AddRange(r.Script == TextScript.Cjk && r.ContainsKana
                ? SplitJapaneseInside(r.Text)
                : new[] { r });

        // 紧跟在假名前面的汉字（「論文、見」+「つかった？」）也是日语 ——
        // 汉字单独看和中文一模一样，只有后面紧挨的假名能证明它是日语。
        //
        // 只能动「紧邻的那几个汉字」。若把整段都并进去，
        // 前面真正的中文（「问」）也会被误送去翻译。
        var merged2 = new List<TextRun>();
        for (var i = 0; i < refined.Count; i++)
        {
            var r = refined[i];
            var nextIsKana = i + 1 < refined.Count && refined[i + 1].Script == TextScript.Kana;
            if (r.Script != TextScript.Cjk || !nextIsKana)
            {
                merged2.Add(r);
                continue;
            }

            var hanStart = r.Text.Length;
            while (hanStart > 0 && Classify(r.Text[hanStart - 1]) == TextScript.Cjk) hanStart--;

            if (hanStart == r.Text.Length)
            {
                // 末尾不是汉字（隔着标点），不算紧邻，整段保持中文
                merged2.Add(r);
                continue;
            }

            if (hanStart > 0) merged2.Add(new TextRun(r.Text[..hanStart], TextScript.Cjk));
            // 汉字必须和后面的假名合成一整段发出去。
            // 单发「論文、見」的话，服务端看到的是纯汉字，判定为中文并原样返回 ——
            // 实测确实如此。带上后面的「つかった？」它才认得出这是日语。
            merged2.Add(new TextRun(r.Text[hanStart..] + refined[i + 1].Text, TextScript.Kana));
            i++;   // 下一段已被并入
        }
        return merged2;
    }

    /// <summary>
    /// 把一段「汉字+假名」混排的文本拆成中文部分和日文部分。
    /// </summary>
    /// <remarks>
    /// 日文块的定义：<b>一段假名 + 它前面紧挨的汉字 + 后面紧挨的汉字</b>。
    /// 「旅の途中」= 旅 + の + 途中 就是一个完整的日文块。
    /// 剩下的汉字当中文保留 —— 它们既可能是中文，也可能是被中性字符隔开的
    /// 另一段日语，交给下一轮的「紧跟假名」规则判断。
    /// </remarks>
    private static List<TextRun> SplitJapaneseInside(string text)
    {
        static bool IsKanaChar(char c) => c is >= '\u3040' and <= '\u30FF';
        static bool IsHanChar(char c) => Classify(c) == TextScript.Cjk;

        var result = new List<TextRun>();
        var han = new System.Text.StringBuilder();

        void FlushHan()
        {
            if (han.Length == 0) return;
            result.Add(new TextRun(han.ToString(), TextScript.Cjk));
            han.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            // 第一段汉字
            var hanStart = i;
            while (i < text.Length && IsHanChar(text[i])) i++;
            var hanEnd = i;

            // 假名
            var kanaStart = i;
            while (i < text.Length && IsKanaChar(text[i])) i++;
            var kanaEnd = i;

            // 第二段汉字（紧跟假名的）
            var afterStart = i;
            while (i < text.Length && IsHanChar(text[i])) i++;
            var afterEnd = i;

            if (kanaStart == kanaEnd)
            {
                // 没有假名：整段是中文候选
                if (hanEnd > hanStart) han.Append(text, hanStart, hanEnd - hanStart);
                // 中性字符（引号、标点）也要跟着走，否则会丢
                while (i < text.Length && !IsHanChar(text[i]) && !IsKanaChar(text[i]))
                {
                    han.Append(text[i]);
                    i++;
                }
                continue;
            }

            // 有假名 → 前一个汉字 + 假名 + 后一个汉字 合成日文块。
            // 前一个汉字只取紧邻的那部分，所以先把累积的中文冲刷出来。
            var beforeHan = hanEnd > hanStart ? text[hanStart..hanEnd] : "";
            var jStart = beforeHan.Length > 0 ? beforeHan.Length - 1 : 0;
            if (jStart > 0) han.Append(beforeHan, 0, jStart);
            FlushHan();

            var block = beforeHan[jStart..] + text[kanaStart..kanaEnd] + text[afterStart..afterEnd];
            result.Add(new TextRun(block, TextScript.Kana));
        }

        FlushHan();
        return result;
    }

    /// <summary>短的假名片段（助词）要并入相邻汉字，长的假名保持独立。</summary>
    /// <remarks>
    /// <para>
    /// 「の」「は」「を」这类助词单独送去翻译毫无意义 —— 译成「的」再拼回去
    /// 就是「旅的的途中」。而「見つかった」「まだです」这种成规模的假名是实打实的
    /// 日语内容，必须独立成段翻译。
    /// </para>
    /// <para>
    /// 短假名如果两侧都是汉字，把三段合成一段：「旅」+「の」+「途中」→ 「旅の途中」，
    /// 整段含假名因而判为日语，整体翻译。只在一侧有汉字时就并入那一侧。
    /// </para>
    /// </remarks>
    private static List<TextRun> MergeKanaParticles(List<TextRun> runs)
    {
        static bool IsHan(TextRun r) => r.Script == TextScript.Cjk;
        static bool IsShortParticle(TextRun r) =>
            r.Script == TextScript.Kana && r.Trimmed.TrimEnd('?', '!').Length <= 2;

        var result = new List<TextRun>();
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];

            if (IsShortParticle(r))
            {
                var prevIsHan = result.Count > 0 && IsHan(result[^1]);
                var nextIsHan = i + 1 < runs.Count && IsHan(runs[i + 1]);

                if (prevIsHan && nextIsHan)
                {
                    // 两侧都是汉字：合成一段完整的日语短语
                    var next = runs[i + 1];
                    result[^1] = new TextRun(result[^1].Text + r.Text + next.Text, TextScript.Cjk);
                    i++;   // 跳过已并入的下一段
                    continue;
                }
                if (prevIsHan)
                {
                    result[^1] = new TextRun(result[^1].Text + r.Text, TextScript.Cjk);
                    continue;
                }
                if (nextIsHan)
                {
                    var next = runs[i + 1];
                    result.Add(new TextRun(r.Text + next.Text, TextScript.Cjk));
                    i++;
                    continue;
                }
            }

            result.Add(r);
        }
        return result;
    }

    /// <summary>目标语言属于哪种文字。判断「已达标」用。</summary>
    public static TextScript ScriptOfTarget(string targetCode) => targetCode switch
    {
        "ja" => TextScript.Kana,           // 日文：汉字假名都算，靠 ShouldTranslate 特判
        "zh" or "zh-Hant" or "ko" => TextScript.Cjk,
        "ru" or "uk" => TextScript.Cyrillic,
        _ => TextScript.Latin,
    };

    /// <summary>这个片段要不要翻。</summary>
    public static bool ShouldTranslate(TextRun run, string targetCode)
    {
        if (run.Script == TextScript.Neutral) return false;

        var trimmed = run.Trimmed;
        if (trimmed.Length == 0) return false;

        // 汉字里混着假名 = 日文，不是中文。
        // 这一条必须排在「已是目标语言」之前 —— 两者的 Script 都是 Cjk，
        // 先判 Script 会把日文直接判成「已经是中文」放过去。
        if (run.Script == TextScript.Cjk && run.ContainsKana && targetCode is not ("ja" or "auto" or ""))
            return true;

        // 已经是目标语言，原样保留
        if (run.Script == ScriptOfTarget(targetCode)) return false;

        return run.Script switch
        {
            // 假名只有日文用，目标不是日文就一定要翻
            TextScript.Kana => targetCode is not ("ja" or "auto" or ""),

            TextScript.Latin => !LooksLikeProperNoun(trimmed),

            // 谚文/西里尔等：按字符数，短片段大概率是名字
            _ => trimmed.Length >= MinOtherChars,
        };
    }

    /// <summary>
    /// 拉丁片段看起来像不像专有名词（品牌、产品、人名）—— 像就保留不翻。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据从「长度」换成了「形态」，因为长度这个信号并不灵：
    /// <c>el mar</c>（大海）、<c>¡Qué bueno!</c>（太好了）、<c>idée</c>（想法）
    /// 都很短，但都是该翻的；而 <c>Windows</c>、<c>arXiv</c>、<c>Google Scholar</c>
    /// 并不比它们长，却是绝对不能翻的。
    /// </para>
    /// <para>
    /// 保留的三类：全大写缩写（OCR / PDF / DOAJ）、所有词都首字母大写的专名
    /// （Google Scholar）、以及带内嵌大写的驼峰写法（arXiv / PowerShell）。
    /// 其余一律翻 —— 宁可多翻一次，也不要漏掉本该翻译的内容。
    /// </para>
    /// </remarks>
    internal static bool LooksLikeProperNoun(string text)
    {
        // 只看字母，标点不该影响判断：
        // "PDF，" 的全大写判定要成立，否则 PDF 会被送去翻译变成「便携式文档格式」。
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetter).ToArray()))
            .Where(w => w.Length > 0)
            .ToArray();
        if (words.Length == 0) return true;

        // 全大写缩写：OCR、PDF、DOAJ、API
        if (words.Length == 1 && words[0].Length >= 2 && words[0].All(char.IsUpper)) return true;

        // 常见功能词：单独出现时翻译只会得到乱码（el→该、of→的）
        if (words.Length == 1 && FunctionWords.Contains(words[0].ToLowerInvariant())) return true;

        // 单个首字母大写的词：多半是品牌或人名（Windows、Lumo、GitHub）。
        // 不加这条的话 Windows 会被译成「窗户」，那是彻底的错译。
        if (words.Length == 1 && words[0].Length >= 2 &&
            char.IsUpper(words[0][0]) && words[0][1..].All(char.IsLower))
            return true;

        // 驼峰：arXiv、PowerShell、GitHub
        // 判据是「非首位的大写字母后面跟小写」，而不是「首字母大写」——
        // 后者会匹配每一个普通词（Qué / C'est / Windows），把该翻的全判成专名。
        if (words.Any(IsCamelCase)) return true;

        // 每个词都首字母大写：Google Scholar
        if (words.Length >= 2 && words.All(w => char.IsUpper(w[0]))) return true;

        return false;
    }

    /// <summary>各语言的高频功能词。单独出现时翻译没有意义，保留原文。</summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 英语
        "a","an","the","of","to","in","on","at","for","and","or","but","is","are","was",
        "were","be","been","it","its","this","that","these","those","as","by","from",
        // 西语 / 葡语
        "el","la","los","las","un","una","unos","unas","y","o","u","de","del","al","en",
        "con","por","para","que","se","su","sus","es","son","como","más","pero",
        // 法语
        "le","les","des","une","et","ou","ne","je","tu","il","elle","nous","vous",
        "ils","elles","du","au","aux","en","ce","cette","est","sont",
        // 德语
        "der","die","das","ein","eine","und","oder","ist","sind","nicht","mit","von","zu",
    };

    /// <summary>是否存在「非首位的大写 + 后面接小写」的驼峰结构。</summary>
    internal static bool IsCamelCase(string word)
    {
        for (var i = 1; i < word.Length - 1; i++)
            if (char.IsUpper(word[i]) && char.IsLower(word[i + 1])) return true;
        return false;
    }

    /// <summary>
    /// 分段翻译并拼回。
    /// </summary>
    /// <param name="text">原文</param>
    /// <param name="target">目标语言</param>
    /// <param name="translateOne">
    /// 翻译单个片段的委托。返回 null 表示这个片段翻不了，保留原文。
    /// </param>
    /// <returns>拼好的译文；只要有一个片段失败就整体抛异常，避免产出半成品。</returns>
    public static async Task<MixedResult> TranslateAsync(
        string text,
        string target,
        Func<string, string, CancellationToken, Task<string?>> translateOne,
        CancellationToken ct = default)
    {
        var runs = Split(text);
        var sb = new StringBuilder(text.Length + 32);
        var translatedCount = 0;
        var keptCount = 0;

        foreach (var run in runs)
        {
            if (!ShouldTranslate(run, target))
            {
                sb.Append(run.Text);
                if (run.Script != TextScript.Neutral) keptCount++;
                continue;
            }

            var segment = run.Trimmed;
            var translated = await translateOne(segment, target, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translated))
            {
                // 这一段翻不了就用原文兜底，不让整篇失败
                sb.Append(run.Text);
                keptCount++;
                continue;
            }

            // 首尾空白必须原样还原，否则缩进/换行会丢
            sb.Append(run.Leading).Append(translated).Append(run.Trailing);
            translatedCount++;
        }

        return new MixedResult(sb.ToString(), translatedCount, keptCount);
    }
}

/// <summary>分段翻译的结果。</summary>
/// <param name="Text">拼好的完整译文。</param>
/// <param name="TranslatedRuns">实际送去翻译的片段数。</param>
/// <param name="KeptRuns">原样保留的片段数（已是目标语言的、术语、品牌名等）。</param>
public readonly record struct MixedResult(string Text, int TranslatedRuns, int KeptRuns)
{
    public bool DidAnything => TranslatedRuns > 0;
}
