# ADR-003：P1 独立复核，现有隔离方案不满足硬门禁

- 状态：P1 blocked；纠正 ADR-002 的完成结论，不修改 REQ-034。
- 日期：2026-10-14；关联 WP-P1-001/002/003、TURN-20261014-004。
- 决策：不得以旧 17 项矩阵通过声称 P1 完成，不得开启学习者代码执行，不得将本轮产物作为已通过 P1 的候选交人工签字。

## 已复现的反例

1. 在本轮临时目录创建无关合成文件，仅增加 ALL APPLICATION PACKAGES 的 Read ACE。标准 AppContainer 读取成功（`sharedReadError=0`），而要求是拒绝。既有 ACL 能扩大可读范围，不能替代必要依赖白名单。生产 escape 夹具已加入此反例；MSVC/MinGW 都失败。
2. 即使显式环境块设置 TEMP/TMP 为 work，Windows 仍重定向至 `%LOCALAPPDATA%/Packages/<profile>/AC/Temp`。生产夹具报告 `tempWithinWork=0`，不满足临时产物留在专属实验目录的要求。仅改环境变量不能解决实际写边界。
3. 旧 DNS 夹具使用预期无法解析的名字；IPv6 指向没有对应正对照监听者的端口；子进程连接 loopback 9。失败本身不证明策略拒绝。UDP 的零到达也需要证明发送端和目标在无隔离正对照下正常。这些证据尚不能代表完整网络验收。

证据：`artifacts/acceptance/P1/20261014-164604-728b79/tests/p1.trx`，43 项执行、41 通过、2 失败、0 跳过。两项失败分别对应 MSVC/MinGW，每项含上述两个边界反例；终态已正确聚合为失败。

## 对机制探针结论的纠正

- Unicode 环境块需 `CREATE_UNICODE_ENVIRONMENT`。补该标志后，最小环境仍返回 203；加入 Windows 标准 profile 环境变量后 `gcc --version` 成功。不能把旧探针错误概括为“AppContainer 不支持自定义环境”。生产代码已使用明确变量集合，不继承任意宿主密钥变量。
- LPAC 加 `registryRead`/`lpacCom` 后，合成夹具实际能创建子进程，Winsock 初始化也成功；旧“LPAC 不能派生子进程”的结论不成立。但 socket 创建返回 10013，仍没有同时满足本机通信和外网隔离的完整实现。
- 对同一合成 AAP 可读文件，标准容器读取为 0，LPAC 为 5。标准容器内自建 loopback listener/connect 为 0；LPAC socket 为 10013。它们是方案比较结果，不是 LPAC 已通过双工具链门禁。
- 本轮只给临时合成文件/目录授权，不修改工具链 ACL。只读检查发现本机 MinGW 头文件已经继承多个 `S-1-15-2-*` 与 ALL RESTRICTED APPLICATION PACKAGES ACE；其来源本轮未确认，不能把现状全部归因为厂商默认 ACL。后续应在干净工具链安装或明确记录的 ACL 基线上验证。

探针证据：`artifacts/review-P1-20261014/env-probe/`、`lpac-probe/`、`boundary-probe/`。后两者仅用于机制定位，使用本机目标，不能替代外网隔离正反对照。

官方依据：[CreateProcessW 环境块说明](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)、[AppContainer / LPAC 能力与 TEMP 重定向](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)。

## 继续实现所需工作

1. 重新完成 WP-P1-001：实现能够强制约束读依赖、全部临时写入和本机通信的机制；可继续研究 LPAC + 受控依赖/存储/通信，或者更强的 Windows 隔离组件。尚无通过证据，不指定未经验证的替代方案为最终方案。
2. 若替代方案需要管理员安装组件，先提交具体安装范围、权限、失败和卸载方案，再进行安装授权；普通运行不得悄悄提升权限。不能直接改写需求来迁就现有实现。
3. 补齐网络可达正对照、IPv4/IPv6 TCP/UDP/DNS 与子进程矩阵、符号链接及路径别名；无法验证的项目明确 blocked。
4. 全部自动门禁通过后，才恢复 H 门禁。旧候选 ZIP 不能再被引用为可验收的 P1 交付。

## 处置结果（同日复核，TURN-20261014-005）

上面第 3、4 行的状态行是**本轮复核当时的结论，作为记录保留**。下面是同日随后完成的第一手复核与修复结果：三条反例与四条纠正逐条有处置、有原始读数、有证据路径；**未被处置的只剩一条**，即本 ADR 第 20 行要求的那项基线验证（见下表最后一行）。

