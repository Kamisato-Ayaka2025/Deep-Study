# 本机替代隔离与磁盘硬限额方案评估

日期：2026-10-17；TURN-20261017-013；状态：只读调研完成，方案待用户选择，PSEC仍blocked。

## 建议

优先开展**现有VMware Workstation + 专用Windows客体 + 客体内PSEC/Job + 预分配固定实验盘**的有限原型验证。它有机会绕开“Windows宿主挂载文件映射VHD被PSEC拒读”的路径：实验盘由虚拟硬件控制器呈现给客体，客体不再使用宿主Mount-DiskImage得到的卷。但这是架构推断，必须先验证客体PSEC能否读取该盘、编译和填盘；不能声称换成VMDK就已经修复。

如果已有可清空的备用小容量存储设备，**宿主PSEC + 专用物理NTFS卷**是代码改动较少的备选验证，可能比VM迁移更快。当前没有确认这样的空白设备，不应将现有F盘或无盘符系统分区当作空闲盘。

两者均保留原安全要求；没有候选已在本机通过完整S1–S8。当前没有“改一个设置即可安全开放P5”的方案。

## 本机实测事实

只读CIM、注册表及已知安装目录文件版本，未启动/检查用户现有虚拟机内容。原始证据：artifacts/psec-alternatives-013/environment.json、virtualization-apps.json、vmware-binaries.json。

| 项目 | 当前观察 | 对方案的影响 |
|---|---|---|
| 系统 | Windows 11 家庭中文版25H2，26200.9711，x64 | 无受支持的Hyper-V角色/Windows Sandbox宿主入口；不要用Home破解启用脚本 |
| CPU | Ryzen 9 7945HX；固件虚拟化、SLAT均True | 具备硬件虚拟化条件；具体VM兼容性仍要启动验证 |
| 内存 | 约16GB，OS可用总量约15.7GiB | 初始只开1台VM，建议4 vCPU、6GiB内存；属容量估算，不是新增R-MEM-01门禁 |
| 当前Hypervisor/VBS | HypervisorPresent=false，VBS状态0 | 当前未发现Hyper-V/VBS占用；不需要为方案关闭现有系统防护 |
| 已安装虚拟化 | VMware Workstation 25.0.0 build-24995812，D:\Vms；VMAuthdService运行 | 优先复用现有产品，先补安全更新；服务存在不等于隔离验证通过 |
| C: | NTFS，空闲约16.6GiB | 不适合放新的Windows实验VM |
| D: | NTFS，空闲约60.6GiB | 当前不足建议的预分配VM预算；可由用户整理个人数据腾空间，不需重分区 |
| E: | NTFS，空闲约29.2GiB，项目所在 | 不适合容纳完整Windows VM和模板 |
| F: | USB物理盘上的exFAT卷，空闲约305.5GiB | 容量多但缺少NTFS ACL，且有USB断连条件；不是空白盘，不擅自格式化/转换。不能仅因容量够而直接作为正式后端 |
| G: | 当前2GiB VHD对应NTFS卷 | 已挂载，但PSEC读取失败，保留作复现 |

