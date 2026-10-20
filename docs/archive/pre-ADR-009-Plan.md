# Study Harness 开发计划与模块契约

> 面向执行开发的 LLM。输入基线：`requirement.md` 的 REQ-001～REQ-037 及“本轮决定与范围边界”。本文规定实施结构，不替代需求。当前仓库只有需求文档；下文模块、命令、测试和目录均为待实现目标，不表示已有实现或已验证。

## 1. 执行规则与交付边界

1. 冲突优先级：用户后续明确决定 > requirement.md 最终结构化 REQ > 本计划 > 原始描述。不得恢复已经废弃的整层屏障、5～15 用例硬限制、联网首期范围等规则。
2. 首期最终交付包括全部 37 条 REQ，分阶段交付不等于删减需求。中间版本应明确缺失功能，不能宣称首期完成。
3. 每次开发只推进一个有界工作包：读取相关 REQ、模块契约和当前进度；实现；执行针对性验证；记录证据与剩余项。禁止只输出代码后声称完成。
4. LLM 负责访谈、拟定计划、生成内容、答疑；确定性程序负责授权、权限、DAG、状态、判分、路径、快照、重试、事务及删除。模型文本不能直接改变完成状态。
5. 本文技术选择是实施默认方案，不是用户新增需求。发现不可实现或需要改变产品范围时，先写明证据、影响和可选方案，不能静默降级。
6. 不增设自然语言课件审核角色、评分或逐课人工放行。Schema、引用有效性校验和实验参考解实跑属于契约校验；本文人工验收指软件开发交付验收。
7. 不实施联网搜索、网页读取、账户、多设备同步、其他服务商、内置 IDE、确认后改计划、课内答疑历史持久化。

## 2. 技术基线与系统结构

默认采用 **C# / .NET LTS + WPF 桌面 UI + SQLite + 独立 Windows Runner 进程**。采用模块化单体，不拆微服务，不开启本地 HTTP 服务。具体 SDK、NuGet 和 Windows 最低版本在 P0/P1 核实官方支持情况后锁定到 `global.json`、包锁文件和 ADR；本计划不虚构已验证的版本兼容性。

- WPF：MVVM；课程使用受控 WebView2 阅读器渲染 Markdown、数学公式和图示，资源随应用打包，不加载远端脚本。
- Application：用例入口，执行权限、事务、幂等和模块编排。UI 不能直接写数据库、读取密钥、调用模型或启动编译器。
- Domain：纯业务对象与规则，无 UI、数据库、HTTP 和进程依赖。
- Infrastructure：SQLite、文件存储、DeepSeek、凭据、PDF 提取、Runner 客户端的适配实现。
- Runner：独立进程执行编译与测试，通过受认证的本机 IPC 接收结构化任务；不得提供任意 shell 接口。
- 测试优先使用确定性 Fake Provider；真实 DeepSeek 联调为另一个验收层，不能以 Fake 代替真实接入。

建议目录：

```text
src/
  StudyHarness.Desktop/                 # WPF、ViewModel、阅读器
  StudyHarness.Contracts/               # DTO、错误码、事件、JSON Schema
  StudyHarness.Domain/                  # 纯业务规则
  StudyHarness.Application/             # 用例、端口、事务编排
    Settings/ Knowledge/ Planning/ Courses/ Generation/
    Learning/ Labs/ Tutoring/ Lifecycle/
  StudyHarness.Infrastructure/          # 端口适配
    Persistence/ KnowledgeStore/ DeepSeek/ Credentials/ Toolchains/
  StudyHarness.Runner/                  # 隔离进程执行及停止
  StudyHarness.ReaderAssets/           # 固定渲染资源
schemas/                               # 模型输出与 IPC JSON Schema
prompts/                               # 按任务版本化的提示模板
fixtures/                              # 资料、图、模型响应、C 实验夹具
tests/                                 # 自动化验证
  Unit/ Integration/ Contracts/ Runner/ DesktopE2E/
scripts/                               # 自检与打包入口
docs/
  adr/                                 # 技术决策与验证证据
  implementation-status.md             # 阶段、工作包、阻塞、下一步
  acceptance/                          # 人工验收步骤
artifacts/acceptance/<phase>/<run-id>/   # 测试、截图、日志摘要，不含密钥
```

实施时 `tests/`、`docs/` 均位于仓库根目录。禁止复制旧项目的学习记录或把旧项目约束当成本项目现状。

## 3. 模块职责与依赖

模块是代码所有权边界，不必每个模块一个程序集。只有所有者能改变所属聚合；其他模块通过端口调用。Application 可协调多个端口，Infrastructure 只能实现端口，不反向决定业务规则。

| 模块 | 负责及拥有的数据 | 对外端口 | 禁止事项 |
|---|---|---|---|
| M01 Desktop/Reader | 导航、课程图、阅读、选择题、实验反馈、设置视图；临时 UI 状态 | 调用 Application；订阅事件 | 不持有密钥，不按 UI 状态自行解锁，不执行模型 HTML/JS |
| M02 Settings/Consent | 模型名、生成方式、资料库登记、外发授权策略及授权记录 | SettingsApi、ConsentApi | 不把密钥放普通配置，不把一次授权扩大到未告知范围 |
| M03 Knowledge | 导入、PDF/MD 提取、树形合并、知识版本、检索、引用解析 | KnowledgeApi | 不改源文件，不随源删除本地知识，不用新版本替换旧引用 |
| M04 Planning | 访谈草稿、默认值、目标、完整计划、用户确认、冻结 | PlanningApi | 不生成正文，不在确认后修改计划 |
| M05 Courses/Policy | 学科目录、DAG、层号、节点资格和课程读取投影 | CourseApi、CoursePolicy | 不以整层完成为条件，不将生成视为完成 |
| M06 Generation | 持久任务队列、逐课生成、有限重试、取消、正文发布 | GenerationApi | 不替换成功正文，不绕过授权，不让刷新重置重试 |
| M07 ModelGateway | DeepSeek 协议、结构化输出、用量、流式响应与取消 | ModelPort | 不直接写业务状态，不接收裸文件路径后任意读盘 |
| M08 Learning | 作答记录、曾答对、阅读位置、永久完成记录 | LearningApi | 不相信客户端传入的 correct/completed 字段 |
| M09 Labs/Snapshots | 实验材料、用户工作区、起点/通过/恢复前快照、连续继承 | LabApi | 答疑不能借此写文件，恢复不能撤销完成 |
| M10 Judge/Runner | 工具链探测、隔离编译运行、测试反馈、停止、参考解执行证据 | JudgeApi、RunnerPort | 不按耗时自动终止，不执行未隔离代码，不累计跨运行通过数 |
| M11 Tutoring | 当前课临时会话、课程解释、只读代码上下文、提示边界 | TutorApi | 不保存会话历史，不访问完整参考解，不修改实验 |
| M12 Persistence/Lifecycle | 事务、迁移、文件操作日志、重启恢复、整学科删除、脱敏日志 | UnitOfWork、Repositories、LifecycleApi | 不替业务模块决定完成，不级联删除共享资料库 |

依赖方向：`Desktop → Application → Domain/Contracts`；`Infrastructure → Application ports`；`Runner → Contracts`。模块交互经 Application 编排，禁止循环依赖。M06 使用 M03/M05/M07；M08 提交完成后通知 M06；M09 使用 M10 验证，M10 不直接修改 M08；M11 只能使用 M03 的引用读取、M09 的受限读取与 M07。

## 4. 领域数据、状态与不变量

### 4.1 核心数据结构

所有 ID 使用稳定 UUID，时间以 UTC 存储、界面本地化；序列化含 `schemaVersion`。路径由宿主解析为资源 ID，不接受模型提供的绝对写入路径。

