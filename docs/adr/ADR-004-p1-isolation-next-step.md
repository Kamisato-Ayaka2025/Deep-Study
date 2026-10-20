# ADR-004: P1 严格读边界的后续机制验证

- 状态：proposed；未选定替代机制，未通过 P1。
- 关联：REQ-034；Plan §6.4；TURN-20261015-001。本文不修改需求。

> 11:47 检查点：优先继续本机 PSEC 1.1 候选，暂不需要切换 VM。真实 API 返回 flags=0xF、version=1.1，并且实际创建环境成功。只配 allow 仍读到 AAP/ARAP；配所有已挂载磁盘根 deny、更具体依赖/工作目录 allow 后，两类无关文件都拒绝 5。loopback egress allow + networkLoopback 能力使自身 socket 成功。尚未验证双工具链、外发与生命周期全矩阵，不代表选型完成。下方 VM 仅保留为候选失败后的备选。

> 15:18 更新：PSEC 双工具链编译/运行与 work 临时写小探针已通过；其余攻击与生命周期矩阵仍未完成。用户明确只能使用当前 Home 机器，当前路线固定为继续验证本机 PSEC。没有要求安装 Windows Sandbox，没有获准降低 REQ-034。

## 已确认的约束

标准 AppContainer 不是“只可读取指定目录”的白名单：无关用户文件只要具有 AAP 或 ARAP ACE，就可能被读取。普通路径检查、Job Object、禁网、防火墙均不会撤销这种文件授权。LPAC 去掉 AAP 仍保留 ARAP，因此“LPAC + WFP”本身也不能宣称修复严格读边界。

TURN-007 的随机限制 SID + AppContainer 在进程初始化时返回 0xC0000022，未到夹具；不能把程序无法运行作为隔离成功。本轮没有证明所有受限令牌组合都不可行，也不宣称 Windows 上需求不可实现。

本机注册表 EditionID=CoreCountrySpecific，build=26200；未发现 System32/WindowsSandbox.exe。微软明确 Windows Sandbox 不支持 Home 版，不能直接用一份 .wsb 配置在当前机器完成验收。硬件虚拟化可用性尚未确认。

## 优先验证候选：独立 Windows 虚拟化环境

先做一次固定 C 夹具的机制验证，成功后再决定是否重写 Runner 后端：

1. 测试环境须支持 Windows Sandbox，或提供明确的其他 Windows VM 后端与可用客体系统；不擅自购买许可证、升级宿主、安装驱动或启用重启功能。
2. 禁用虚拟机网络适配器/外部网络，保留客体内部 loopback。关闭剪贴板、设备重定向及任何隐式宿主目录共享。仅映射每次运行新建的私有输入/输出目录，依赖使用独立只读副本。
3. 在客体内实际执行 MSVC 与 MinGW 编译；不要把只在宿主编译、客体运行当作完整通过。验证工具链 DLL、头文件、SDK 和库的完整性，不能只复制 cl.exe/gcc.exe。
4. 将带 AAP/ARAP 授权的宿主哨兵放在未共享路径，证明无法读取；所有可写映射、客体差分盘、临时数据和报告都位于本次专属运行根。客体路径不存在本身不能冒充原生错误 5，需设计可验证的命名空间隔离对照并审查验收方法，保留原有进程沙箱用例。
5. 明确“本机通信”包含范围：虚拟化候选至少证明客体内 loopback；宿主任意进程直连不会自动成立。若用户要求与宿主服务通信，需额外设计受控通道，不能默认豁免。
6. 正常退出、手动停止、宿主崩溃后关闭本次 VM/作业并回收差分盘；不设置产品运行时长上限。重复、并发与中断清理必须有实际证据。
7. 只有文件、网络、双工具链、生命周期全部通过，才替换产品路径并跑完整 P1 gate，再交 H1–H9。概念设计不算交付。

这条路线需要可用的 Windows 虚拟化测试环境，也涉及运行后端和本机通信语义；先确认这些实际前提，再实施系统安装或产品架构切换。

## 如果必须保持当前宿主进程模型

继续研究由内核执行的文件白名单（例如具备签名与明确安装/卸载流程的文件系统过滤组件）及网络策略。必须覆盖重解析点、已打开句柄、进程派生、并发与崩溃；仅注入 DLL/拦截 Win32 API 不能抵挡原生系统调用。本仓库尚无此组件、签名或安装包，不能把它写成已可用，也不能要求用户先管理员运行现有 Runner 来碰运气。

## 来源

- [Microsoft MXC 源码](https://github.com/microsoft/mxc/tree/6f6e43683877d68a43fdd1d6c201808c914c5685/src/mxc-sdk/src/backends/process_container/common)：PSEC FFI、FlatBuffer 规格和网络规则；本轮缓存到 artifacts/review-P1-20261015/psec/mxc，仅作为参考，未执行第三方安装器。网页缓存旧路径为 404，已用真实 Git checkout 核对当前路径。
- [Microsoft: QueryProcessSecurityEnvironmentSupport](https://learn.microsoft.com/en-us/windows/win32/api/processmodel/nf-processmodel-queryprocesssecurityenvironmentsupport)：能力查询；FFI flags 实际为 UINT64，按 MXC 源码使用 ulong 复核。
- [Chromium: LPAC](https://chromium.googlesource.com/chromium/src/+/main/docs/design/sandbox.md)：LPAC 仍承认 ARAP 与指定 package SID。
- [Microsoft: Restricted Tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens)：普通 SID 与 restricting SID 两次访问检查。
- [Microsoft: Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)：独立内核的虚拟化隔离；Home 不支持。
- [Microsoft: Windows Sandbox configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)：网络、映射目录、剪贴板等配置项。

来源核对日期：2026-10-15。候选未实测，不据文档宣称验收通过。
