# TURN-YYYYMMDD-NNN：本轮结果

> 复制本模板到 `docs/changes/YYYY-MM/TURN-YYYYMMDD-NNN.md`（NNN 当日递增），
> 并在 `docs/changes/INDEX.md` 追加一行。`scripts/check-docs.ps1` 会校验
> 标为必填的章节与首部字段；缺失会使 `scripts/verify.ps1` 失败。

- 时间：YYYY-MM-DDTHH:mm:ss+08:00
- 状态：completed / partial / blocked / interrupted
- 阶段与工作包：P? / WP-...
- 关联：REQ-...、M...、ADR-...、相关 TURN-ID
- Git：分支；开始 HEAD 完整 SHA（尚无提交写 unborn）；开始时已有未提交改动
- 用户目标与本轮范围：

## 修改与原因

| 文件/模块 | 修改内容 | 原因/影响 |
|---|---|---|

## 契约与数据影响

接口、Schema、数据库迁移、兼容性、授权或范围变化；没有则明确写无。

## 验证

| 命令/操作及工作目录 | 预期 | 实际结果与退出码 | 证据 |
|---|---|---|---|

未运行/失败/跳过的项、原因和影响须单列，人工验收写 pending/pass/fail。

## 决策与排查

本轮采用方案、关键依据；失败方法及失败原因；相关 ADR 链接。

## 未完成与下一步

按可执行顺序列出剩余事项、阻塞条件和建议先读文件；无则写无。

## 提交与交接

已提交/未提交；未提交原因与文件范围；包含本记录的提交通过 INDEX/Git 查询。
说明恢复本轮工作的必要步骤及风险，不记录密钥或完整私人内容。
