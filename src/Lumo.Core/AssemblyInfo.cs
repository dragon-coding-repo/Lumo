// 测试要覆盖 internal 的纯函数（语言对照表、DeepL 语言码、请求头名校验）。
// 这些函数刻意不暴露给外部，但值得被测试钉住。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumo.Core.Tests")]
