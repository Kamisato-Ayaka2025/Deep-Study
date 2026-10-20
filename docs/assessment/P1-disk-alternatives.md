最新：2026-10-17已完成本机环境约束下的[替代方案评估与用户操作](PSEC-alternatives-20261017.md)，包括已安装VMware的条件性验证建议。下文保留原评估；没有候选通过安全验收。

# P1 磁盘硬限制候选复核（2026-10-16）

2026-10-17补充：管理员重新挂载后已用最小原生探针确认两套工具链均在CreateFile阶段拒绝VHD盘符/GUID路径，普通授权文件与其路径转换均成功。不是再次未挂载，也不是编译器语法问题；精确内部根因尚未确定。见[PSEC卷读取诊断](PSEC-volume-read-20261017.md)，当前仍无已验证替代方案。

结论：尚未找到已在本机验证、无需改变部署条件即可替代固定VHD的方案。保持严格读取、首期禁网和磁盘硬容量门禁；不修改REQ-034，不把定期统计或IO限速解释为容量限制。

| 候选 | 能否满足当前条件 | 下一步 |
|---|---|---|
| 已授权固定2GiB VHD + PSEC | 固定卷提供容量边界，但PSEC双工具链无法读取卷内源码，尚未执行填盘 | 保留复现，优先查明文件映射卷支持/修复；不继续盲目扩大ACL |
| 普通NTFS用户硬配额 | 按文件所有者SID计费，重解析点、安全描述符等元数据不计费；现Runner与宿主用户相同，亦非每次运行配额 | 不作为等价替代，不修改宿主卷配额 |
| 定期目录统计/超限杀进程 | 两次扫描间可任意增加写入，且路径枚举、ADS、元数据存在覆盖问题 | 排除 |
| Job IO rate控制 | IO速率不是容量；微软文档还明确该结构在Windows 10 1607及后续不受支持 | 排除，不增加伪安全配置 |
| Windows Server FSRM目录配额 | 官方适用系统是Windows Server，不是当前Home环境 | 不要求用户安装，也不作为本机可用方案 |
| 独立、预先定容的物理卷 | 容量原理可成立，避免文件映射卷这一变量；PSEC兼容性、设备身份、重挂载和全攻击矩阵仍待验证 | 只是候选；涉及新设备/分区，现有VHD授权不涵盖；不操作现有物理分区 |
| 新的RAM磁盘/文件系统过滤驱动 | 可能提供容量强制边界，但新增内核信任、兼容性、内存成本和管理工作 | 未选型、未安装，不能宣称安全替代；须另行审查与授权 |

本轮Inspect：volumeUniqueId仍为`\\?\Volume{50a1dbcb-e65a-4811-befb-40014dbc8a4b}\`，卷容量2130636800字节，空闲2113871872字节，realCodeEnabled=false。这是挂载与容量检查，不是PSEC填盘行为证明。

## 不降低标准的验证路线

1. 保留当前正式双工具链VHD失败测试及原始日志。上游#1473仍Open，尚无可验证修复；相似现象不能当作完全同因的证明。
2. 后续诊断应先用最小文件打开探针分离“CreateFile拒绝”和“句柄到DOS路径转换失败”，再决定是否值得修复路径表达。#1464是后者，已有句柄可打开的报告，不能直接解释本机前者。不可为绕过路径API扩大盘根读取或取消deny规则。
3. 候选必须在同一限制下完成双工具链编译/运行、单进程与子进程填盘、元数据/小文件/ADS写入、卷满错误、宿主哨兵不变、重启失挂拒绝及停止回收。填盘只能针对已核验的专用卷，不能向普通宿主目录运行。
4. 产品启用还须建立专用卷独占租约、当前用户/镜像/卷身份绑定和容量检查；挂载丢失、身份改变、配置错误都拒绝启动，不落回宿主目录。新宿主文件API拒绝目录挂载点，未来应采用经身份核验的盘符规范路径接入并补回归，不能偷偷允许任意reparse。

## 核对的一手来源

- [Microsoft MXC #1473](https://github.com/microsoft/mxc/issues/1473)：文件映射NTFS卷拒读，Open；2026-10-16已分派，未见修复。
- [Microsoft MXC #1464](https://github.com/microsoft/mxc/issues/1464)：Mount Manager路径转换问题，需与文件打开拒绝区分。
- [Microsoft Disk Quota Limits](https://learn.microsoft.com/en-us/windows/win32/fileio/disk-quota-limits)：所有者计费与元数据例外。
- [Microsoft Job IO rate结构](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/ns-jobapi2-jobobject_io_rate_control_information)：速率语义与支持限制。
- [Microsoft FSRM Quota Management](https://learn.microsoft.com/en-us/windows-server/storage/fsrm/quota-management)：Server适用范围。