```ts
type Subject = {
  id: Id; revision: number; lifecycle: 'active'|'deleting';
  planStatus: 'draft'|'confirmed'; planHash?: Sha256;
  goal: string; background: string; weeklyMinutes: number;
  defaults: { field: string; value: unknown; reason: string }[];
  weeks: { index: number; nodeIds: Id[]; estimatedMinutes: number }[];
  sourceRefs: SourceRef[]; confirmedAt?: Timestamp;
};
type CourseNode = {
  id: Id; subjectId: Id; order: number; type: 'theory'|'lab';
  title: string; objectives: string[]; prerequisiteIds: Id[];
  estimatedMinutes: number; layer: number;
  labPlan?: { mode: 'independent'|'continuous'; previousLabId?: Id;
    toolchainKind: 'msvc'|'mingw'; projectSourceRefs: SourceRef[] };
};
type SourceRef = {
  libraryId: Id; nodeId: Id; versionId: Id; contentHash: Sha256;
  locator: { sourceLabel: string; page?: number; heading?: string; block?: string };
};
type Lesson = {
  nodeId: Id; contentHash: Sha256; planHash: Sha256;
  blocks: ContentBlock[]; sourceRefs: SourceRef[];
  privateQuestions?: QuestionWithAnswer[]; labArtifactId?: Id;
};
type NodeState = {
  generation: 'absent'|'queued'|'running'|'failed'|'cancelled'|'ready';
  completedAt?: Timestamp;
  labValidation?: 'pending'|'running'|'failed'|'cancelled'|'passed';
  latestRunId?: Id; readingAnchor?: string;
};
```

`ContentBlock` 是带稳定 ID 的 union：paragraph、heading、code、math、diagram、example、question；拒绝原始脚本、远端可执行资源和任意文件 URL。题目内部结构含题干、至少两个不同选项、唯一正确选项 ID、解析；Reader DTO 在首次答对前不含正确选项及解析。题数为 1～10，最后一个教学内容块为 question（引用列表/导航不算教学块）。

持久实体至少包括：Settings、LibraryRegistry、ConsentGrant、Subject/Plan、CourseNode/Edge、Lesson、GenerationTask、DispatchAttempt、QuestionAttempt、QuestionMastery、Completion、ReadingPosition、LabArtifact、LabWorkspace、Snapshot、JudgeRun/CaseResult、ReferenceValidation、FileOperationJournal、Outbox。临时 TutorSession 不入数据库、遥测、崩溃上下文或普通日志。

### 4.2 权限公式与状态分离

```text
prereqsMet(n) = n 的全部直接前置节点存在 Completion
layer(n) = 前置为空 ? 0 : 1 + max(layer(p))
canGenerate(n) = planConfirmed && prereqsMet(n) && 无成功 Lesson
                && 无活动生成任务 && subject.active
canRead(n) = 存在已发布 Lesson
canAnswer(n) = theory && canRead(n) && prereqsMet(n) && subject.active
canStartLab(n) = lab && canRead(n) && prereqsMet(n)
                && referenceValidation.passed && subject.active
```

无前置时 `prereqsMet=true`。所有操作在服务端重新检查，按钮禁用不是授权。未生成且锁定时仅展示标题、目标；已有正文且锁定时允许只读。正常冻结计划流程未必产生后一种状态，但仍通过合法测试夹具验证该权限规则。

- 理论完成：每个 questionId 都存在曾答对记录；错误重试不清除记录。
- 实验完成：同一 `purpose=user` 的完整运行，非空用例集全部通过，无跳过/中止；完成依据绑定实际执行源码及测试集 hash。
- Completion 只追加一次；除整学科删除外不能撤回。最近失败与历史完成分开显示。
- 确认事务固定计划 hash；生成只接受该 hash。已有成功 Lesson 只读且唯一。
- 首次通过快照、完成记录必须可靠关联；参考解运行绝不写用户完成记录。

### 4.3 资料库语义

资料库各有固定 `libraryId`、绝对根路径登记及独立内容索引。课程始终引用 `libraryId + nodeId + versionId + hash`，不能只存当前库相对路径。

知识树默认按“来源身份 / 文档 / 章节 / 内容块”组织。来源身份由规范化源路径与登记 ID 表示；内容 blob 按 hash 去重；相同内容在不同来源可共用 blob，但保留来源定位。MD 优先标题链和已有块映射；PDF 优先页/段落映射及内容匹配。插入、重排时先匹配旧块，再分配新块 ID；无法可靠匹配时新增并保留旧块，不误覆盖。该规则在 P2 用夹具固化，不承诺语义级自动消歧。

同一稳定位置内容变化创建新版本并更新 currentVersion；旧版本不可变。源删除的块保持本地可检索，记录源缺失标记但不清理。跨来源相同内容合并存储，不丢失多个出处；跨来源同名章节不能直接覆盖。无文字层 PDF 先报告需文本资料，不静默当作成功；OCR 不在首期硬承诺。

## 5. API 契约

### 5.1 统一约定

以下是进程内异步应用接口，不是 HTTP URL。C# 实现采用明确 record DTO、Task 和 CancellationToken；伪代码只规定协议。所有写接口使用 `Command<T>`，查询使用 `Query<T>`。

```ts
type Command<T> = {
  requestId: Id; expectedRevision?: number; payload: T;
};
type Result<T> =
  | { ok: true; value: T; revision?: number }
  | { ok: false; error: { code: ErrorCode; message: string;
      retryable: boolean; details?: SafeDetails; correlationId: Id } };
```

- expectedRevision 在草稿、设置、恢复/删除确认提交等涉及竞争的接口必填；错误时返回 REVISION_CONFLICT。普通作答以不可变 lesson hash 和 requestId 验证，不因其他题并发提交丢失记录。
- 同一 requestId + 同一 payload 返回同一结果，不重复副作用；同 ID 不同 payload 返回 IDEMPOTENCY_CONFLICT。用户再次有意提交使用新 ID，必须新增一次尝试。
- 事件只能在事务提交后通过 Outbox 发布，消费者按 eventId 去重；UI 重连后重新查询状态，不能只依赖内存事件。
- 标准错误码：VALIDATION_ERROR、PLAN_NOT_CONFIRMED、PLAN_FROZEN、PREREQUISITES_UNMET、ALREADY_READY、TASK_ACTIVE、AUTO_PAUSED、REVISION_CONFLICT、IDEMPOTENCY_CONFLICT、CONSENT_REQUIRED、CONSENT_DENIED、SOURCE_UNAVAILABLE、INSUFFICIENT_SOURCES、AUTH_INVALID、MODEL_INVALID、QUOTA_EXHAUSTED、TRANSIENT_PROVIDER_ERROR、INVALID_MODEL_OUTPUT、TOOLCHAIN_MISSING、ISOLATION_UNAVAILABLE、REFERENCE_NOT_VALIDATED、RUN_CANCELLED、SNAPSHOT_UNAVAILABLE、IO_FAILURE、SUBJECT_DELETING。
- retryable 表示允许采取重试动作，不代表自动重试；自动重试仅按 6.2。

### 5.2 设置、授权与资料 API

| 接口与所有者 | 输入 | 输出与副作用 |
|---|---|---|
| M02 `Settings.Get` | 无 | 脱敏设置、hasApiKey、当前 libraryId；不返回密钥 |
| M02 `Settings.Save` | revision、model、可选新密钥、generationMode、consentMode | 保存配置；密钥写凭据存储；改变模式不等于恢复暂停 |
| M02 `Settings.SwitchLibrary` | revision、selectedDirectoryToken | 验证目标路径，登记/切换库；不迁移，不重写旧引用 |
| M02 `Consent.Prepare` | purpose、provider、待发送各项 hash/类型/字节数 | disclosureId、展示范围、是否已获授权 |
| M02 `Consent.Decide` | disclosureId、allow、once/each | 对应范围 grantId 或拒绝；不能复用于已变更 payload |
| M03 `Knowledge.Import` | 只读选择令牌、libraryId | importJobId；逐文件结果、提取/合并统计和错误 |
| M03 `Knowledge.Search` | topic、用户来源 ID、libraryId、limit | 优先用户指定资料、其次本地库的版本化片段；不足标记 |
| M03 `Knowledge.Resolve` | SourceRef | 对应版本正文和定位，或带原引用的不可访问结果 |