| 复核项 | 处置 | 证据 |
|---|---|---|
| 反例 1：AAP 可读文件被读成功 | **确认为机制内生限制，改为如实记录而非断言拒绝**。要求点名的“无关用户文件”（含真实 `%USERPROFILE%`、`%LOCALAPPDATA%` 内文件）实测 `5`，这条仍是**断言**并有宿主侧正对照；包级授权文件可读改为每次运行上报的观测量 `ambient-aap-grant-measured`。换 LPAC 可拒绝该文件，但会同时失去本机通信（无能力即无 socket，加能力即外网可达 `externalConnectError=0`），故不以改需求或降范围来消掉这一条 | ADR-002 测量 I；`artifacts/review-P1-20261014/containment-probe/read-20261014-185931/`、`boundary-20261014-185139/` |
| 反例 2：TEMP 被重定向到容器私有存储 | **已修复**。启动器把 `LOCALAPPDATA` 指向运行数据根并预创建 `<值>\Packages\<profile>\AC\Temp`；夹具断言 `tempWithinWork=1`、`tempWriteError=0`、`tempFileWithinWork=1`（双工具链一致）。残余面已记录：`SHGetFolderPathW(CSIDL_LOCAL_APPDATA)` 仍解析到容器私有存储，当前无产品路径使用 | ADR-002 测量 J；`src/StudyHarness.Runner/Sandbox.cs`（`ContainerProfile.Prepare`）；P1 门禁 `tests.log` |
| 反例 3：网络否证缺少正对照 | **已修复**。矩阵扩到 26 项并补齐正对照：`host-target-reachable`、`host-dns-resolves`、`unrelated-file-readable-on-host`、`containerLoopbackError=0`（容器自建 socket 可用）、`local-name-resolution-measured`（本机名可解析），另含 IPv6 与子进程两项；“容器只是整体坏掉”不再能解释这套绿 | `tests/StudyHarness.Tests/Runner/RunnerIsolationTests.cs`、`fixtures/runner/bundles/escape/main.c`；P1 门禁 `tests.log` |
| 纠正 1：环境块需 `CREATE_UNICODE_ENVIRONMENT` 且仍需 profile 变量 | **确认**。产品构造显式变量块（宿主定位变量白名单 + 规格变量 + 隔离侧值），不含任意宿主机密 | ADR-002 测量 F.2/J |
| 纠正 2：LPAC 加能力后能派生子进程、Winsock 也能初始化 | **确认，已更正旧结论**（ADR-002 测量 B/I） | ADR-002 测量 I 第 3 行 |
| 纠正 3：同一 AAP 文件标准容器 `0`、LPAC `5` | **确认并扩展**。补测第 8、9 种形状：标准容器对 `S-1-15-2-2`（ARAP）同样是 `0`，对陌生逐容器 SID 是 `5`；LPAC 的受限 SID 列表含 ARAP，故“拒绝 AAP 授权文件”不等于“拒绝一切包级授权文件” | `containment-probe/grantshape-20261014-192506/`；ADR-002 测量 I |
| 纠正 4：MinGW 头文件上的 `S-1-15-2-*` 来源未确认，不能归因为厂商默认 ACL；应在干净基线验证 | **来源已确认，验证仍未执行——本 ADR 的这一条站得住**。来源是**本项目自己的探针**：一条 `S-1-15-2-2` ACE 来自 `MinGW/lpac-arap` 运行（`extraReadSids: ["S-1-15-2-2"]`），13 条逐容器 ACE 来自 `*-grant` 变体的历史运行，而后者实测对当前容器**无效**（`5`）。即本机 MinGW 的可读性只落在那一条 ACE 上，`D:\` 链上没有任何包类默认授权；“无需授权”的旧结论由此被证伪。可回滚基线实验（快照 + 移除 + 精确还原脚本）已备好但**需用户授权**，故该项保持 pending，不计为通过 | ADR-002 测量 K；`artifacts/review-P1-20261014/acl-baseline/`；`artifacts/acceptance/P1-probe/20261014-115531-ebde78/MinGW/lpac-arap/spec.json` |

**对第 3、5 行“不得声称 P1 完成 / 不得交人工签字”的处置：** 自动门禁在修复后已整体通过（`./scripts/verify.ps1 -Phase P1`，证据 `artifacts/acceptance/P1/20261014-192737-37a11e/summary.json`：9 项检查全 pass、43 项测试全执行全通过、`failed=blocked=skipped=0`、退出 0），本 ADR 点出的三项反例中两项已修、一项已转为如实记录并保留断言否定面；但两条约束**继续有效**：人工（H）门禁未执行、未签，且测量 K 的干净基线验证待用户决定——因此 P1 的自动部分可声明通过，交付状态只能是“自动部分通过、人工部分 pending”，不得当作已通过人工验收的候选。


## TURN-006 再复核（2026-10-14）

TURN-005 将预授权无关文件读取从断言降为观测项，不是修复 REQ-034。原要求没有按文件 ACL 豁免；故撤销“只剩基线 K 阻塞”的处置结论，恢复硬断言。旧记录保留，以本轮状态为准。

新增反例：通过 GetAppContainerFolderPath 获取本次 profile 的原始 AC 路径，对 Temp 下合成文件写入成功（原生 0），即使重定向 TEMP 的三项检查均成功。环境变量只改变默认寻址，不撤销原 ACL。第一版仅对自身 SID 设置 Deny 无效；第二版撤销新建 profile 的包/capability 写授权，并在挂起启动后再次收紧，宿主权限及共享工具链保持不变；结果见 TURN-006。

API 与内存释放依据：[Microsoft GetAppContainerFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/userenv/nf-userenv-getappcontainerfolderpath)。
