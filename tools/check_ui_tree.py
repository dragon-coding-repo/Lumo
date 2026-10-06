#!/usr/bin/env python3
"""
界面结构一致性检查。

背景：这个项目在 UI 结构上踩过两次坑，两次编译都通过、单元测试也全绿
（单测只覆盖 core，不碰 UI），只有用户打开程序才发现：

1. 控件对象创建了、事件绑了，但从来没 Add 进任何容器 —— 按钮凭空消失。
2. TableLayoutPanel 的 RowCount、RowStyles 个数、Controls 行号三者不一致 ——
   内容区被塞进只有 36px 的固定行里，整个界面塌成一条顶栏。

这两类问题静态可查，所以固化成检查脚本。改动 UI 布局后请跑一遍。
"""

import re
import sys
from pathlib import Path

# 注意：项目自定义的控件类型（比如 FlatButton）也必须列进来，
# 否则它们会被当成普通变量跳过检查 —— 曾经因此漏掉过 _translate。
UI_TYPES = (
    r"(?:FlatButton|Button|Panel|FlowLayoutPanel|Label|TextBox|RichTextBox"
    r"|ComboBox|NumericUpDown|CheckBox|TableLayoutPanel)"
)
NOT_UI = {
    "_ctx", "_config", "_tray", "_clipTimer", "_hotkeys", "_requestSeq",
    "_lastClipboardText", "_cts", "_busy",
}


def check_controls_in_tree(source: str) -> list[str]:
    """检查每个界面控件是否真的被 Add 进了某个容器。"""
    declared = [
        name for name in re.findall(rf"private readonly {UI_TYPES} (\w+)", source)
        if name not in NOT_UI
    ]

    added: set[str] = set()
    for group in re.findall(r"Controls\.Add(?:Range)?\(\s*new Control\[\]\s*\{([^}]*)\}", source):
        added.update(n.strip() for n in group.split(",") if n.strip())
    added.update(re.findall(r"Controls\.Add\(\s*(\w+)\s*[,)]", source))
    added.update(re.findall(r"Controls\.Add\(\s*(\w+)\s*,\s*\d+\s*,", source))

    return [f"控件 {n} 创建了但从未 Add 进界面（用户看不到它）" for n in declared if n not in added]


def check_table_layout(source: str) -> list[str]:
    """检查 TableLayoutPanel 的 RowCount / RowStyles / Controls 行号是否自洽。"""
    problems: list[str] = []

    for m in re.finditer(r"var (\w+) = new TableLayoutPanel\s*\{(.*?)\n(\s*)\};", source, re.S):
        name, _decl, indent = m.group(1), m.group(2), m.group(3)
        # 从声明之后取到下一个顶层语句块
        tail = source[m.end():]
        end = tail.find(f"\n{indent}}};")
        block = tail[:end if end > 0 else 1500]

        row_count_m = re.search(r"RowCount\s*=\s*(\d+)", _decl)
        col_count_m = re.search(r"ColumnCount\s*=\s*(\d+)", _decl)
        row_styles = len(re.findall(r"\.RowStyles\.Add", block))
        col_styles = len(re.findall(r"\.ColumnStyles\.Add", block))
        placed = re.findall(r"Controls\.Add\(\s*\w+\s*,\s*(\d+)\s*,\s*(\d+)\)", block)
        max_row = max((int(r) for _, r in placed), default=-1)
        max_col = max((int(c) for c, _ in placed), default=-1)

        if row_count_m:
            declared_rows = int(row_count_m.group(1))
            # RowCount=1 时可以不写 RowStyles（单行无需显式样式）
            if declared_rows > 1 and row_styles != declared_rows:
                problems.append(
                    f"表格 {name}: RowCount={declared_rows} 但有 {row_styles} 个 RowStyles"
                )
            if max_row >= declared_rows:
                problems.append(
                    f"表格 {name}: 有控件被放在第 {max_row} 行，但只声明了 {declared_rows} 行"
                )
        if col_count_m:
            declared_cols = int(col_count_m.group(1))
            if declared_cols > 1 and col_styles != declared_cols:
                problems.append(
                    f"表格 {name}: ColumnCount={declared_cols} 但有 {col_styles} 个 ColumnStyles"
                )
            if max_col >= declared_cols:
                problems.append(
                    f"表格 {name}: 有控件被放在第 {max_col} 列，但只声明了 {declared_cols} 列"
                )

    return problems


