# PSEC 已挂载卷最小读取诊断

日期：2026-10-17；关联TURN-20261017-012、REQ-034；结论：**blocked，未修复**。

用户以管理员身份挂载已登记2GiB VHD；普通宿主Inspect确认卷GUID一致、卷容量2130636800字节，空闲2113835008字节。无需再次Prepare或格式化。原两个RunnerVolumeTests重新失败在编译阶段（MSVC退出2、MinGW退出1），填盘负载未执行。

## 最小探针

`probes/PsecNative/volume-read.c` 只执行CreateFile、ReadFile、GetFinalPathNameByHandle(DOS/NT)并打印数字，不写文件、不填盘、不创建子进程。可信固定源码先在普通合成work中使用现有隔离编译器编译，再由同一PSEC机制运行。

`RunnerVolumeReadTests`验证专用卷GUID及容量之后创建带随机名的合成目录，并通过普通宿主读取盘符路径与GUID路径建立正对照。PSEC分别采用普通work+VHD目录只读、VHD work+普通目录只读；不放行卷根、底层镜像或普通用户目录。第四个文件位于两个授权目录外，用于严格拒读负例。

| 目标 | 普通宿主 | MSVC构建探针，两种work | MinGW构建探针，两种work |
|---|---|---|---|
| 普通授权合成文件 | 已知写入 | open=0/read=0/bytes=1；finalDos=0/finalNt=0 | 相同 |
| VHD授权文件，盘符路径 | 正文一致 | open=5，未进入ReadFile或路径转换 | 相同 |
| 同一VHD文件，卷GUID路径 | 正文一致 | open=5，未进入ReadFile或路径转换 | 相同 |
| 授权目录外合成哨兵 | 原文保持 | open=5 | 相同 |

错误5为访问被拒绝。证据：`artifacts/psec-20261017-012/read-tests.log`及`volume-read.trx`，每个随机证据目录下保留编译日志、两个work模式的结果和策略启动journal。诊断用例保留允许文件应成功的硬断言，所以两个测试报告失败，不能用退出码0或已取得诊断输出算通过。原两个填盘测试断言不变。

## 能确定与不能确定的内容

确定当前失败发生于实际文件打开，而不是打开后的DOS路径转换；与C源语法无关，两个编译器构建出的固定探针结果一致。把路径换成GUID或把VHD目录从work换到只读允许目录不能消除失败。没有证据足以把根因精确归为某个Windows内部组件、DACL或策略编解码缺陷。

2026-10-17在线核对[Microsoft MXC #1473](https://github.com/microsoft/mxc/issues/1473)：相似文件映射NTFS拒读问题仍Open，无可验证修复链接；本机现象一致并不证明完全同因。[#1464](https://github.com/microsoft/mxc/issues/1464)讨论打开后的路径转换问题，本机此次在更早的CreateFile阶段已失败。

## 后续边界

当前已授权部署条件下仍无验证通过的修复。继续开放真实参考解/学习者代码将违反ADR-006与Plan P5，不能以放宽盘根/镜像读取、普通宿主编译、统计目录容量或Mock Judge绕过。P5保持上轮已自检的禁用执行预览与固定契约范围。

下一步需要可测试的PSEC兼容性修复，或用户另行决定替代隔离/容量方案。已有记录中的物理专用卷、新驱动/后端只是未验证候选，现有挂载授权不涵盖这些变更。即使解决当前读取问题，仍须完成S1填盘/生命周期以及S2–S8完整安全集成与人工签收。
