# P2 知识与模型契约

M02/M03/M07；schemaVersion=1。定义见 Contracts/Knowledge.cs、Application/KnowledgePort.cs、Application/WorkspaceService.cs。

- HostSelection 只能由可信宿主文件/目录选择器创建，模型无导入/写库/Runner工具。对源目录逐层固定句柄，拒绝 reparse point、路径别名和多链接文件；在相同已验证句柄读取有界快照。
- SwitchAsync 使用 expectedRevision 和 requestId；同请求同负载返回原结果，负载冲突拒绝。ImportAsync 为每命令原子提交，各文件失败独立报告；同命令重试不重复版本。成功前取消回滚索引事务。
- SearchAsync 按所选来源优先，再返回本地匹配。空结果明确不足；非空结果不声称足以覆盖学习目标。ResolveAsync 校验整个 SourceRef，不能退回最新版本或当前库。
- 源身份为规范化源路径及 UUID；内容跨来源共享 hash blob，来源/节点独立。节点的版本 ID、hash、定位不变。
- PrepareAsync 对实际解析成功的引用建立不可变来源快照；ModelEgress 核对授权对象、目标、模型、来源种类。修改后的快照不能复用旧授权；运行输出/程序修改文件/凭据禁止作为学习内容外发。
- DeepSeekProvider 仅接受 ApprovedModelRequest；固定 HTTPS、禁止重定向、无自动重试；密钥只用于 Authorization。响应体有界，不回显服务错误体。Usage 未返回时为 null，余额不估算。
- HTTP 401/403→AUTH_INVALID，402→QUOTA_EXHAUSTED，400/404/422→MODEL_INVALID，其他失败→TRANSIENT_PROVIDER_ERROR；空、截断或格式错误→INVALID_MODEL_OUTPUT；用户取消→RUN_CANCELLED。
- 采用短文本 chat/completions 非流式调用用于 P2；不将回复解释成课程、完成记录、文件路径或可执行操作。

实现参考（2026-10-16核对）：[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)、[PdfPig 官方文档](https://github.com/UglyToad/PdfPig)。PdfPig 0.1.13 已锁定；Apache-2.0，包内容及依赖由 NuGet lock 文件记录。
