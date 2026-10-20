# P1 人工验收标准

当前结论：**blocked，暂不具备最终人工签收条件**。TURN-006 复核确认旧版 43/43 测试不能证明 REQ-034：`ambientAapReadError=0` 曾被记录为始终成功的观测项。本轮恢复硬断言。人工验收不豁免已知缺陷，也不替代自动隔离验证。

最新TURN-20261016-004：完整门禁`20261016-153348-80e971`执行80项，78通过、2项VHD失败、0测试跳过，退出1。宿主文件16项、模型外发10项全部通过；同源码P0门禁`20261016-153640-59d0c6`的8/8步骤、UI5/5通过。宿主回写/清理业务入口和真实模型导入/授权UI/HTTP连接尚未实现，不能视为端到端通过。范围见[宿主与外发矩阵](../assessment/P1-host-egress-gates.md)，磁盘选择见[候选复核](../assessment/P1-disk-alternatives.md)。以下58项等为历史结果。

2026-10-16 更新：当前决定见ADR-005：严格读取、首期禁网，本机IPv6测试明确排除且未验证。最新完整门禁`20261016-151304-1a0a06`退出1：58测试、56通过、2个专用卷测试失败、0测试跳过，后续发布/UI/打包未运行。历史52/52及UI5/5（`20261016-104709-753e61`）不代表当前通过。标准输出/错误输出合计64KiB硬上限、单进程/作业总内存、进程数、CPU实际负载和本轮宿主硬链接保护已有实测；PSEC在已挂载固定VHD内读取授权源码仍失败，容量填盘尚未执行，完整宿主文件竞争及模型外发负例也未完成。真实代码执行仍关闭，不交最终签收。接手路线见 [TURN-20261016-003](../changes/2026-10/TURN-20261016-003.md)。

关联：WP-P1-001/002/003、REQ-022/023/032/034/037、Plan.md §6.4/§8。最新证据与接手路线见 [TURN-006](../changes/2026-10/TURN-20261014-006.md)。范围仅固定合成夹具，无真实模型与学习者代码执行。

## 自动交接前提

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -Phase P1
```

必须退出 0，summary 的全部检查通过，失败/跳过/未执行为 0；TRX 中全部测试实际执行。必须使用修改完成后的同一源码与构建产物，保存源码摘要、summary、TRX、tests.log。不能通过删除用例、改成观测项或跳过测试取得绿色。

逐条查看两套 escape 原始报告：

- 编译、必要依赖读取、工作目录写入与子进程创建成功；TEMP/TMP 及实际临时文件留在 work。首期自身本机通信也须被禁止，输入输出仅经宿主授予的句柄。
- 普通无关文件与预先带 AAP Read 的无关合成文件都必须拒绝读取（原生错误 5）；后者仍是无关文件，ACL 授权不是需求豁免。
- 原始 OS 容器目录的绝对路径写入、越界写、穿越、junction、控制通道访问必须被拒绝；路径不存在不能冒充访问拒绝。
- IPv4 网络控制端在宿主可达；容器对 TCP、UDP、DNS、本机服务及子进程外发被阻止，接收端没有攻击数据。按用户2026-10-15决定，本机不探索/测试 IPv6，报告须注明明确排除且未验证，不能记为通过。禁网策略不得主动放行 IPv6；不要把超时单独当作全面隔离证明。
- 开放真实代码前必须验收资源强制限制、宿主产物处理防越界与并发替换、模型请求不透传未授权运行输出/文件；未实现项保留 blocked，不因调整需求自动通过。详见 REQ-034 与 ADR-005。
- 停止、断连与宿主异常退出回收整树，失败 CaseResult 必须导致失败 RunFinished。
- 干净工具链基线已验证。当前 MinGW 有早期探针遗留的包级 ACL，不能当作默认安装环境。优先使用隔离的工具链副本/干净机器验证；不要求用户以接受广泛权限替代验收。
- Plan §6.4 要求但尚未完成的路径别名、符号链接等覆盖必须明确补齐或登记阻塞，不能声明已全部验证。

## 人工操作与通过标准

自动前提全部满足之后，普通权限 PowerShell 执行以下步骤。现在可以预演操作，但结果不构成 P1 签收。

```powershell
./scripts/manual-p1.ps1 -Toolchain both -Bundle hello
./scripts/manual-p1.ps1 -Toolchain msvc -Bundle escape
./scripts/manual-p1.ps1 -Toolchain mingw -Bundle escape
./scripts/manual-p1.ps1 -Toolchain msvc -Bundle loop
./scripts/manual-p1.ps1 -Toolchain mingw -Bundle loop
```

| 编号 | 操作及通过标准 | 当前结果 |
|---|---|---|
| H1 工具链 | 显示真实 MSVC、Windows SDK、MinGW 路径与版本，两套均可用；缺失必须明确报错 | pending |
| H2 MSVC | hello 的 CompileResult、program-output、RunFinished 全部成功 | pending |
| H3 MinGW | hello 同样成功，输出与预期一致 | pending |
| H4 隔离 | 两套 escape 每项硬断言及终态成功；核对原始错误号，预授权无关文件不能可读；存在任一失败即不通过 | blocked |
| H5 停止 | 两套 loop 超过 20 秒观察窗口仍运行；按 S 得到 RUN_CANCELLED；父子进程回到启动前基线；再启动正常 | pending |
| H6 关闭与崩溃 | loop 运行时关验证窗口，无该次 Runner/编译器/program 子树残留；自动异常退出测试也通过 | pending |
| H7 权限与数据 | 普通运行无 UAC，无工具链/资料 ACL 修改；工作输出与临时文件在指定运行目录，原始 OS profile 不可作为越界写入口 | pending |
| H8 拒绝降级 | 不存在的 Runner 返回 ISOLATION_UNAVAILABLE、退出 65，不启动普通进程替代 | pending |
| H9 干净环境 | 未受探针 ACL 污染的环境/副本上完成双工具链验证，记录授予的最小依赖权限及清理结果 | blocked |

H8（先构建人工入口）：

```powershell
$repo = (Get-Location).Path
& "$repo/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows/StudyHarness.HostProbe.exe" `
    --interactive --runner "$repo/does-not-exist/StudyHarness.Runner.exe" `
    --data-root "$repo/artifacts/manual-P1" --toolchain msvc --bundle hello
```

## 留证与签收

逐项记录：日期、执行人、源码摘要/提交、机器与工具链版本、命令、预期、实际结果、日志路径、相关截图及启动前后 PID。数据在 `artifacts/manual-P1/`；该目录不入 Git，长期交接需单独归档。

所有自动前提和 H1–H9 都通过，才可由用户明确签收；任一失败记 blocked，未执行记 pending。Agent 不代签。Windows Kits 是 SDK 路径，不等于 MSVC 安装路径；以工具链发现输出为准。