目录选择令牌由宿主文件选择器建立，绑定操作与根路径。导入枚举不能跟随连接点越出选择范围。读取权限与模型外发授权独立；拒绝外发不应删除已经提取的本地知识。计划、生成、答疑请求均由 Gateway 在发送前核对 payload hash 与 grant。

### 5.3 计划、课程和生成 API

| 接口与所有者 | 输入 | 输出与约束 |
|---|---|---|
| M04 `Planning.Start` | 学习请求 | draftId、已知信息、缺项；保存明确标识的默认值 |
| M04 `Planning.Revise` | draftId、revision、用户补充/修正 | 新草稿；失效旧待确认计划 |
| M04 `Planning.Propose` | draftId、revision、授权令牌 | 完整计划、来源、目标、时间、实验素材和不可修改提示；无正文 |
| M04 `Planning.Confirm` | draftId、revision、planHash、ackFrozen | 原子确认并建立冻结学科；旧 hash 或未提示不能确认 |
| M05 `Courses.List/GetGraph` | 可选 subjectId | 学科列表/图和状态投影 |
| M05 `Courses.GetLesson` | nodeId | Reader DTO、权限及不能操作的原因；锁定无正文仅标题目标 |
| M06 `Generation.Request` | nodeId、trigger=click/manualRetry/regenerate | taskId 或现有活动任务；已有正文仅 regenerate 可替换；旧版存历史 |
| M06 `Generation.Cancel` | taskId | 取消任务；不自动恢复；重生成取消保留旧正文 |
| M06 `Generation.GenerateAsync` | subjectId、nodeKey、regenerate | 点击即授权本课固定范围并创建/续接唯一任务 |
| M08 `Learning.HistoryAsync` | subjectId、nodeKey | 旧版只读 Reader，保留对应作答与掌握记录 |
| M06 `Generation.GetTask` | taskId | 状态、已派发次数、失败类别、实际用量 |
| M07 `ModelPort.Invoke` | purpose、promptVersion、schemaId、context片段、grantId、taskId | 结构化候选结果、实际 usage；无业务写权限 |

模型任务 purpose 固定为 interview/plan/theory/lab/tutor。计划和课件输出 Schema 校验在宿主执行；模型输出中的 status、完成声明、路径和工具调用不具备权限。内容引用必须是本次实际读取且传给模型的来源集合子集。

### 5.4 学习、实验与答疑 API

| 接口与所有者 | 输入 | 输出与约束 |
|---|---|---|
| M08 `Learning.SaveReadingPosition` | nodeId、lessonHash、blockId、offset | 保存锚点，非法锚点拒绝；不增加完成 |
| M08 `Learning.SubmitAnswer` | nodeId、lessonHash、questionId、optionId、requestId | attemptId、correct、everCorrect、答对后解析、completed；答案仅宿主判定 |
| M09 `Labs.GetEnvironment/SelectToolchain` | nodeId、探测或选择的 toolchainId | 路径、版本、可用性；课件发布前固定本课环境指纹 |
| M09 `Labs.ValidateReference` | nodeId、artifactHash、toolchainId | validationTaskId；通过前不可开始用户实验 |
| M09 `Labs.Start` | nodeId、requestId | workspaceId、用户目录、本课起点 snapshotId；重复调用不重新继承 |
| M09 `Labs.ReadForTutor` | workspaceId、相对文件选择 | 受限文本片段和 hash；拒绝越界、二进制、参考解路径 |
| M10 `Judge.Start` | workspaceId、purpose=user、requestId | runId；绑定不可变源码快照、测试集、工具链指纹 |
| M10 `Judge.GetRun/Stop` | runId | 用例状态/停止结果；停止后不授予首次完成 |
| M09 `Labs.PrepareRestore` | workspaceId、target=start/firstPass | 一次性 confirmationId、变更清单、当前目录指纹 |
| M09 `Labs.ConfirmRestore` | confirmationId、revision | 先备份，再恢复；文件已变则重新预览确认 |
| M11 `Tutor.Open/Ask/Close` | nodeId / sessionId、问题、授权令牌 / sessionId | 临时流式回复；切课关闭清空，旧回复不进入新课 |
| M12 `Subjects.PrepareDelete` | subjectId | 删除清单、一次性 confirmationId、revision |
| M12 `Subjects.ConfirmDelete` | confirmationId、revision | 删除作业 ID；停止相关任务并清理专属数据 |
```
JudgeRun = { runId, purpose: user|reference, sourceHash, testSuiteHash,
  toolchainFingerprint, state: preparing|compiling|running|passed|failed|cancelled,
  cases: [{ id, input, expected, actual?, stdoutTruncated,
    state: pending|passed|failed|notRun|cancelled, reason? }], passedCount, total }
```

参考解验证使用内部端口，UI 不允许伪造 `purpose=reference` 或将其结果转为 user。Judge 结果必须来自实际 Runner，并验证 runId/hash/完整用例集；模型不能提交 JudgeRun。

### 5.5 Runner IPC

宿主经受限命名管道发送 `StartRun {protocolVersion, runId, sandboxProfileId, sourceBundleId, toolchainId, testSuiteId, purpose}` 或 `StopRun {runId}`。只接受宿主登记的资源 ID，不接受命令字符串、任意路径或任意环境变量。应答为 Accepted、CompileResult、CaseResult、RunFinished；每条含 runId 和递增 sequence。断连、非法消息、重复完成及乱序均不能制造通过。

宿主与 Runner 用当前会话的随机握手秘密和管道 ACL 验证通信；秘密不进入用户代码可读目录。宿主拥有进程作业生命周期；失联、退出或终止时销毁所属编译/运行进程树。编译器本身也处于文件与通信边界内。

### 5.6 领域事件

`PlanConfirmed(subjectId,planHash)`、`LessonReady(nodeId,hash)`、`NodeCompleted(nodeId,evidenceId)`、`GenerationChanged(taskId,status)`、`LibrarySwitched(oldId,newId)`、`JudgeFinished(runId,purpose)`、`SubjectDeleted(subjectId)`。事件均带 eventId、aggregateRevision、occurredAt。完成事件更新解锁资格，不派发下一课程；重复投递不能重复生成。切库事件不改旧 Lesson。

## 6. 关键流程与失败语义

### 6.1 建课到理论完成

`访谈 → 检索本地资料 → 外发范围授权 → 完整计划候选 → DAG/字段校验 → 用户确认冻结 → 用户进入课程点击生成 → 生成候选 → Schema/引用校验 → 原子发布 → 阅读/答题 → 确定性累计判分 → 完成事务 → Outbox → 下一节点解锁展示`。

资料不足在计划或生成时返回缺口，允许补充本地资料，但不能暗中修改已冻结目标或计划。确认前允许反复改草稿，确认后的正文失败可以重试，成功理论正文可显式重新生成；新正文校验成功后同事务保存旧版只读历史、切换当前版并清空新题掌握/阅读位置，保留完成与解锁及旧作答。内容 hash 相同时沿用当前版本与掌握记录。

### 6.2 生成调度、取消与重启

- 默认全局正文生成并发数 1，显式任务按创建时间排队；层仅用于展示。单节点活动任务有数据库唯一约束。
- 生产入口仅按需点击，无模式选择、无完成后自动扫描。启动切至 Click，取消旧版未开始的自动任务；兼容旧自动模式代码仅用于迁移回归。
- 一个 taskId 最多 3 次模型派发：初次 + 最多 2 次临时网络/服务错误重试。派发前持久增加 attempt 序号；重启不能将其归零。认证、模型、额度错误立即失败。
- 退避建议 1s/3s，服务端 Retry-After 可覆盖等待值；取消可中断等待。不对业务无效/Schema 无效输出暗中追加修复请求；显示失败，用户手动重试建立新任务。
- 点击生成即授权当前节点固定外发范围，不要求预览。授权不跨重启恢复；排队任务重启后需再次点击续接原任务，不重置派发次数。
- 取消与成功同时到达时使用任务 revision 原子竞争；取消已提交后，迟到响应不发布。成功已提交则保留正文，取消不能删除成功结果。
- 程序崩溃后 running 任务标记 interrupted/failed 并解释可能已计费，默认等待用户继续；不假定外部请求具备 exactly-once。已派发次数保留，已有成功正文仅显式重新生成时请求。
- usage 只保存实际返回字段；余额未返回则不展示余额。日志不记录原始鉴权头、完整 prompts、答疑内容或完整响应。

