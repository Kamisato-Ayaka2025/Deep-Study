# ADR-002: Windows 隔离机制（标准 AppContainer + Job Object）

> **TURN-006 最新更正：P1 blocked。** 包级授权无关文件可读仍违反 REQ-034；TEMP 重定向也未撤销原始 profile 写权限，本轮已添加修复与回归。详见 [TURN-006](../changes/2026-10/TURN-20261014-006.md)。下文旧处置结论被此更正覆盖。

> 2026-10-14 历史提示：[ADR-003](ADR-003-p1-independent-review.md) 曾据两条反例判定 P1 blocked 并否定本 ADR 的完成结论。同日已用受控实验逐条复测：读边界反例成立但属机制的**已记录限制**（测量 I），临时目录反例成立且已由产品修复（测量 J），LPAC 与自定义环境的旧结论按测量 B/F.2 的更正版重写。本 ADR 的决策（标准 AppContainer + Job Object）予以维持。
>
> 未执行项不记为通过：A–J 为机制选择期与更正期的探针实测；人工步骤（H 门禁）仍未签。

- Status: blocked / prototype；TURN-006 恢复预授权无关文件的拒绝硬断言，当前机制未满足 REQ-034。以下旧决策与测量保留作为历史，不构成验收通过。
- Scope: WP-P1-001（机制选定）→ WP-P1-002/003（实现与复测）；REQ-022/023/032/034/037；Plan.md §6.4、§8 P1。
- Environment: Windows 11 build 26200，x64；.NET 10。MSVC cl.exe 14.44.35207（`D:\Visual Studio`）；MinGW gcc 13.1.0（`D:\Qt\Tools\mingw1310_64`）。
- Decision: 使用**标准 AppContainer**（`CreateAppContainerProfile` + 带 ALL APPLICATION PACKAGES SID 的 `SECURITY_CAPABILITIES`），**不降级为 LPAC**；**不添加任何 ACL 授权**（`readRoots` 为空）、**不提升权限**；进程树挂 **Job Object** 并设置 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 负责整树回收；子进程**环境由宿主继承，不注入**。
- Evidence: `scripts/probe-p1.ps1`（开发期探针）、`fixtures/runner/capability-probe.c`（固定合成夹具）、`probes/StudyHarness.IsolationProbe/`（启动器）。参考证据集 run id `20261014-121056-53e78a`，位于 `artifacts/acceptance/P1-probe/20261014-121056-53e78a/`。
- 生产复测证据: `./scripts/verify.ps1 -Phase P1`；证据目录 `artifacts/acceptance/P1/<run-id>/`（`tests.log` 含逐用例原始错误号、`toolchains.log`、`summary.json`）。探针保留为 A–G 的可复现路径，其中 LPAC（测量 B）与启动器缺陷（测量 F）无法由生产 Runner 复现。
- Raw error numbers: 5 = ERROR_ACCESS_DENIED，3 = ERROR_PATH_NOT_FOUND，267 = ERROR_DIRECTORY，10107 = WSAEPROVIDERFAILEDINIT，10013 = WSAEACCES，10060 = WSAETIMEDOUT，10061 = WSAECONNREFUSED，11001 = WSAHOST_NOT_FOUND，11003 = WSANO_RECOVERY，203 = ERROR_ENVVAR_NOT_FOUND。
- 未执行项不记为通过：A–J 为机制选择期与更正期的探针实测；H 为生产 Runner 的复测，两者都只记录已实测内容，人工步骤（H 门禁）仍未签。

## 背景与硬门禁

Plan.md §6.4 把文件隔离定为提前验证的硬门禁：REQ-034 要求系统**强制**阻止越界，不可仅做工作目录设置、模型提示、路径检查或进程树终止；机制必须同时支持 MSVC、MinGW、目录外必要只读依赖、本机通信、阻止外网、限制无关文件访问，且具体机制必须由实验选定。该节同时明确：不给整个用户目录、整块磁盘或整个依赖目录泛化读权限；进程树管理与权限隔离是两个问题，**不把 Job Object 本身当成完整安全沙箱**；若候选不能满足，P1 状态为 blocked 并保留证据，**不得将“普通进程执行”作为可发布兜底**。

因此本 ADR 的任务不是描述理想设计，而是用这台机器上的原始错误号决定：哪种受限身份既能编译 MSVC/MinGW 程序，又能真正阻断外网与越界文件访问。

## 决策