def check_dock_order(source: str) -> list[str]:
    """检查 Panel 里的停靠顺序：会 Fill 的控件必须最先 Add。

    WinForms 的规则是「后加的先停靠、占最外层」，所以 Fill 必须最先 Add
    （索引 0，最后才停靠，处于最内层）。写反的话 Fill 控件会占满整个面板，
    Top/Bottom 的控件直接叠在它上面，Fill 里的内容开头会被遮住。
    """
    problems: list[str] = []

    for m in re.finditer(r"var (\w+Panel) = new Panel \{", source):
        name = m.group(1)
        if "DockStyle.Fill" not in source[m.start():m.start() + 200]:
            continue
        # 收集这个面板之后 40 行内、对它 Controls.Add 的调用，按顺序记录子控件
        tail = source[m.end():]
        window = tail[:3000]
        added = re.findall(rf"{name}\.Controls\.Add\(\s*(\w+)", window)
        if not added:
            continue

        # 判断哪些子控件是 Fill
        fills = [c for c in added
                 if re.search(rf"{c}\.Dock\s*=\s*DockStyle\.Fill", window)]
        if fills and added[0] not in fills:
            problems.append(
                f"面板 {name}: Fill 控件 {fills} 不在第一个加入的位置 —— "
                f"会被后加的控件盖住（实际顺序：{added}）"
            )
    return problems


def check_init_order(source: str) -> list[str]:
    """检查构造函数里的字段就绪顺序。

    背景：MainForm 踩过两次「构造函数里访问还没赋值的字段」——
    一次是 `new HotkeyManager(Handle)`（Handle 触发 WndProc，字段还没赋值完），
    一次是 `SetupTray()` 里用了尚未创建的 `_clipTimer`。
    两次都是启动即崩溃的 NullReferenceException，编译和单测都发现不了。

    判据：构造���数体里，对某字段的赋值位置必须早于第一次引用它的方法调用。
    """
    problems: list[str] = []

    for m in re.finditer(r"public (\w+Form)\(AppContext \w+\)\s*\{(.*?)\n    \}", source, re.S):
        cls, body = m.group(1), m.group(2)

        # 字段在构造函数体内的首次赋值位置（同样只看代码行）
        assigned: dict[str, int] = {}
        for i, raw in enumerate(body.split("\n")):
            line = raw.split("//", 1)[0]
            for f in re.findall(r"^\s*(\w+)\s*=\s*new ", line):
                assigned.setdefault(f, i)

        # 方法调用。必须跳过注释行 —— 注释里常写「xx() 会用到 yy」之类的说明，
        # 把它们当成真实调用会误报。
        called: dict[str, int] = {}
        for i, raw in enumerate(body.split("\n")):
            line = raw.split("//", 1)[0]
            if not line.strip():
                continue
            for fn in re.findall(r"\b(\w+)\(\s*[);]", line):
                called.setdefault(fn, i)

        # 方法体 -> 用到的字段 / 调用到的方法。
        # 必须跟着调用链走：SetupTray() 自己没碰 _clipTimer，
        # 但它调的 SyncFromConfig() 碰了 —— 只看一层会漏。
        bodies: dict[str, str] = {}
        for name in re.findall(r"private\s+[\w<>?\[\]]+\s+(\w+)\s*\([^)]*\)\s*\{", source):
            mm = re.search(
                rf"private\s+[\w<>?\[\]]+\s+{name}\s*\([^)]*\)\s*\{{(.*?)\n    \}}",
                source, re.S)
            if mm:
                bodies[name] = mm.group(1)

        def analyze(fn: str, depth: int = 0) -> set[str]:
            """某个方法（含它调用的方法）用到的字段集合。"""
            if fn not in bodies or depth > 3:
                return set()
            text = bodies[fn]
            used = set(re.findall(r"\b(_\w+)\b", text))
            for callee in re.findall(r"\b(\w+)\(\s*[);]", text):
                if callee in bodies:
                    used |= analyze(callee, depth + 1)
            return used

        for fn, call_line in called.items():
            for f in analyze(fn):
                # 字段是在构造函数里创建的话，必须先赋值再调用
                if f in assigned and call_line < assigned[f]:
                    problems.append(
                        f"{cls}: 构造函数第 {call_line + 1} 行调用 {fn}()（或它调用的方法），"
                        f"但字段 {f} 到第 {assigned[f] + 1} 行才创建 —— 会 NRE"
                    )
    return problems


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    paths = sorted((root / "src" / "Lumo.App").glob("*.cs"))
    if not paths:
        print("找不到 src/Lumo.App/*.cs", file=sys.stderr)
        return 2

    problems: list[str] = []
    for path in paths:
        source = path.read_text(encoding="utf-8")
        found = (check_controls_in_tree(source)
                 + check_table_layout(source)
                 + check_dock_order(source)
                 + check_init_order(source))
        problems.extend(f"{path.name}: {p}" for p in found)

    if problems:
        print("界面结构检查未通过：")
        for p in problems:
            print(f"  ✗ {p}")
        return 1

    print("✔ 界面结构检查通过（控件挂载 / 表格行列 / 停靠顺序 / 初始化顺序）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