### 6.3 实验生成、连续继承与判分

1. 模型输出任务说明、TODO 初始材料、内部完整参考解、非空且不超过 20 的用例，以及连续项目的文件变更清单；先存私有草稿。
2. 工具链探测成功且 P1 基础验收及 PSEC 真实代码开放门禁均通过后，在私有实验验证目录运行参考解。P1 单独通过不得开放参考解或用户代码。通过证据绑定材料、测试、工具链 hash；任一改变则证据失效。未通过只能展示待验证/失败，不可开始。
3. 首次进入用户实验时：独立实验从模板建立工作区；连续实验先稳定复制上一堂的**当前**代码，再添加本课新文件/明确 TODO 区块，保存本课起点。下一堂不会采用首次通过快照。
4. 连续项目生成的补丁限定新增文件或具稳定锚点的本课 TODO 插入，不得覆盖此前用户实现。锚点消失/内容冲突时返回可解释冲突，请用户在外部编辑器整理；不能替用户重置前课代码。下一课的参考解在标准完整前课基础验证，用户继承代码可能已损坏，需如实反馈。
5. 复制前后核对目录清单和文件 hash；外部编辑并发导致变化则重试有限次数并请用户暂停编辑，不以半份代码初始化。起点成功后不可覆盖。
6. 每次用户检验先创建稳定不可变运行源码快照，在该快照的隔离副本编译执行，编辑器继续修改不影响本次判分。首次全通过快照来自本次执行源码，不能临时读取已经改变的当前目录。
7. 首次全通过时先可靠保存通过快照，再提交完成/证据关联事务；写盘失败显示保存失败，不伪造持久完成。重启通过操作日志补齐或重试提交，不能把另一次代码绑定成通过版本。
8. 输入/输出测试首期采用标准输入与标准输出；对比仅统一 CRLF/LF，其余差异默认严格比较。编译诊断与 stderr 单列，不冒充 actual。输出可截断展示但必须完整流式比较，不能凭截断前缀判通过；超出配置的资源配额时报告资源失败，不报告通过。用例串行，无自动时长上限，卡住时用户手动停止。

### 6.4 文件隔离是提前验证的硬门禁

REQ-034 要求系统强制阻止越界，不可仅做工作目录设置、模型提示、路径检查或进程树终止。P1 优先验证 PSEC，支持双工具链、必要依赖只读及严格无关文件拒绝；AAP/ARAP 不豁免。按2026-10-15用户决定，首期实验禁网，包括自身 loopback、宿主服务及外部网络。缺失所需隔离能力时拒绝启动，不回退宽松后端；支持的系统范围由能力探测和实测结果确定。

- 工具链环境发现可在可信宿主进行，收集经确认的安装路径/环境；不能执行模型生成的 shell/build 脚本。实际编译和执行都进入隔离环境，TEMP/TMP、产物和运行日志均在专属实验目录。
- 不给整个用户目录、整块磁盘或整个依赖目录泛化读权限；依据探测结果建立必要依赖白名单。处理符号链接、junction、路径穿越和大小写/短路径别名。
- 本机网络测试覆盖 IPv4 TCP/UDP、DNS、loopback、宿主服务和子进程；外部目标须有宿主正对照。本机不支持 IPv6，按用户决定不继续探索或测试 IPv6，验收报告标为明确排除/未验证，不能伪造通过；产品禁网策略不得主动放行该协议。
- 开放真实代码执行前的 PSEC 强制门禁：进程数、CPU、输出与磁盘硬容量限制；产物读取/复制/回写/清理的最终路径及重解析点防护（包括并发替换）；模型请求来源及独立外发授权；完整文件/通信攻击及生命周期矩阵。不得仅凭输出长度、work 路径或模型自述认定安全。资源超限返回失败；不添加正常运行的自动时长上限。宿主通过输入输出句柄及退出码判定，不能采信程序自写成绩单。
- 按2026-10-16用户决定，进程/作业内存限制列为保留项 R-MEM-01，成品后再决定是否要求，不属于 P1/PSEC/首期成品通过的必要条件。已有防护和回归暂留，不为本决定主动拆除；不新增内存专项工作，不据此承诺完整抗内存耗尽能力。潜在影响为内存压力、系统响应下降及其他应用失败。
- 进程树管理与权限隔离是两个不同问题；不将 Job Object 本身当成完整安全沙箱。
- 若需要管理员安装组件，需提供有明确范围的安装说明和失败状态；不能在普通运行中悄悄提升权限。
- 分别维护 P1 基础阶段与 PSEC 真实代码开放状态。基础能力不满足则 P1 blocked；磁盘硬限制或其他完整安全项不满足则 PSEC blocked，即使 P1 已验收也不得开放真实代码。保留失败测试/证据，不得将普通进程执行作为兜底。理论模块和禁用执行入口的产品界面可继续开发。

### 6.5 恢复与删除

恢复：Prepare 返回目标、将覆盖/删除的文件、目录指纹；用户二次确认后检查指纹未变，停止该工作区运行，先完整备份并校验，再以 staging/journal 切换工作区。新增作业文件按目标快照清除，快照保存在工作区之外。失败保留备份和操作日志，提供可恢复状态，不先毁掉唯一副本。

删除：Prepare 列出课程、正文、进度、用户代码、起点、通过快照、恢复前备份及内部参考解/运行产物；Confirm 后置 lifecycle=deleting，阻止新任务，取消生成/答疑并停止实验，逐项清理专属目录和数据库记录。删除日志放全局控制库中，失败记录剩余路径，重启继续；只有全部清理后显示删除成功。共享知识、其他学科、用户源目录不在清单中。

所有目录由内部 subjectId/workspaceId 建立。递归写/删除前验证最终解析路径确在专属根内且不通过重解析点逃逸；不能将模型文本或数据库里未经校验的任意绝对路径直接递归删除。

## 7. 存储、隐私与故障恢复

```text
%LOCALAPPDATA%/StudyHarness/
  app.db                         # 设置、计划、记录、任务、库登记、日志
  subjects/<id>/
    lessons/                     # 已发布不可变正文
    labs/<nodeId>/work/           # 用户外部编辑的唯一工作目录
    labs/<nodeId>/snapshots/      # start、first-pass、pre-restore
    labs/<nodeId>/internal/       # 参考解/测试材料，答疑不可访问
    labs/<nodeId>/runs/           # 稳定执行快照与运行反馈
  operations/                    # staging、恢复/删除 journal
用户选定资料库路径/
  library.json                   # libraryId 与 schemaVersion
  index.db                       # 来源树、版本、当前指针
  blobs/<hash>                   # 不可变提取内容，软件不自动清理
```

- 密钥使用 Windows 凭据存储或用户绑定的系统加密服务；具体适配在 P0 决定并测试。数据库仅留凭据引用。
- 数据库事务与文件系统不能假装同一事务；使用“临时写入 → 校验/hash → 原子替换 → DB 提交 → journal 完成”的明确状态机；每种断点有恢复测试。SQLite 外键、唯一约束、事务控制重复完成/正文/起点。
- 迁移前备份 DB；迁移失败不启动写操作，展示可恢复错误。测试空库和至少一个旧版本 fixture。
- 资料库路径切换只更新 currentLibraryId；遇旧库离线显示原出处和错误。新增导入在开始时绑定目标 libraryId，切库不将正在导入的一半内容写到另一库。
- 普通日志只留操作 ID、耗时、状态、错误类别和非敏感 hash；脱敏测试同时覆盖错误信息、模型回显、诊断与导出。不得把 API key 混入 prompt。
- 渲染器只接受受控 DTO；点击引用经宿主安全路由，关闭远程脚本和任意本地文件访问。题目提交消息绑定当前 nodeId/lessonHash，不接受页面传入正确答案。
- 本地课程内容、模型返回和源资料中的指令均是不可信数据；不能扩大权限。Tutor 无写工具且不载入完整参考解；用提示模板及对抗用例约束完整答案输出，不声称仅靠提示即可数学上保证所有回复无泄露。