未查询/变更许可密钥、BIOS、系统可选功能、网络策略或磁盘分区。没有确认可用于Windows客体的授权，亦不假定宿主OEM授权自动涵盖额外客体。[Windows 11最低存储要求](https://support.microsoft.com/en-us/windows/experience/windows-11-system-requirements)为64GB；以下工程预算不是厂商最低要求。

## 候选比较

| 候选 | 能否保持要求 | 本机可行性与代价 | 排序 |
|---|---|---|---|
| VMware专用Windows VM + 客体PSEC + 固定盘 | 有条件：双层隔离、禁共享/网卡、客体loopback拒绝、固定工作卷及宿主附属文件上限都要实测 | 已安装，需更新、客体授权、额外NTFS空间、Runner适配；仍需客体内PSEC兼容验证 | 主推荐原型 |
| 宿主PSEC + 空白物理NTFS专用卷 | 有条件：容量硬边界明确，保留现有隔离；USB/设备身份、失挂和两工具链待测 | 代码改动较少；需要明确指定可清空备用设备。格式化/分区需单独授权 | 有备用盘时可先验证 |
| 升级Pro + Hyper-V专用Windows VM | 可采用与VMware类似设计，不能仅开启Hyper-V算完成 | 需系统升级/重启、同样客体与存储资源；本机已有VMware，无首选收益 | 备选 |
| Windows Sandbox | VM隔离有价值，但官方配置没有本项目所需每任务磁盘硬容量参数 | Home不支持；共享映射输出也不能当容量边界；升级后仍需额外实现 | 不作为直接替代 |
| VirtualBox / QEMU-WHPX Windows客体 | 逻辑上可采用VM路线，仍有相同客体隔离/容量和通信适配工作 | 新安装或启用平台、重启与新兼容风险；WHPX是QEMU加速后端，不等于直接具备安全Runner | 不优于现有VMware |
| WSL2 / Linux容器 + Linux配额 | 不是现有MSVC/MinGW Windows双工具链合同的等价实现 | MSVC Windows环境和现有Windows隔离集成需重写；默认宿主挂载/互操作也要另行封闭 | 本期不选 |
| 普通NTFS独立用户硬配额 | 单独不满足：按所有者计费，重解析点/安全描述符/其他元数据不计入 | 换独立账户也不能补齐元数据容量边界 | 排除单独使用 |
| FSRM目录硬配额 | 不能直接套用到当前家庭版客户端 | 官方适用Windows Server，需改变部署系统 | 不选 |
| RAM盘/第三方配额过滤驱动 | 理论上可以设计硬边界，但必须审计驱动、所有写路径、元数据/ADS及故障语义 | 新内核驱动、16GB内存压力，现无已验证实现；不启动内存专项 | 不推荐本期引入 |
| IO限速、定时统计、超额后杀进程 | 不是容量强制边界，存在写入窗口 | 无法覆盖任意短时写入和元数据 | 排除 |

事实来源：[Hyper-V Home限制](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v)、[Sandbox支持范围](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)、[Sandbox配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)、[NTFS quota例外](https://learn.microsoft.com/en-us/windows/win32/fileio/disk-quota-limits)、[FSRM适用范围](https://learn.microsoft.com/en-us/windows-server/storage/fsrm/quota-management)、[Job IO rate说明](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/ns-jobapi2-jobobject_io_rate_control_information)、[QEMU WHPX](https://www.qemu.org/docs/master/system/whpx.html)。可行性与排序是本项目评估，非厂商认证。

## VMware方案的必要设计

1. **不是在普通VM里直接运行任意代码。** 建立无个人资料/无API密钥的独立客体，隔离编译器与程序仍由客体可信监督进程控制，保留PSEC对系统/工具链只读、无关文件拒读、目录外拒写，以及Job进程数/CPU/停止约束。PSEC在虚拟硬件磁盘上的可用性是首个待验证门槛。
2. **真实断开宿主通道。** 测试阶段移除虚拟网卡（不是改为host-only），不配置共享文件夹、剪贴板、拖放、USB自动连接、宿主磁盘直通或3D加速。客体loopback仍可能可用，必须另由客体PSEC验证拒绝；不能把无网卡当作完整禁网。仅允许宿主授予的受控任务输入输出。
3. **硬容量覆盖全部写入。** 例如预分配80GiB客体系统盘和2GiB实验盘，用户程序只能写实验盘；TEMP、profile、编译产物都在该盘。固定容量保证总卷占用不能越过设备大小，元数据耗尽同样只能消耗该卷。PSEC若仍拒读即停止路线，不改为宿主编译。
4. **VM宿主文件也要有预算。** 预分配VMDK不是整个VM的总上限。快照delta、日志、vmem、暂停状态、转储、输出证据都可能额外占空间。禁自动快照/无限链和暂停；单实例，日志大小/数量、输出累计量和保留份数有硬边界，启动前一次性预留本次所有最大占用。不能靠磁盘快满后定时检查替代上限；若不能证明附属文件有界，方案仍不通过。
5. **不要在宿主直接自动挂载不可信输出盘。** 优先设计可信客体监督进程通过受限、限长的非网络传输通道返回退出码/stdout/stderr；源文件以不可变输入传入，可用只读ISO。控制通道不能暴露给实验进程；宿主只接收关联runId/hash的白名单消息，不接受任意命令、路径或客体自写成绩单。该桥接目前未实现，需要独立安全审查。
6. **恢复与证据分离。** 可信基线保持不变；后续选择一个预分配完整运行副本复用/还原的有界流程，避免无限快照。P5首次通过源快照仍来自实际执行输入，不能拿VM状态恢复替代用户实验快照和完成事务。

初步预算：单台原型80GiB系统+2GiB实验+6GiB内存支持文件+约20GiB工具/安装/余量，建议NTFS可用空间至少110GiB；保留80GiB基线与80GiB运行副本的工程版本建议预留200GiB。实际安装后重新测量，不能假定这些估值就是已实现硬上限。目前C/D/E均达不到原型建议余量，须先整理空间或另备已规划的NTFS存储；不建议为此改现有分区。

[官方宿主兼容表](https://knowledge.broadcom.com/external/article?legacyId=80807)列出Workstation 25H2支持Windows 11，但不代表本机具体build/配置已验证。[预分配盘和快照说明](https://knowledge.broadcom.com/external/article?legacyId=1008885)说明预分配与额外delta文件的区别。当前25.0.0不可直接视作安全更新齐全：[VMSA-2026-0002](https://support.broadcom.com/web/ecx/support-content-notification/-/external/content/security-advisories/0/36986)给出25H2u1修复版本；至少包含该修复，并再次核对所选受支持版本的更新公告，而不是认定25H2u1已涵盖所有后来公告。

## 用户需要做什么（选择后实施，本轮均未执行）

推荐路线：

1. 确认采用“现有VMware独立Windows客体验证”，并确认可用的Windows客体镜像/授权。无需先升级宿主Pro；不要复用装有个人资料的现有VM。
2. 关闭/备份现有VM后，通过Broadcom官方渠道更新Workstation到适用且安全修复齐全的版本；安装可能需要管理员和重启。更新属于后续明确授权操作，本轮未下载/安装。
3. 指定一处NTFS目录，先准备至少约110GiB空闲做原型，正式双副本预算约200GiB。可自行整理D盘文件腾空间；无需改分区。F盘现有数据不要格式化或转换，不把“可存VMDK”当成已有正式安全方案。
4. 获确认后由Agent准备详细、可复核的VM配置与合成验证脚本，再创建专用客体；首轮只跑固定探针，不接真实用户/模型代码。4 vCPU/6GiB只是起始配置，可依实测调整。

若选择最少代码改动的物理卷路线：提供一块明确可清空的备用设备及其身份，随后另行确认只在该设备创建专用小NTFS卷的方案。不得指向当前F盘、系统恢复分区或普通工作目录；本轮不给可直接误操作的格式化命令。

## 通过条件与接手顺序

先原型验证授权工作盘CreateFile/ReadFile和双编译器；再测试主/子进程大文件、ADS、小文件/元数据耗尽、卷满不影响宿主。继而验证无关文件读写、客体loopback和宿主/外网、子进程、CPU/输出/停止/崩溃回收、磁盘拔出/满/身份变化拒绝、宿主桥接数据来源与重解析点防护。保留原有失败测试和历史证据，新后端按等价测试映射，不能直接删除失败取得绿色。完整S1–S8以及人工签收之前，P5不开放真实执行。

本轮只完成环境与来源评估，不声称VM方案或物理卷方案已通过、也不修改ADR-006/REQ/Plan。用户选择后另开实施记录和必要ADR；选型尚未获批准。
