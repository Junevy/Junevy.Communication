# 代码度量脚本（P2.5）

- `dupscan.py`：重复块扫描。`python build/metrics/dupscan.py 6 <文件>...` 列出在两处以上出现的规范化 6 行窗口（忽略注释、using 与纯括号行）；各行窗口数之和即"重复窗口"指标。
- `methodlen.py`：超长方法扫描。`python build/metrics/methodlen.py 61 <文件>...` 按括号匹配（近似）列出行数不少于 61 的方法。
- `effective_lines.py`：有效行统计。`python build/metrics/effective_lines.py <目录>...` 逐目录打印 `*.cs` 去掉空行与 `//` 注释行后的行数，跳过 `obj`、`bin`。

生产代码范围（计划第 3 节）：Core、Channels、Tcp、Udp、Serial、Plc、Melsec 的 `*.cs`，排除 `obj/`、`bin/`。