## 8. 分阶段实施与验收门禁

所有阶段验收分为 A（LLM 可执行自检）与 H（人工验收）。A 必须先完成；H 未实际执行只能标记 pending，不能由 LLM 代签。无需在每个小工作包等待人工，但进入首期发布前所有阶段 H 必须有结果。P1 隔离失败会阻塞实验相关发布，不阻止无依赖模块开发。

### P0：工程骨架、契约及可运行空壳

依赖：无。模块：M01/M02/M12、Contracts/Domain。

交付：解决方案、固定 SDK/依赖、空主对话/课程/设置、基础 DTO/Schema/错误码、SQLite 迁移、Fake Provider、脱敏日志、自检脚本、ADR、状态文档。设置页仅真实实现已有项，不能伪造已连接模型。

A：Windows 干净工作区 restore/build；单元与契约测试；空库启动与重启；无账号可进入；错误码可被 UI 展示；API key 未落普通配置；提供一条命令执行本阶段检查。

H：安装/启动候选包，点击三个入口，空状态可理解；关闭再开设置仍在；不需要登录。

完成条件：可运行壳 + 测试证据，不能称为学习闭环。对应 REQ-001/002/003/004/032 的基础部分。

### P1：固定夹具下的 Windows 隔离与双工具链基础验收

依赖：P0。模块：M10、最小 M09。

交付：Runner 原型、工具链探测、IPC、停止与退出清理、隔离 ADR、恶意测试夹具。先使用固定 C 程序，不依赖模型生成质量。

A：分别 MSVC、MinGW 在隔离内编译登记的固定 C 夹具；必要依赖读取和副本内写入成功；现有普通/AAP/ARAP无关哨兵、越界写入、junction、原profile、控制管道及本机IPv4/DNS/子进程网络负例保持硬断言。停止、断连、宿主异常退出回收整树；观察窗口不是产品超时。验证当前宿主文件入口、模型外发应用门禁、进程数/CPU/输出限制和未登记资源拒绝。运行 scripts/verify.ps1 -Phase P1，所选测试必须全部执行、零失败/零跳过，发布/UI/打包通过。IPv6明确排除且未验证。磁盘及完整攻击/真实业务集成属于独立PSEC门禁，不能声称本阶段覆盖。

H：在实际用户 Windows 机器配置两种工具链并运行示例；无限循环点击停止；关闭软件确认无遗留进程；核对安装权限与必要依赖说明。

完成条件：上述基础A通过后交用户H验收，H未签为pending；通过仅表示REQ-034的固定夹具基础子集，不表示真实实验安全开放。对应REQ-022/023/032/034/037执行基础。PSEC已知失败不阻止该有限阶段验收，但必须在报告中单独显示blocked。

### PSEC：真实代码开放前的独立安全门禁

依赖：P1基础及相关宿主/模型业务实现；可以与P2–P4并行，必须在P5首次真实用户代码或模型参考解执行前完成。由M10负责隔离/资源、M09负责文件生命周期、M02/M03负责来源与外发，UI不得自行开启执行。

必过清单与状态见 docs/acceptance/PSEC.md：磁盘硬容量及失挂拒绝、完整读取/写入/通信攻击矩阵、子进程继承与退出清理、实际宿主导入/复制/回写/删除竞争、来源授权与实际模型发送体、PSEC策略编码核验及干净依赖基线。内存限制R-MEM-01除外，不将其余未完成项一并延期。IPv6仍按用户要求明确排除。

scripts/verify.ps1 -Phase PSEC 保留全部测试（包括原失败卷测试）；未完成业务核验时readiness明确blocked。P1报告必须注明outOfScope与PSEC状态，不能把过滤到另一阶段的测试叫作已通过或删除原始失败记录。测试框架Skip仍禁止。人工核对完整证据后才可批准真实执行；没有配置开关能用P1绿色代替PSEC。

### P2：本地资料库与真实 DeepSeek 接入

依赖：P0，可与不依赖它的 P1 工作交替推进。模块：M02/M03/M07。

交付：PDF/MD/目录导入、树与版本合并、源定位、切库、授权预览、DeepSeek adapter、模型设置与真实用量。

A：原始文件导入前后 hash 相同；重复导入/插入/修改/删除段落夹具验证版本语义；A→B 切库后旧引用仍读 A，A 离线时正确报错；失败文件不影响成功文件结果；模拟鉴权/模型/额度/临时错误；两种授权与拒绝均断言实际 payload；日志扫描无测试密钥。真实 API 用测试者提供凭据执行一次最小请求，记录脱敏成功证据，不在文档中写密钥。

H：导入含两类文件的目录，查看来源；切库后查看旧引用；选择逐次授权并拒绝一次；配置真实模型看到回复；无余额时不显示虚构数字。

完成条件：本地资料与真实模型适配可用，不能宣称已有完整课程。对应 REQ-003/004/006/007/008/033/035。

### P3：访谈、计划确认与 DAG 展示

依赖：P2。模块：M04/M05/M01。

交付：访谈、默认值、目标与时间表、范围限制、实验素材优先级、完整目录、确认冻结、图与按钮导航。

A：完整请求不重复问已知项；宽泛请求显示默认值及可修正项；2h/8h 周计划均含单课时长且安排不同；零时间阻止确认；环、自依赖、缺节点、非法课型被拒绝；纯数学无实验、纯概念计算机可无实验、C 实践有实验；范围外实践明确限制；确认前零正文；旧 revision/hash 无法确认；确认后所有计划写入拒绝。用 A/B→C/D→E 图验证层号和逐节点资格。

H：从学习请求走到完整待确认计划，修改一项后重新查看；看到不可修改提示；确认后无法改目标、时间、图及实验方案；两个学科可分别浏览。

完成条件：真实模型能产出可确认计划，确定性契约控制冻结。对应 REQ-005/009/010/011/012/013/014/015/020/021/036。

### P4：理论学习纵向闭环

依赖：P3。模块：M06/M08/M01，接入 M07。

交付：按需一键生成、显式重生成与历史版本、线性阅读/数学/代码/图示、1～10 单选、解析、累计完成、阅读位置、重试取消与续学。

A：T01～T08（见第 9 节）全部通过；1/10 题接受，0/11 题拒绝，末尾无题拒绝；首次答对前 Reader DTO 不含答案解析；两题分次正确后完成，后续错误不撤回；重启保留每次作答及位置；渲染脚本注入被阻止；Fake 闭环和真实 DeepSeek 理论闭环分别记录。

H：学习一堂数学或计算机理论课，查看例子/公式或代码；先答错后答对看到解析；跳过阅读直接测验仍需答对所有题；完成 A 后 B 未完成也可生成只依赖 A 的 C；重新生成保留完成和历史，新题独立；取消/失败保留旧正文；重启不自动请求。

完成条件：可独立交付“理论阶段预览版”，显式标明实验未交付。对应 REQ-009/013/015/016/017/018/019/027/029/030/031/033。

### P5：实验生成、Judge、连续项目和恢复

依赖：P1基础通过 + P3/P4；开放任何真实执行额外依赖PSEC自动和人工门禁通过。未满足时仅开发禁用执行入口的界面/固定夹具。模块：M09/M10/M08/M06/M01。

交付：独立 C 实验与 4～8 堂连续实验夹具、内部参考解实跑、TODO、外部编辑路径、一键测试反馈、两类快照、恢复确认及备份。

