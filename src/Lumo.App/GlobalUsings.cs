// WinForms 项目的 ImplicitUsings 不含 System.IO，而 App 里到处用 Path/Directory/File。
global using System.IO;
// 显式列出这几个，避免依赖 SDK 的隐式 using 行为在不同 SDK 版本间有差异。
global using System.Windows.Forms;
global using System.Drawing;