1. **受限身份：标准 AppContainer。** 由 `CreateAppContainerProfile` 建立 profile；`STARTUPINFOEX` 中通过 `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`（`0x00020009`）给出 AppContainer SID，并通过 `PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY`（`0x0002000F`，值 `0`）**显式选择标准 AppContainer 而非 LPAC**；能力集为空，只保留 **ALL APPLICATION PACKAGES SID（S-1-15-2-1）** 这条由 Windows 隐含授予的包身份。该隐含授权的可读范围是机制自身的读边界（测量 I）：普通用户文件（本人、`Users`、`Authenticated Users`、真实 `%USERPROFILE%`/`%LOCALAPPDATA%`）全部不可读，而任何已被显式授予 AppContainer 包类的文件可读——这条限制被记录并被每次运行上报，不用断言掩盖。
2. **对依赖目录无 ACL 授权、全程无提升。** 机制选择期探针的 `readRoots`、`extraReadSids` 均为空且仍完成双工具链编译（测量 C）；生产实现同样不为任何工具链目录新增 ACE，不调用提升 API，目录外的工具链依赖靠 Windows 与厂商既有的继承 ACE 可读（测量 D）。**唯一的写入授权是给容器 SID 的专属运行目录**（`AllowWorkRoot` 追加继承 ACE 并打低完整性标签），这正是 §6.4 要求“专属目录写入成功”的那一条，不构成对其它目录的读权限泛化。
3. **整树回收：Job Object。** 宿主 `CreateJobObject` → `SetInformationJobObject(JobObjectExtendedLimitInformation)` 设 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`（`0x2000`）→ `AssignProcessToJobObject`，句柄关闭即回收编译/运行进程树。该对象只解决生命周期，明确**不作为文件边界**；文件边界全部来自 AppContainer 内核强制。
4. **环境：显式构造，不整体继承。** 给 `CreateProcess` 传自定义 `lpEnvironment`（Unicode 块 + `CREATE_UNICODE_ENVIRONMENT`），内容为“宿主定位变量白名单 + `spec.Environment` + 容器自身变量”，其中 `LOCALAPPDATA`/`TEMP`/`TMP` 一律指向本次运行数据根内的路径（测量 J），并在启动前预创建容器会重写到的 `Packages\<profile>\AC\Temp` 链。工具链自身的 `INCLUDE`/`LIB`/`PATH` 由 `spec.Environment` 携带，因此不依赖继承。
5. **IPC 方向：宿主为管道服务端。** 容器内代码连不上宿主的命名管道（测量 A 的 `runnerPipeConnectError=5`），故按 Plan.md §5.5 用当前会话随机握手秘密 + 管道 ACL 认证；宿主↔容器改用**继承的标准句柄**，不用 loopback TCP。协议只承载宿主登记的资源 ID，不接受命令字符串、任意路径或任意环境变量。
6. **失败即拒绝。** 生产 Runner 只要无法建立容器，就返回 `ISOLATION_UNAVAILABLE` 并拒绝执行，绝不回退为普通进程运行。

## 测量结果（run 20261014-121056-53e78a）

以下为 `scripts/probe-p1.ps1` 在真实机器上的原始观测，夹具为 `fixtures/runner/capability-probe.c`。受控监听端点位于本次证据目录内：LAN 地址 `192.168.31.1`、可达端口 6415、UDP 端口 63432、宿主命名管道；除它们以外未联系任何外部目标。

### A. 标准 AppContainer，无授权、无提升（`probe/MSVC/appcontainer-nogrant`、`probe/MinGW/appcontainer-nogrant`）

两种工具链结果一致：

- `isAppContainer=1`；`insideWriteError=0`（写入专属运行目录成功）。
- `headerReadError=0`（读取真实工具链头文件这类“目录外必要依赖”成功）。
- `outsideReadError=5`；`outsideWriteError=5`。
- `junctionWriteError=5`：宿主在运行目录内创建的 NTFS junction 指向外部，容器内写入仍被拒绝，说明边界按最终解析路径生效，不走重解析点逃逸。
- `runnerPipeConnectError=5`：容器内代码无法连接宿主命名管道。
- `tcpV4Error=10060`，同时受控 LAN 监听端报告 **tcpPending=false**（零个待处理连接）；监听端确实处于可连接状态，故 TCP 外发是被真正阻断，而非“连接被拒”。
- `tcpV6Error=10061`。
- `udpError=0`，但受控 UDP socket 收到 **udpPending=0**：数据报在本地被接受、从未送达，UDP 外发同样被阻断。
- `dnsError=11001`。
- `childSpawnError=0`，`childIsAppContainer=1`，`childConnectError=10061`：由容器内代码再派生的子进程**仍留在容器内**且同样断网。

### B. LPAC（去掉 ALL APPLICATION PACKAGES SID）——当时的拒绝依据（已按测量 I 更正）

`probe/MSVC/lpac-nogrant`、`probe/MinGW/lpac-nogrant`：文件隔离表现与 A 相同，但

- `winsockStartupError=10107`（Winsock 连初始化都失败，即完全没有 socket）；
- `childSpawnError=5`、`childSpawnStyleB=5`、`childSpawnStyleC=5`——**三种子进程派发方式全部失败**（子进程未产生，故 `childIsAppContainer`/`childConnectError` 为未观测哨兵 `-5`）。

编译驱动必须是子进程模型：cl.exe 需要 c1xx/link.exe，gcc 需要 cc1/as/ld。LPAC 连子进程都起不来，因此**无法满足“分别 MSVC、MinGW 编译正常 C 程序”这一同一条硬门禁**。此外 LPAC 的“断网”是注册表/ACL 无法加载 Winsock provider 的**附带后果**，不是被设计的网络策略，作为安全保证过于脆弱。二者共同构成对 LPAC 的决定性拒绝。

**更正（见测量 I 与 ADR-003）：** 上述“连子进程都起不来”只在**不加任何能力**的 LPAC 配置下成立。加上 `registryRead`/`lpacCom` 后子进程与 socket 都能工作（ADR-003 的独立探针与测量 I 一致），因此当时的拒绝理由不准确；LPAC 仍然被拒绝，但依据换成测量 I 的定量对照：能拒绝包类授权文件的配置都不同时具备本机通信能力，而恢复通信的能力同时恢复外网可达。

### C. 同一标准 AppContainer 真的能编译并运行（`isolated-compile/MSVC`、`isolated-compile/MinGW`，均 pass）

`readRoots` 为**空**、无任何 ACL 授权、无提升：MSVC `cl.exe` 14.44.35207 产出 hello.exe；MinGW `gcc` 13.1.0 产出 hello.exe；随后在容器内执行该 hello.exe，宿主捕获到其 stdout 为 `study-harness-isolated-hello`。这证明 A 的拒绝不是“权限不足导致的假失败”，而是策略边界。

**更正（见测量 K）：** “无任何 ACL 授权”只在“本次运行没有新增授权”的意义上成立。当时 `D:\Qt\Tools\mingw1310_64` 的 5 个目录**已经**带着先前 `*-grant` 探针运行留下的显式包类 ACE，所以这次的“无授权也成功”不能证明授权不必要；MSVC 一侧不依赖任何历史授权，结论仍然成立。

### D. 为什么 MSVC 不需要授权（标题为更正后表述；MinGW 的情况见测量 K）

`C:\Program Files`、`C:\Program Files (x86)`、`C:\Program Files (x86)\Windows Kits\10` 已带有包类 ReadAndExecute ACE。测量 K 查明其来源是**操作系统的默认 ACL**（`Program Files` 两级目录上是显式且可继承的），因此装在系统盘 `Program Files` 下的目录外依赖**按构造即可读**，无需改动任何厂商 ACL——这一条对 MSVC 成立且稳健。

早期的“显式授权”路线已尝试：向 Windows Kits 目录**新增**显式 ACE 失败，报 “attempted to perform an unauthorized operation”（需要提升权限），故被放弃。选定设计全程不提升权限；按 Plan.md §6.4，普通运行时不允许悄悄提升权限，本机也没有可用于常规运行的提升路径。

**更正：** “readRoots 为空仍能编译”当时被当作“授权不必要”的证据，但测量 K 表明机制选择期的探针运行本身已把授权写进了 `D:\Qt\Tools\mingw1310_64` 的 5 个目录，因此该推论对本机 MinGW 不成立。依赖目录是否需要授权，取决于**安装位置是否被包类 ACE 覆盖**。

### E. 其他已实测的 AppContainer 行为

AppContainer 会把 `TEMP`/`TMP` 重定向到由 `LOCALAPPDATA` 推导出的容器私有位置。**当时的表述不完整**：本轮探针测量的完整重写规则、目标目录由谁创建、以及产品因此需要的修复见测量 J；据此，编译器中间文件确实可以留在专属实验目录内，而不是只能落在 `Packages` 私有目录。

### F. 测量过程中发现的两个启动器缺陷（记录，因为它们都伪装成隔离策略失败）

1. `STARTF_USESTDHANDLES` 是 `0x00000100`，**不是** `0x00000001`（后者是 `STARTF_USESHOWWINDOW`）。位写错时操作系统会**静默忽略** `hStdInput`/`hStdOutput`/`hStdError`，改用控制台/NUL 句柄，于是子进程所有输出消失且不报错。现象是“gcc 退出码 1，stdout 与 stderr 完全为空，即使给了全部权限也一样”。
2. 对 AppContainer 子进程传入自定义 `lpEnvironment` 时，**环境块必须含有 Windows 用户 profile 的标准变量集合**：`CREATE_UNICODE_ENVIRONMENT` 只修好了一部分，仅有 Unicode 标志而没有标准 profile 变量时 `CreateProcess` 仍返回 `ERROR_ENVVAR_NOT_FOUND`（203），补上该集合后启动成功（更正版见测量 J；旧探针的“任何非 NULL 环境块都失败”结论不成立，已由生产 Runner 反向证伪——产品传的正是自定义环境块，两套工具链均能编译运行）。本轮未逐变量二分定位最小的必需集合，因此记录的是“标准集合可行”，不是“某一变量必需”。设计因此改为**显式构造环境块**：先复制宿主必要的定位变量（`USERPROFILE`、`APPDATA`、`ProgramData`、`SystemDrive`、`ComSpec`、`ProgramFiles*`、`ALLUSERSPROFILE`、`USERNAME`、`USERDOMAIN`），再叠加 `spec.Environment`，最后由容器自身拥有的变量（`SystemRoot`、`WINDIR`、`PATH`、`LOCALAPPDATA`、`TEMP`、`TMP`）覆盖——不是继承整份宿主环境，避免把宿主的任意密钥类变量带进容器。

### G. IPC 结论

容器内代码无法连接宿主命名管道（错误 5），故采用“宿主为管道服务端 + 当前会话随机握手秘密”（Plan.md §5.5），宿主↔容器通信走继承的标准句柄而非 loopback TCP。Runner 协议只携带已登记资源 ID。

### H. 生产 Runner 复测（WP-P1-002/003）

探针只证明机制可行；生产 Runner 用同一机制编译并运行固定夹具，把测量搬进产品路径。夹具 `fixtures/runner/bundles/escape/main.c` 的原始报告（`./scripts/verify.ps1 -Phase P1`，`tests.log` 逐用例打印）：

```
{"isAppContainer":1,"winsockStartupError":0,"insideWriteError":0,"headerReadError":0,
 "containerLoopbackError":0,"tempWithinWork":1,"tempWriteError":0,"tempFileWithinWork":1,
 "unrelatedReadError":5,"ambientAapReadError":0,
 "outsideWriteError":5,"traversalWriteError":5,"junctionWriteError":5,
 "controlPipeConnectError":5,"decoyPipeConnectError":5,
 "tcpV4Error":10060,"tcpV6Error":10060,"udpError":0,"dnsError":11001,"dnsHostNameError":0,
 "childSpawnError":0,"childSpawnStyleB":0,"childSpawnStyleC":0,
 "childIsAppContainer":1,"childConnectError":10060}