A：T09～T13；两种工具链分别进行真实运行；0/21 用例拒绝，1/20 接受；错误参考解不可开始；内部通过不增加进度；部分通过/跨次累计/中止不可完成；一次全通过固定首次快照；修改失败与恢复仍保持完成；继承当前代码、TODO 保留、起点固定；修改/删除/新增文件后恢复等同目标快照，恢复前备份完整；并发编辑不会污染通过快照；停止/退出重复验证。

H：用自选外部编辑器填 TODO，查看每例输入/预期/实际输出；制造编译错误后看到真实诊断；通过后改坏，再进入下一堂验证继承改动；分别恢复本课起点和首次通过版本，核对进度保留及确认提示。

完成条件：实验基于真实执行证据交付；不可用 Mock Judge 替代。对应 REQ-012/020/021/022/023/026/028/032/034/037。

### P6：临时答疑与生命周期整合

依赖：P4/P5。模块：M11/M12，并完善 M02/M01。

交付：两类课程侧边答疑、授权范围、只读代码上下文、会话销毁、整学科删除、失败恢复。

A：T14～T16；提示注入及“直接改好/给完整答案”回归；文件 hash 不变，工具接口不存在写权限，参考解不进上下文；切课/重启后无历史恢复，日志/DB 无聊天内容；删除前取消不改变数据，确认后清理全部专属数据；共享库/其他学科/源文件 hash 不变；删除中注入进程退出、IO 错误，重启能恢复清理。

H：理论及实验各问两轮；切课回来无历史；答疑期间代码不变；取消一次删除，再确认删除测试学科，检查其代码快照清理而另一学科与资料可用。

完成条件：隐私、权限和生命周期均有正反例证据。对应 REQ-004/024/025/029/036。

### P7：首期发布候选与完整验收

依赖：P0～P6 自检通过。

交付：可安装 Windows 发布包、安装/卸载说明、工具链配置文档、版本信息、已知限制、全部追踪矩阵与验收报告。卸载不默认删除用户资料和学习数据；数据清理使用既定学科删除流程。

A：干净用户环境安装与启动；完整 Mock 回归、真实 DeepSeek 最小闭环、双工具链实验闭环、重启/权限不足/磁盘写失败/迁移失败；检查 REQ 覆盖和证据链接；无未解释失败、跳过和安全门禁缺口。

H：按完整路径“设置→导入→访谈→确认→理论→实验→答疑→续学→恢复→删除”验收；分别核实 MSVC/MinGW 与真实 DeepSeek；人工检查样课例子、数学条件、代码和解析的一致性。这是发布抽查，不成为每课审核流程。

完成条件：37 条需求均有 A 证据及 H 结果，阻塞缺陷为零；不把未运行项目标作通过。

## 9. 必备验收场景索引

每例均保存输入、执行方式、期望、实际、证据路径。优先测试可观察行为，不写仅复述实现的测试。

| ID | 场景与关键断言 | 阶段 |
|---|---|---|
| T01 | A、B 起点，C←A，D←B，E←C+B；层 0/0/1/1/2；完成 A 即有 C 资格 | P3/P4 |
| T02 | 确认前正文请求拒绝；确认后改计划拒绝；已有正文重新打开零模型请求 | P3/P4 |
| T03 | 锁定已有正文可读但提交/实验拒绝；锁定无正文仅标题目标且零生成 | P4 |
| T04 | 同节点并发点击仅一任务；同一答题 requestId 不重复记录，新 ID 记录新尝试 | P4 |
| T05 | 临时错误最多 3 次派发；刷新/重启不重置；永久错误只派发一次 | P4 |
| T06 | 取消与响应竞争不发布取消后迟到正文；重生成取消保留原文；重启不自动派发 | P4 |
| T07 | 两题分次答对完成；先错无解析；完成后再错不回退 | P4 |
| T08 | 写入/发布中断后恢复不产生半正文；阅读锚点、作答、完成与生成状态分离 | P4 |
| T09 | 参考解失败/缺环境/取消均不可开始；参考解通过不授予用户完成 | P5 |
| T10 | 两次各过一半不完成；一次全过完成；未执行、中止、空用例不完成 | P5 |
| T11 | 第一次全过快照固定；之后失败/恢复保持完成；运行中外部编辑不改变证据源码 | P5 |
| T12 | 前课通过后标记改动，后课继承当前标记；起点保留 TODO；前课再改不改后课起点 | P5 |
| T13 | 恢复先备份，删除目标中无的新文件；取消无变化；备份失败不得先覆盖 | P5 |
| T14 | 答疑切课销毁，重启无历史；读取代码后 hash 不变；不载入参考解 | P6 |
| T15 | 删除取消无变化；确认仅清理目标学科；断点重启完成剩余清理 | P6 |
| T16 | 外发两种模式、拒绝、payload 变化重新确认；密钥/聊天不落日志 | P2/P6 |
| T17 | 重复/增改删源文档、旧引用固定、切库/离线库全部符合版本语义 | P2 |
| T18 | 两工具链隔离、文件/网络攻击、无限循环手动停止、退出清理 | P1/P5/P7 |

## 10. 需求追踪矩阵

“阶段”指完成该需求相关工作的阶段集合；最终仍需 P7 集成验收。

| REQ | 主要模块 | 阶段 | 主要验收依据 |
|---|---|---|---|
| 001 | M01/M12 | P0/P7 | Windows 独立安装，无登录 |
| 002 | M01 | P0/P4 | 主对话/课程/设置和空状态 |
| 003 | M02/M07 | P0/P2 | 保存配置、模型切换、真实请求与错误 |
| 004 | M02/M07/M12 | P2/P6 | T16 |
| 005 | M04 | P3 | 默认值、确认前无正文、冻结提示 |
| 006 | M03 | P2 | 只读导入与不足反馈 |
| 007 | M03 | P2 | 重启复用与预置知识导入 |
| 008 | M02/M03 | P2 | T17 |
| 009 | M04/M08 | P3/P4 | 自述适配，无诊断，跳读仍测 |
| 010 | M04 | P3 | 2h/8h、零时间、冻结周计划 |
| 011 | M04 | P3 | 目标具体化、修正与确认 |
| 012 | M03/M04/M09 | P3/P5 | 项目优先级及确认 |
| 013 | M01/M05 | P3/P4 | 两学科导航和状态 |
| 014 | M04/M05 | P3 | 图校验、T01/T02 |
| 015 | M05/M08/M09 | P4/P5 | T03 |
| 016 | M06 | P4 | T01/T02/T04 |
| 017 | M06/M01 | P4 | 提示模板与线性正文，无审核门槛 |
| 018 | M06/M01 | P4 | 样课例子及适用图文代码 |
| 019 | M06/M08 | P4 | 题数/末题/唯一答案、T07 |
| 020 | M04/M09 | P3/P5 | 三类主题及首期边界 |
| 021 | M04/M09 | P3/P5 | 两类实验、T12 |
| 022 | M09/M10 | P1/P5 | 双工具链、TODO、外部编辑 |
| 023 | M10 | P5 | 1/20 与 0/21 用例，完整反馈 |
| 024 | M11 | P6 | T14 |
| 025 | M11/M09 | P6 | 只读工具及答案边界回归 |
| 026 | M09 | P5 | T11/T13 |
| 027 | M08 | P4 | T07 |
| 028 | M08/M10 | P5 | T09/T10/T11 |
| 029 | M08/M12 | P4/P6 | T08/T14/T15 |
| 030 | M05/M06 | P4 | T01/T04/T06 |
| 031 | M06/M07 | P4 | T05/T06、实际用量 |
| 032 | M01/M07/M10 | P1/P2/P5/P7 | 平台、服务、双工具链范围 |
| 033 | M03/M06/M09 | P2/P4/P5 | 引用子集、内容生成约束、T09/T17 |
| 034 | M10 | P1/P5/P7 | T18 硬门禁 |
| 035 | M03 | P2 | T17 |
| 036 | M04/M12 | P3/P6 | T02/T15 |
| 037 | M09/M10 | P5 | T09 |

## 11. LLM 开发与证据交付协议

### 11.1 工作包模板

每个工作包在 `docs/implementation-status.md` 登记：

