# P1 基础验收签收 — 2026-10-16

- 签收人：项目用户（本会话）。
- 用户原文：“验收通过。现在给P1收尾”。
- 结论：P1固定合成夹具基础 accepted；WP-P1-001/002/003在ADR-006范围内关闭。
- 人工证据：用户整体签收，未附加逐项操作日志；不虚构H1–H8的独立观察或执行时间。
- 自动证据：artifacts/acceptance/P1/20261016-170908-8c5e53/summary.json，78/78测试、9/9步骤、UI5/5、0失败/跳过。
- 源码/REQ/Plan SHA256：70895B9FB8261ECAC9C2676F72C33DB51030384D983853EC9FE2F22A3AFA311E。
- 候选包：artifacts/acceptance/P1/20261016-170908-8c5e53/StudyHarness-P1-win-x64.zip。
- 包SHA256：ED0611C66C4E48DE212B4D864587CE6BD396F44565EEA898B9A679C4C7F5B73D。
- 环境：当前Windows Home机器，Windows PowerShell 5.1，固定SDK 10.0.401；不是跨机器兼容性签收。

收尾时按验收脚本相同Windows PowerShell 5.1环境重算源码摘要及包摘要，均匹配。当前只补叙述性记录，不修改已验收实现、REQ或Plan，不重复执行完整运行门禁。自动summary的manualPending/desktopVisualPending保持当时原值，本记录追加用户签收，不修改历史证据。候选包和原始日志留在本地artifacts，不进入Git或公开发布。

## 保留边界与后续入口

PSEC仍blocked：最近完整运行20261016-155401-abaea8为80执行、78通过、2个定容盘测试失败；磁盘硬限制及完整攻击、宿主文件生命周期、真实模型外发集成和独立策略验证须继续按[PSEC清单](PSEC.md)完成，之后才能开放真实执行。P1签收不代表保证任意代码安全。

内存条款R-MEM-01继续成品后待决，现有保护保留。IPv6按用户决定不探索、不记通过，网络策略仍默认拒绝。P2–P7尚未实现，可按Plan另行启动后续工作；不得借P1收尾开放真实学习者代码或模型参考解。版本仍为0.1.0-alpha.1，未发布。