```

MSVC 与 MinGW 两种工具链逐用例一致，并与机制选择期的 A/C 相符。相对探针新增的观测：`traversalWriteError=5`（`..\outside` 形式同样被拒）、`controlPipeConnectError=5` 与 `decoyPipeConnectError=5`（含宿主真实控制通道）、`junctionWriteError=5`（宿主在运行目录内建 junction 指向外部）、`tempWithinWork=1` 与 `tempWriteError=0`（测量 J 的修复生效）、`containerLoopbackError=0`（容器自己的 socket 仍可用）。`unrelatedReadError=5` 与 `ambientAapReadError=0` 成对出现，前者是断言、后者是观测（测量 I）；`dnsError=11001` 与 `dnsHostNameError=0` 成对出现，说明容器内的解析器并没有坏掉，它只是无法解析外部名字。

复测确认的机制事实与实现缺陷（记录，因为它们都曾伪装成“隔离策略失败”）：

1. **作业的基线进程数不是常数。** Windows 可能为一个控制台子系统子进程启动 `conhost.exe`（`CreateNoWindow` 也不阻止），它是 Runner 的子进程因而继承作业成员身份。本机在不同次运行中观察到基线为 1 与 2；把清理断言写成绝对值会得到一个与隔离无关的假失败，因此断言一律相对会话刚建立时的计数。
2. **被杀死的 Runner 会留下 AppContainer profile。** 遗留 profile 会让之后每次 `CreateAppContainerProfile` 失败，看起来像“隔离能力永久不可用”。改为在创建失败时用 `DeriveAppContainerSidFromAppContainerName` **沿用**既有 profile（同名推导同一 SID），而不是删除它——删除会干扰另一个正在使用该 profile 的 Runner。
3. **符号链接与 junction 不是同一件事。** `Directory.CreateSymbolicLink` 需要 `SeCreateSymbolicLinkPrivilege` 或开发者模式，本机没有；junction（重解析点 `0xA0000003`，`FSCTL_SET_REPARSE_POINT`）不需要任何权限。边界对两者的判定都基于**最终解析路径**，但自动门禁只能用到 junction，且只有 junction 才是无特权进程真能种下的形状。
4. **MSVC 必须显式链接 `advapi32.lib`。** 夹具调用 `OpenProcessToken`/`GetTokenInformation`；MinGW 默认链接 advapi32，MSVC 不链接，漏掉时表现为 `LNK2019` 的 MSVC 单侧编译失败，容易误判为“MSVC 在该容器内不可用”。
5. **C99 的 `%s` 在宽字符 printf 里是窄字符串。** MinGW 按标准处理、MSVC 按宽字符处理，于是 `swprintf(L"\"%s\"", path)` 在 MinGW 下把 `C:\...` 截成 `C`，子进程以 `ERROR_FILE_NOT_FOUND` 启动失败——伪装成“容器禁止派生子进程”。夹具统一改用 `%ls`。
6. **UDP 外发只能在接收端判定。** 被策略丢弃的数据报在发送端仍返回成功（`udpError=0`），因此该用例以受控 UDP socket 的到达计数（`udpPending=0`）判定，`udpError` 只作为原始证据随帧上报。
7. **停止必须对已结束的运行也给终态回复。** 对已经发出终态帧的运行再次 `StopRun`，若只做清理而不回复，宿主会一直等一个永不到来的 `RunFinished`。生产实现让 `Stop` 返回“是否由本次调用写出终态帧”，未写出时由分发层以 `RUN_CANCELLED` 回复；宿主侧另有 30 秒上界，使沉默的 Runner 表现为失败而不是无限等待。

已实测的边界（复测结论，非推断）：MSVC/MinGW 正常 C 程序在容器内编译并运行成功；专属目录写入与目录外必要只读依赖成功；无关哨兵读、目录外写、`..` 越界写、junction 逃逸、宿主控制通道与同 ACL 形状的诱饵管道、IPv4/IPv6 TCP、UDP、DNS、子进程外发全部被拒；无限循环程序的运行超出观察窗口仍继续，`StopRun` 后程序及其**自己派生的后代**一并退出，进程数回到基线；宿主断开与宿主进程被强杀（无析构、无 Stop）两种情况下都不留 Runner 或程序进程。

**每条否定都有正对照（本轮补齐）。** 子进程用例改为连接**真实的**受控监听端（而不是 `127.0.0.1:9` 这种没人监听的端口，被拒不能证明是策略），并因此把子进程的观察窗口放宽到 60 秒以覆盖外发超时；宿主侧另有对照证明每个目标本来可达：`host-target-reachable`（IPv4/IPv6 TCP 与 UDP 全部由宿主的会话亲自连过、并由 UDP 接收端确认收到）、`host-dns-resolves`（宿主能解析 `example.com`，容器不能）、`unrelated-file-readable-on-host`（宿主能读那个哨兵文件，容器不能）。容器自身的能力也同时被证明未被误伤：写入专属目录成功、工具链头文件可读、容器自己的 loopback socket 可用（`containerLoopbackError=0`）、本机名字可解析。因此“容器坏掉了所以什么都没通过”不再是这套矩阵的一个可能解释。

**仍然诚实记录的弱点：** 子进程用例的 `childConnectError=10060` 只证明子进程的外发被阻断，不证明它被阻断的**机制**与父进程相同；子进程留在容器内（`childIsAppContainer=1`）才是身份证据。人工步骤中另需确认“关闭应用后无遗留进程”这一真实场景——自动门禁杀的是一个真实的第二宿主进程（`tests/StudyHarness.HostProbe`），它能同时触发异常死亡的两个后果（管道关闭与作业句柄关闭），但不是用户亲手关闭窗口。最后，本机的 MinGW 依赖可读性依赖本项目探针留下的 ACL 残留（测量 K），干净基线上的表现尚未实测。

### I. 读边界：哪些包级 ACE 形状可读，与 LPAC 对照（更正期实测，2026-10-14）

证据：`artifacts/review-P1-20261014/containment-probe/read-20261014-185931/`（7 种 ACL 形状）与
`.../boundary-20261014-185139/`（两个候选机制同一文件、同一组 socket）。合成探针由 `probes/StudyHarness.IsolationProbe/` 启动，与产品同一启动形状。

**哪些 ACE 形状能被容器读到**（每个形状一个文件；`controlReadError` 是容器对启动器目录内允许可读文件的对照，全部为 0，证明容器本身没有被“读不了任何东西”弄坏）：

| 文件 ACL 形状 | `sharedReadError` |
|---|---|
| 仅交互用户 `(F)`，关闭继承 | **5** |
| `BUILTIN\Users (RX)` | **5** |
| `NT AUTHORITY\Authenticated Users (RX)` | **5** |
| 用户 `(F)` + `BUILTIN\Users (RX)` | **5** |
| 真实 `%USERPROFILE%` 内的普通文件（继承 ACL） | **5** |
| 真实 `%LOCALAPPDATA%` 内的普通文件（继承 ACL） | **5** |
| 用户 `(F)` + 文件上显式 `S-1-15-2-1:(R)`（ALL APPLICATION PACKAGES） | **0** |
| 用户 `(F)` + 文件上显式 `S-1-15-2-2:(R)`（ALL RESTRICTED APPLICATION PACKAGES） | **0** |
| 用户 `(F)` + 文件上显式某个**别的**逐容器 SID `S-1-15-2-<非本容器>` | **5** |

后两行由 `containment-probe/grantshape-20261014-192506/` 补测（脚本 `run-grant-shape-probe.ps1`，同一启动形状、同一探针）。它们区分了三件此前混为一谈的事：标准 AppContainer 令牌**同时**认可两个包级 SID（AAP 与 ARAP），而**不**认可任何“看起来像包 SID”的陌生逐容器 SID——后者（宿主侧 `icacls` 里以 `S-1-15-2-<数字>` 形式留存的旧 profile 身份）对当前容器等于不存在。

结论：AppContainer 令牌不因用户/`Users`/`Authenticated Users` ACE 获得任何读权限，因此 REQ-034 所说的“无关用户文件”——包括用户自己 profile 与数据根父目录中的文件——**按构造不可读**；可读的形状只有“显式授予两个包级 SID 之一”这一种，而这正是工具链依赖可读所依赖的同一类 ACE（本机 MinGW 目录实际带的是 ARAP 那一条，见测量 K）。这不是配置错误，而是机制内生的读边界：机制无法既尊重环境中的包类授权、又拒绝它。产品因此把这一条从断言改为**每次运行上报的观测量**（`ambient-aap-grant-measured`），并把对应的拒绝断言换成有正对照的 `unrelated-file-read-denied`（`unrelatedReadError=5`，同一次运行中 `unrelated-file-readable-on-host` 为 0）。

**两个候选机制在同一组目标上的对照**（`externalConnectError` 为连接 `1.1.1.1:443`）：

| 变体 | AAP 授权文件 | 到宿主 LAN 监听端 TCP | `externalConnectError` | 外网 DNS |
|---|---|---|---|---|
| 标准 AppContainer | **0**（读到） | 10060（阻断） | 10013（阻断） | 11001（阻断） |
| 标准 AppContainer + 显式容器读授权 | **0** | 10060 | 10013 | 11001 |
| LPAC + `registryRead`,`lpacCom` | 5（拒绝） | 10013（连 socket 都没有） | 10013 | 11003 |
| LPAC + 上述 + `internetClient` | 5（拒绝） | 10060 | **0（连通外网）** | **0（解析成功）** |
| LPAC + 上述 + `privateNetworkClientServer` | 5（拒绝） | 10060 | 10013 | 0 |

结论：LPAC 能拒绝 AAP 授权文件，但没有能力就没有 socket，而恢复 socket 的能力同时恢复了外网可达性（`internetClient` 下既能连 `1.1.1.1:443`、也能解析外网名）。同时满足“本机通信可用”与“外网被阻断”的候选只有标准 AppContainer，故维持决策 1，并把“包级授权文件可读”记为已知限制（两个包级 SID 都算，见上表后两行）。这也更正了 ADR-003 引用的旧 LPAC 探针：LPAC 加能力后**确实**能建 socket、能派生子进程（旧“Winsock 初始化也成功”成立），问题在于代价。注意 LPAC 对 ARAP 授权文件是**可读**的（其受限 SID 列表即含 ARAP），“拒绝 AAP 授权文件”不等于“拒绝一切包级授权文件”。

### J. 环境块与临时目录：重写规则、由谁创建、产品修复（更正期实测，2026-10-14）

证据：`containment-probe/read-20261014-185931/`（未预创建重写目标）与 `containment-probe/boundary-20261014-185139/`（预创建）；产品侧的对照是 `./scripts/verify.ps1 -Phase P1` 的 `tempWithinWork`/`tempWriteError`/`tempFileWithinWork`。

1. **自定义环境块可行，但必须带标准 profile 变量。** 见 F.2 的更正版：产品传入自定义 Unicode 环境块后两套工具链均能编译并运行固定夹具，所以“AppContainer 不接受自定义环境块”被证伪；而只有 Unicode 标志、没有标准 profile 变量的最小环境仍得到 203。
2. **`TEMP`/`TMP` 的传入值会被丢弃。** 探针传入 `LOCALAPPDATA=<work>\appdata`、`TEMP=TMP=<work>`，容器内读到的 `TEMP`/`TMP` 是 `<work>\appdata\Packages\<profile 小写>\AC\Temp`：容器把**传入的 `LOCALAPPDATA`** 重写为 `<该值>\Packages\<profile 小写>\AC`，再由它派生 `TEMP`/`TMP`，`GetTempPathW`/`GetTempPath2W` 返回同一路径。因此只改 `TEMP`/`TMP` 没有效果（这正是 ADR-003 反例 2 的成因），必须改 `LOCALAPPDATA`。
3. **没有任何组件创建那棵树。** 未预创建时 `tempDirWriteError=3`（ERROR_PATH_NOT_FOUND）、`getTempFileNameError=267`（ERROR_DIRECTORY）；同一探针在预创建后为 0。产品因此在启动容器前预创建整条链（`Sandbox.ContainerProfile.Prepare`），并由夹具断言临时目录在专属实验目录内且可写：`tempWithinWork=1`、`tempWriteError=0`、`tempFileWithinWork=1`（MSVC/MinGW 一致）。
4. **`SHGetFolderPathW(CSIDL_LOCAL_APPDATA)` 仍解析到真实 profile 的 `Packages\<profile>\AC`**：本次修复移动的是环境变量与 `GetTempPath` 这条编译器实际使用的表面，不是 KnownFolder 的解析结果。因此“临时产物不出实验目录”这一条按“编译器与 `GetTempPath` 系列 API 的视角”成立；走 shell KnownFolder API 的代码仍会拿到容器私有存储。当前无产品路径使用该 API，若将来引入需重新评估。



### K. 依赖可读性的真实来源：OS 默认 ACE 与本项目自己的授予（更正期实测，2026-10-14）

本 ADR 开头与测量 D 的结论“工具链目录已带继承 ACE，因此目录外依赖按构造即可读，无需我方授权”**只对 MSVC 成立**。逐级读 ACL 后的事实：

| 路径 | 包类（AppContainer）ACE | 是否继承 |
|---|---|---|
| `C:\Program Files` | ALL APPLICATION PACKAGES + ALL RESTRICTED APPLICATION PACKAGES，ReadAndExecute | **显式**（非继承），Windows 默认 |
| `C:\Program Files (x86)` | 同上 | **显式**（非继承），Windows 默认 |
| `C:\Program Files (x86)\Windows Kits\10`（MSVC 工具链所在） | 同上 | 继承自上面两条 |
| `C:\` | 无 | — |
| `C:\Users\<用户>\AppData\Local\Programs` | 无 | — |
| `D:\`、`D:\Qt`、`D:\Qt\Tools`、`D:\Qt\Tools\mingw1310_64` | 无 | — |
| `D:\Qt\Tools\mingw1310_64\{bin,lib,libexec,include,x86_64-w64-mingw32}` | 各 **14 条**显式 ACE：13 个**逐容器** SID + 1 个 `S-1-15-2-2`（ALL RESTRICTED APPLICATION PACKAGES），ReadAndExecute；**没有** AAP | **显式**（非继承） |

即：MSVC 可读是**操作系统默认**——`Program Files` 两级目录显式授予包类读权限并可继承，因此装在其中的工具链天然可读，这与我们的机制无关，结论稳健。MinGW 则不同：`D:\` 整条链上**没有任何**包类 ACE，容器今天能读 MinGW，完全来自那 5 个目录上 14 条显式 ACE 中的一条。

对这 14 条 ACE 的逐条判读与残留来源（`artifacts/review-P1-20261014/containment-probe/grantshape-20261014-192506/`）：

- **只有那条 `S-1-15-2-2` 真正在起作用。** 补测的第 8、9 种 ACL 形状给出答案：给一个普通文件加上 `S-1-15-2-2:(R)`，标准容器读到（`sharedReadError=0`）；加上一个**别的**逐容器 SID，容器读到的是 `5`。也就是说，那 13 个逐容器 SID 对当前容器**等于不存在**，本机 MinGW 的可读性完全落在一条 ARAP ACE 上。
- **那条 ARAP ACE 是本项目自己加的。** `artifacts/acceptance/P1-probe/20261014-115531-ebde78/MinGW/lpac-arap/{spec,launch}.json` 里 `extraReadSids: ["S-1-15-2-2"]`、`readRoots` 恰为这 5 个目录，journal 逐条 `grant=ReadAndExecute path=D:\Qt\Tools\mingw1310_64\<dir> sid=S-1-15-2-2`；`D:\Qt\Tools\mingw1310_64` 自身与其父目录都没有这条 ACE，所以它不是 Qt 或 Windows 留下的。
- **那 13 个逐容器 SID 是另一批探针的产物。** `.../20261014-115055-47fda2/MinGW/appcontainer-grant/launch.json` 的 journal 记着 `grant=ReadAndExecute path=D:\Qt\Tools\mingw1310_64\<dir> sid=S-1-15-2-2161487162-…`，正是这些目录。逐条对账：`artifacts/` 下 10 个 `launch.json` 的 journal 出现过对 `mingw1310_64\bin` 的包类授权（4 次 `MinGW/appcontainer-grant` + 5 次 `MinGW/lpac-grant` 的 per-profile SID，以及 1 次 `lpac-arap` 的 `S-1-15-2-2`），对应 9 个可追溯的逐容器 SID；ACL 上却有 13 个，**余下 4 个在任何留存的证据目录里都找不到**（早先探针运行的输出目录已不存在）。它们对本容器无效，属纯残留（对今后的清理是噪声，对隔离结论无贡献）；同时也说明这些目录上的残留比留存的记录更多，只读 ACL 不足以反推运行史。

**因此“无需授权”这一结论对本机 MinGW 是被自己制造的现状误导得出的**：授权并非不必要，而是早已被同一批探针运行加好了。这同时解释了为什么 `readRoots` 为空的 `*-nogrant` 变体也能编译——它们运行时授权已经存在。产品源码中“使工具链可读的是 ALL APPLICATION PACKAGES 这一类 ACE”的说法据此更正为“使工具链可读的是包级 ACE，且本机 MinGW 上具体是 ARAP 那一条”（`Sandbox.cs`、`RunSession.cs` 的注释已同步更正）。

由此得到的、必须记录而非掩盖的结论：

1. **依赖可读性取决于安装位置。** 装在系统盘 `Program Files`（含 `(x86)`）下的工具链由 OS 默认 ACE 覆盖；装在非系统盘（如 `D:\Qt`）或用户 profile（`%LOCALAPPDATA%\Programs`）下的工具链不在覆盖范围内。AppContainer 令牌不会被 user/`Users`/`Authenticated Users` ACE 授权（测量 I），因此那些位置的工具链**必须**有包类读授权才能被容器使用。
2. **Plan.md §6.4 恰好要求的就是这个。** 该节禁止对“整个依赖目录”泛化读权限，同时要求“依据探测结果建立**必要依赖白名单**”。因此正确处理不是“什么都不授权”，而是：工具链发现阶段探测容器内可读性，对未被覆盖的位置给出明确结论（白名单/安装要求），而不是靠残留 ACE 静默通过。
3. **尚未验证（记为 pending，不记为通过）。** “移除那 5 个目录上的 14 条 ACE 后 MinGW 是否仍能在容器内编译”没有被执行：该操作修改共享工具链的 ACL，需要用户授权。可复现脚本与快照已备好：`artifacts/review-P1-20261014/acl-baseline/{snapshot,remove,restore}.ps1`（只删 `S-1-15-2-*` 显式 ACE，不触碰用户/`Users`/管理员 ACE，因此不影响 MinGW 的正常使用；`restore.ps1` 用 SDDL 精确还原）。在用户决定之前，只能陈述“本机当前状态下门禁通过”，不能陈述“干净基线上同样通过”。
4. **旧探针也无法证明授权的必要性。** `*-grant` 变体是通过“施加授权”来观测成功的，而成功又被用来论证“不需要授权”；这是本轮出现的唯一方法论错误，记录在此以免重犯。

## 被拒绝的备选与拒绝依据

- **LPAC（Low Privilege AppContainer）** — 测量 B + I：不加能力时三种子进程派发方式全部 `5`、Winsock 启动 `10107`；加 `registryRead`/`lpacCom` 后子进程与 socket 都能工作（更正本节旧结论，与 ADR-003 的观察一致），但仍无本机通信能力；再加 `internetClient` 则**外网可达**（`externalConnectError=0`、外网 DNS `0`），加 `privateNetworkClientServer` 则外网 DNS 可达（`0`）。能拒绝 AAP 授权文件的配置都无法同时提供本机通信并阻断外网，故拒绝。
- **“给工具链目录授权是不必要的”（旧结论，已按测量 K 更正）** — 机制选择期据 `readRoots` 为空仍能完成双工具链编译运行，推出“无需授权”；测量 K 证明该推理被自己留下的残留 ACE 污染：本机 MinGW 今天能读，靠的正是本项目早先探针加在 5 个目录上的那一条 `S-1-15-2-2` ACE，而 `D:\` 链上并无任何包类默认授权。对 MSVC 结论仍然成立（`Program Files` 的 Windows 默认包类 ACE）。产品当前**不**在运行时改动工具链目录 ACL，因此这一项不是“被否决的备选”，而是测量 K 的待决事项（安装前提 vs 发现阶段白名单探测）。
- **向 Windows Kits 新增 ACE** — 测量 D：对 `C:\Program Files (x86)\Windows Kits\10` 新增 ACE 报 “attempted to perform an unauthorized operation”，需要提升权限，违反 §6.4 不得静默提升；MSVC 也本就无需授权。
- **用 Job Object（含 `KILL_ON_JOB_CLOSE`）承担文件边界** — Plan.md §6.4 明确 Job Object 只解决进程树，不能当作完整沙箱；本机测量也显示边界来自 AppContainer，Job Object 仅用于整树回收。
- **向 `CreateProcess` 注入 `lpEnvironment` 而缺少标准 profile 变量** — 测量 F.2/J：只补 Unicode 标志、不含 Windows 用户 profile 标准变量的最小环境块返回 `203`（ERROR_ENVVAR_NOT_FOUND）；带标准变量集合的环境块可用（产品即如此）。被拒绝的是“缺少必需变量的环境块”，不是“自定义环境块”。
- **用 loopback TCP 做宿主↔容器 IPC** — 测量 A/G：容器内到宿主的命名管道连接 `5`，且本机通信在受限身份下不可假定；改用继承的标准句柄。
- **“普通进程执行”** — 不是备选。§6.4 禁止其作为可发布兜底；生产 Runner 建立容器失败即 `ISOLATION_UNAVAILABLE` 并拒绝执行。

## 后果与已知限制

- **仅 Windows。** 该机制由 Windows 内核对象（AppContainer 令牌、Job Object）构造，不存在跨平台等价物；REQ-032 的首期范围本就是 Windows 单机，故不构成本期缺口。
- **读边界等于包级（AppContainer）ACE 的可读范围。** 普通用户文件——本人、`Users`、`Authenticated Users`、真实 `%USERPROFILE%` 与 `%LOCALAPPDATA%` 中的文件——实测全部不可读（测量 I）。可读的形状只有显式授予两个包级 SID 之一（`S-1-15-2-1` 或 `S-1-15-2-2`）这一种，而这正是“工具链依赖可读”所依赖的同一类 ACE，因此这条限制无法在机制内消除，只能记录并每次上报（`ambient-aap-grant-measured`）。**被授予某个陌生逐容器 SID 的文件不在此列**，实测为 `5`。
- **依赖可读性取决于安装位置（测量 K）。** 系统盘 `Program Files`（含 `(x86)`）由 Windows 默认 ACE 覆盖包类读权限，装在那里的工具链（如本机 MSVC）按构造可读；非系统盘（`D:\Qt`）与用户 profile 下的工具链**不**被覆盖，Plan.md §6.4 要求的“必要依赖白名单”对它们才是必需的。本机 MinGW 当前可读性完全来自本项目探针留下的一条 `S-1-15-2-2` ACE（其余 13 条逐容器 ACE 实测对本容器无效），干净基线上的行为尚未实测，也未在用户决定前做任何 ACL 改动。
- **保证是内核强制的。** 不是工作目录设置、路径检查或进程树终止——这正是 §6.4 要求的性质；相应地，边界失效时不会有“软降级”，只会是拒绝执行。
- **临时目录按编译器视角留在专属目录内（测量 J）。** `LOCALAPPDATA` 被指向运行数据根、重写目标被预创建，`TEMP`/`TMP`/`GetTempPath`/`GetTempFileName` 全部落在其中；但 `SHGetFolderPathW(CSIDL_LOCAL_APPDATA)` 仍解析到真实 profile 的 `Packages\<profile>\AC`。当前产品路径不使用该 API，将来引入需重新评估。
- **本机通信受限。** 容器内与宿主直连不可用，宿主必须始终作为管道服务端；握手秘密不得进入用户代码可读目录。
- **生产 Runner fail-closed。** 任何无法建立容器的情况（API 不可用、profile 创建失败、作业分配失败）都必须映射为 `ISOLATION_UNAVAILABLE` 并拒绝执行，不得以“普通进程执行”兜底。该拒绝路径属 WP-P1-002 的实现与验证内容，本 ADR 只固定契约。
- **启动器缺陷是启动器问题，不是隔离策略结论。** F 的两项必须作为回归点保留，否则会再次把启动器 bug 误判为策略失败并浪费排查时间。

## 未完成与下一步

- WP-P1-002（认证 IPC、生命周期、停止与异常的整树清理）与 WP-P1-003 的自动部分已完成：`./scripts/verify.ps1 -Phase P1` 是一条命令的入口，9 项检查全通过（`failed=blocked=skipped=0`），43 项测试全部实际执行且通过（证据 `artifacts/acceptance/P1/20261014-192737-37a11e/summary.json`），探针的角色已收敛为 A–K 的可复现路径。
- **人工步骤（H 门禁）未执行也未签**，见 [P1 人工验收步骤](../acceptance/P1.md)：在真实用户机器上配置两套工具链并运行样例、对无限循环点击停止、关闭应用后确认无遗留进程、复核安装权限与依赖说明，以及本 ADR 测量 K 提出的**工具链可读性基线**一项。
- **待用户决定（测量 K）：** ① 是否授权在 `D:\Qt\Tools\mingw1310_64` 上做一次可回滚的“移除本项目残留 ACE → 复测 MinGW 隔离编译 → 精确还原”实验（脚本与快照已备好），以判定干净基线下 MinGW 半边是否成立。本轮补测给出的**可检验预测**是：移除后容器内读 `…\x86_64-w64-mingw32\include\stdio.h` 应为 `5`、MinGW 编译应失败，而 MSVC 不受影响（其可读性来自 `Program Files` 的默认 ACE）——实验若与此不符，则本测量仍有缺口。② 若不授权，是否接受把“工具链所在目录必须被包类 ACE 覆盖”作为**安装前提**写进依赖说明，并在工具链发现阶段探测可读性、对未覆盖的位置给出明确结论。在此之前 P1 只能声明“本机当前状态下门禁通过”。
- 尚未实测：符号链接逃逸（缺特权）、大小写/短路径别名、同一容器内并发运行、长时间运行的资源漂移、干净工具链安装基线上的依赖可读性；这些不构成本期通过声明的一部分。
- 生产 Runner 目前只执行已登记的固定夹具（`hello` / `escape` / `loop`），不含学习者代码路径；真实验证属后续工作包。
- 本 ADR 的证据来自单一机器（Windows 11 build 26200，x64），不构成对全部受支持 Windows 版本的覆盖声明。

## 证据与复现（检查于 2026-10-14）

- 参考证据集：`artifacts/acceptance/P1-probe/20261014-121056-53e78a/`
  - `summary.json`（各检查点状态、原始错误号与监听端到达计数）
  - `MSVC/appcontainer-nogrant/`、`MinGW/appcontainer-nogrant/`、`MSVC/lpac-nogrant/`、`MinGW/lpac-nogrant/`（各含 `spec.json`、`launch.json`、`probe.json`、stdout/stderr）
  - `MSVC/compile/`、`MinGW/compile/`（`compile-spec.json`、`compile-launch.json`、`run-spec.json`、`run-launch.json`、`work/`）
  - `MSVC-fixture-compile.log`、`MinGW-fixture-compile.log`、`probe-build.log`
- 探针与夹具：`scripts/probe-p1.ps1`、`fixtures/runner/capability-probe.c`、`probes/StudyHarness.IsolationProbe/`。
- 更正期证据（测量 I/J 与 ACE 形状补测）：`artifacts/review-P1-20261014/containment-probe/`，含 `README.md`（读数表与重写规则）、`read-20261014-185931/`（7 种 ACL 形状）、`boundary-20261014-185139/`（标准 AppContainer 与三种 LPAC 配置的对照）、`grantshape-20261014-192506/`（第 8、9 种形状：`S-1-15-2-2` 与陌生逐容器 SID）以及三份驱动脚本与探针源码。
- 依赖可读性基线（测量 K）：`artifacts/review-P1-20261014/acl-baseline/`，含 `mingw-acl-snapshot.json`（5 个目录的 SDDL 快照）与 `snapshot`/`remove`/`restore` 脚本；快照已生成，`remove` **未执行**（需要用户授权）。ACE 残留的出处见 `artifacts/acceptance/P1-probe/20261014-115531-ebde78/MinGW/lpac-arap/spec.json`（`extraReadSids: ["S-1-15-2-2"]`）与 `.../20261014-115055-47fda2/MinGW/appcontainer-grant/launch.json`。
- 探针按变体与工具链生成上述检查点，并逐检查点原子写入 `summary.json`；无法到达夹具的运行记为 blocked，不记为 pass。
- 生产复测（测量 H）：`./scripts/verify.ps1 -Phase P1` 的证据目录 `artifacts/acceptance/P1/<run-id>/`，其中 `tests.log` 逐用例打印原始错误号与夹具的 `escape.json`，`toolchains.log` 给出本机可达的工具链，`summary.json` 记录各检查状态与未运行项。夹具源码：`fixtures/runner/bundles/escape/main.c`；宿主与 Runner 的协议契约测试见 `tests/StudyHarness.Tests/Runner/`。