```text
WP-ID / 所属阶段 / 状态(pending|in_progress|blocked|self_checked|accepted)
目标及关联 REQ：
允许修改的模块/文件：
前置工作包及输入契约版本：
API/Schema 变化与迁移方式：
实现清单：
自检命令、夹具、预期结果：
实际结果与证据路径：
人工验收步骤与结果(pending/pass/fail)：
已知限制、阻塞原因、下一个工作包：
```

先契约后适配再 UI；修复接口时同步更新 DTO、Schema、调用方、测试与迁移。不在当前工作包中顺手增加联网/IDE 等范围外功能。无需默认多 Agent；若以后并行开发，各工作包须限定写入模块，共享 Contracts 变更先协调。

### 11.2 自检入口目标

P0 创建下列脚本，之后各阶段补齐；这是约定命令，当前尚不可执行：

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
./scripts/verify.ps1 -Phase P4 -Provider Fake
./scripts/verify.ps1 -Phase P5 -Toolchain MSVC
./scripts/verify.ps1 -Phase P5 -Toolchain MinGW
./scripts/verify.ps1 -Phase P7 -Provider DeepSeek
```

首次生成锁文件使用正常 restore，再用 locked-mode 验证。verify 必须汇总子命令退出码，失败返回非零；必要环境缺失标记 blocked/skip 并使对应门禁不通过，不能把零执行用例当成功。真实 API 从凭据存储取值，不把 key 放命令行或报告。

每次报告至少输出 `summary.json`：phase、commitOrTreeHash、environment、startedAt、checks[{id,status,evidence}]、failed、skipped、manualPending。UI 自检应启动实际桌面应用，验证导航、图节点、阅读、答题、状态及错误信息，并保留截图/自动化日志；截图不能替代状态/持久化断言，编译通过也不能替代 UI 自检。

### 11.3 Definition of Done

一个工作包完成必须满足：实现与契约一致；相关 REQ 有正向和必要异常测试；没有伪造模型、Judge 或人工证据；状态文档可让另一 LLM 继续工作；未实现项明确列出。人工验收未执行可以交付待验收包，但不能将其状态记为 accepted。

首期发布完成还要求：P1基础及PSEC真实代码开放门禁通过（不含保留项R-MEM-01）；真实 DeepSeek 与 MSVC/MinGW 实跑通过；完整 37 条追踪矩阵有证据；所有必要人工验收完成；文档明确环境、支持范围和已知限制。仅理论预览版可保持真实执行关闭并明确缺失功能；不得称完整实验成品。不以“代码已写完”替代产品验收。

## 12. Git 使用标准

本节是开发过程约束，不新增产品业务需求。P0 建立并执行；后续所有开发轮次必须遵守。当前编写计划不等于已初始化 Git、创建提交或配置远端。

### 12.1 仓库与分支

1. 开始工作前读取仓库约定，并执行 `git status --short --branch`、`git diff`、`git diff --cached`、`git log -5 --oneline`；确认当前分支、已有改动与最近提交。尚无提交时允许 log 不可用，记录原因。
2. 尚无 Git 仓库时，在 P0 初始化项目自己的仓库，默认主分支为 `main`；先核实没有误处于父目录仓库中，不擅自修改父仓库。已有仓库则沿用其主分支与约定，不重建历史。
3. 主分支保留可构建、相关自检通过的集成状态。开发使用短生命周期分支，建议 `feat/<wp-id>-<topic>`、`fix/<wp-id>-<topic>`、`docs/<topic>`；一次分支围绕一个有界目标，不建立长期平行版本。
4. 接手已有工作分支时优先延续它，不因每轮对话机械创建分支。是否提交由工作单元决定，是否记录由轮次决定；两者不能混为一谈。
5. 不覆盖、删除、暂存或顺手提交其他人/其他任务的改动；无法判断归属时先检查差异并记录。禁止为获得干净工作区而自动执行 `reset --hard`、`clean -fd`、整库 restore 或未经授权 stash。
6. 共享历史默认使用新提交修复；撤销已提交改动用 `git revert`。不得自行 force-push、重写共享提交、移动发布标签或丢弃用户工作。
7. 远端不是本地开发前提；未配置时照常本地开发、记录和提交。只使用已确认的远端与发布权限；用户未授权时不推送、开源或上传产物，不把配置 Git 等同于授权公开内容。

### 12.2 提交规则

采用 Conventional Commits 的项目约定：

```text
type(scope): 简洁说明结果

Why: 修改原因
Refs: WP-P4-003, REQ-019, TURN-20261013-001
Validation: 执行过的检查及结果；未执行则写原因
Breaking: 有不兼容变化时说明迁移方式，否则省略
```

- type 使用 feat、fix、refactor、test、docs、build、ci、chore、revert；scope 优先模块名，例如 learning、runner、knowledge、contracts。
- 一个提交对应一个可说明、可回退的逻辑变更；避免混入无关格式化、依赖升级和功能重写。大型工作包可多次提交，不要求每轮必有代码提交。
- 提交前检查完整 diff 与 staged diff、运行相应自检及 `git diff --check`；按明确路径暂存，禁止无检查地 `git add .`。
- 代码、对应测试、接口/迁移说明、必要轮次记录一并提交。工作分支可保存未完成检查点，但提交正文和记录必须注明失败/未运行项，不得标记 self_checked 或合入发布候选。
- 合入主分支前通过本工作包及依赖影响范围的检查；处理冲突后重新验证，不盲目选择 ours/theirs。默认使用保留工作包边界的合并提交；若采用 squash，必须保留 WP/REQ/TURN 标识和轮次记录。
- 不伪造提交哈希、测试结果或签名；Git 用户身份缺失时记录阻塞，不擅自冒用用户身份或修改全局 Git 配置。
- 不要求提交记录包含“当前提交自身哈希”，避免自引用循环。轮次记录保存开始时 HEAD；包含该记录的最终提交通过 Git 历史查询。

### 12.3 纳入与排除

P0 配置 `.gitignore`、`.gitattributes`、`.editorconfig`：文本 UTF-8，换行规则按文件类型固定，禁止整库无意义换行改写。

| 纳入 Git | 不纳入 Git |
|---|---|
| 源码、测试、合成 fixtures、需求、Plan、API Schema、提示模板 | API key、凭据文件、鉴权头、个人环境变量文件 |
| SDK/包版本与锁文件、迁移脚本、构建/验证脚本 | bin/obj、构建缓存、本地 IDE 状态、大型安装包 |
| ADR、工作包状态、轮次记录、CHANGELOG、脱敏验收摘要 | 用户源资料、真实资料库、学习数据库、实验作业、聊天内容 |
| 不含隐私且对复现必要的小型验证材料 | 未脱敏完整日志、内存转储、真实用户路径和外发内容 |

运行时数据使用第 7 节规定的用户数据目录，不写进源码目录。大型截图和原始测试产物默认保存在 Git 忽略的 artifacts 目录；提交的验收摘要记录相对路径、hash、保留位置与复现命令。重要发布证据需随私有发布归档保存，不能只引用会被清理的临时路径；对外上传仍需已有授权。

`.gitignore` 不会清除已跟踪秘密。提交前扫描 staged 内容；若误提交密钥，停止外传、报告并先使凭据失效，再处理历史清理，不把删除当前文件当作消除泄露。

## 13. 版本与发布管理

### 13.1 版本对象分离

| 对象 | 版本策略 | 变更责任 |
|---|---|---|
| 应用版本 | SemVer：MAJOR.MINOR.PATCH；预发布用 `-alpha.N`、`-beta.N`、`-rc.N` | 发布工作包维护根目录 VERSION，构建读取同一来源 |
| API/DTO/IPC/JSON Schema | 独立 schemaVersion / protocolVersion；兼容变化补字段，不兼容变化提升协议版本 | Contracts 所有者维护兼容性说明、调用方和契约测试 |
| SQLite 与资料库结构 | 单调递增迁移编号；已发布迁移不可改写 | Persistence/Knowledge 所有者提供迁移和失败恢复 |
| Prompt 与模型输出格式 | 每类任务有 promptVersion，绑定输出 Schema | Generation/Tutoring 维护；生成证据记录模型名及模板版本 |
| 已确认计划、正文、来源、实验材料 | 稳定 ID + 不可变版本/hash | 原模块维护；软件升级不能替换已冻结业务内容 |
| 需求与设计 | Git 历史 + REQ/WP/ADR/TURN 标识 | 变更时说明影响，不重新编号既有 REQ |

模型服务端行为可能变化，记录模型名与时间也不保证逐字复现；测试稳定性由 Fake fixtures 保证，真实联调单独记录。

### 13.2 应用版本规则

- 开发期从 `0.1.0-alpha.1` 起作为默认首次可运行预览版本；不要给当前只有文档的仓库虚报已发布版本。
- 首期全部 P7 门禁通过后发布 `1.0.0`。阶段号不等于版本号；P4 预览版不能标成首期正式版。
- 1.0 后，不兼容用户数据/对外契约或破坏既有使用方式时提升 MAJOR；向后兼容功能提升 MINOR；兼容修复提升 PATCH。0.x 阶段仍须记录不兼容变化与迁移，不能因版本未到 1.0 就默认删除用户数据。
- 在根目录维护 `CHANGELOG.md`，先写 Unreleased，发布时归入具体版本，分 Added/Changed/Fixed/Breaking/Known limitations；关联 REQ/WP/TURN，不以 Git commit 列表代替用户可理解的变更说明。
- 内部接口升级需同步调用方；遇到不支持的 Runner 协议、更新版本资料库或未知 Schema 时，明确拒绝不安全写入并说明兼容问题，不猜测格式继续覆盖。
- 新版本升级先备份和迁移验证；旧版不能直接写新版数据库。回退方案明确“应用二进制 + 对应备份数据”的兼容组合，不承诺所有迁移可以逆向执行。

### 13.3 发布流程

1. 汇总 Unreleased、未完成需求、兼容性、已知限制与人工验收状态；确定本次版本。
2. 从明确提交构建，工作区必须干净；锁定 SDK/包依赖，运行对应发布门禁。
3. 生成发布清单：应用版本、源提交完整 SHA、构建环境、依赖锁文件 hash、数据库/协议版本、产物 SHA-256、测试摘要、人工验收记录和迁移/回退说明。
4. 门禁满足后创建带说明标签 `v<version>`，标签指向实际用于构建的提交；发布包版本、VERSION、CHANGELOG、标签必须一致。已经发布的标签和产物不覆盖，修复用新版本。
5. 远端推送和发布沿用用户已授权的目标与范围；无授权时保留本地候选包，状态标记“待发布”，不能声称已上线或已上传。

## 14. 每轮修改记录与后续 LLM 检索

### 14.1 强制记录范围与文件归属

从后续实际开发开始，**每轮修改代码、测试、配置、需求、计划或其他项目文档，都必须新增一份轮次记录并更新索引**。有意义的只读排查、失败尝试及阻塞也要记录，避免后续 LLM 重复踩坑；纯闲聊或无项目进展的确认无需记录。轮次记录自身和索引更新属于同一轮，不递归生成新的日志。

```text
docs/changes/INDEX.md                    # 按轮次追加的可搜索索引
docs/changes/YYYY-MM/TURN-YYYYMMDD-NNN.md # 每轮独立记录，NNN 当日递增
docs/implementation-status.md           # 当前工作包状态
docs/adr/ADR-NNN-<topic>.md              # 长期架构决策
CHANGELOG.md                            # 发布级摘要，不替代逐轮记录
```

使用项目约定时区 Asia/Shanghai 标识轮次日期，记录完整带时区时间。创建前检查现有 ID，禁止覆盖同名历史文件。轮次记录属于项目 Git 文档，不写入工具的私人记忆目录，不依赖聊天历史或某个 LLM 的会话 ID 才能理解。

历史记录原则上追加后保留；修正错误用新轮次注明 `supersedes/corrects` 及旧记录链接。仅敏感信息清理等必要情况修改历史，并明确说明；不为了让报告好看而删失败证据。INDEX 和 implementation-status 是当前索引/状态，可更新但不能丢失历史记录链接。

### 14.2 每轮记录模板

```markdown
# TURN-YYYYMMDD-NNN：本轮结果

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
```

日志描述事实，不复制整段聊天或完整 diff，不保存内部推理过程。代码细节由文件和 Git diff 承载，日志保留动机、结论、证据与入口。只完成文档结构检查时必须明确“未运行软件测试”。

### 14.3 索引与接手流程

`INDEX.md` 每轮一行：`TURN-ID | 时间 | 阶段/WP | REQ | 模块 | 摘要 | 状态 | 记录相对链接`。它是检索入口，不存整份日志。不将提交 SHA 设为索引必填字段，避免记录与提交相互循环。

后续 LLM 开始一轮时必须：

1. 阅读仓库约定、`docs/implementation-status.md`、`docs/changes/INDEX.md` 的最近记录。
2. 按当前 REQ、WP、模块或文件名用 `rg` 查询索引与日志，只打开匹配记录及相关 ADR，不无差别重读全部历史。
3. 用 `git status`、`git log` 和当前源码核对日志；日志不能覆盖代码事实。发现旧记录失效时在新记录解释，不将旧结论当作已验证现状。
4. 恢复未完成任务前先确认已有改动归属、环境、失败项和下一步，避免重做已完成工作或覆盖未提交结果。

检索示例（均从仓库根目录执行；路径按实际记录替换）：

```powershell
rg -n 'REQ-026|M09|WP-P5' docs/changes docs/adr
rg -n 'blocked|interrupted|未运行' docs/changes
git log --all --oneline --grep='TURN-20261013-001'
git log --oneline -- docs/changes/2026-10/TURN-20261013-001.md
git show <commit-sha> -- <file-path>
```

长任务在切换工作包、完成关键验证、出现阻塞或预计上下文交接时先保存当前轮次草稿，再继续工作；结束前补齐结果及索引。异常中断导致记录未收尾时，下一轮根据 Git 与产物核对后追加恢复说明，不能补造未执行测试。

## 15. 将工程治理纳入阶段验收

- **P0 新增交付**：Git 初始化/现状登记、分支约定、忽略与格式规则、版本单一来源方案、CHANGELOG、轮次记录模板及 INDEX。首次开发轮即可记录，不必等待工程骨架全部完成。
- **每轮结束新增门禁**：有本轮记录、索引条目、工作包状态更新；实际改动与记录一致；自检失败/未运行项及未提交内容明确。没有达到可提交单元时可以保留未提交改动，但必须交接清楚。
- **每个工作包新增门禁**：提交或未提交原因可追踪；必要 ADR/契约/迁移已更新；stage diff 无无关改动和敏感信息。将本节并入 11.3 的 Definition of Done。
- **P7 新增门禁**：版本、标签、提交、包 hash 一致，CHANGELOG 与迁移/回退说明齐全；全部发布证据有持久入口。

LLM 自检：P0 实现文档检查脚本，验证 INDEX 链接存在、TURN-ID 唯一、必填字段齐全、相关 REQ/WP 可检索；提交前检查差异与敏感信息；发布时验证版本与产物清单一致。结构脚本不能代替对日志事实的核对，不要求记录声称已完成尚不存在的 Git 提交。

人工验收：任选一次轮次记录，能定位修改文件、原因、关联需求、实际测试和剩余事项；任选一个已发布版本，能从标签追到源提交、构建清单与迁移说明；让一个未参与原对话的 LLM 仅凭仓库文档和 Git 找到指定需求的最近修改及下一步，结果与实际仓库一致。

## 待办扩展：R-PLAN-SCALE-01

2026-10-17用户确认：正式版使用小时设置周学习时长，并按每周学习时长适当放宽课程规模和Token预算。M01/M04/M07共同负责；测试版维持当前值，不在本轮实现。实施前确定档位、上下限、模型硬限制和迁移精度，按 requirement.md 的保留项验收，不随本条改变冻结计划或模型接受标准。