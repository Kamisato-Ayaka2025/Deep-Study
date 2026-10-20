# P3 planning contract v1

## 所有权与存储

M04通过PlanningService编排M03资料、M07外发和IPlanningStore；M01只调用应用服务。
planning.db v1独立存储草稿/冻结学科JSON、幂等结果、PlanConfirmed outbox；沿用P2独立功能库方式，app.db v2不迁移。
Subject ID沿用draft UUID；Course key经subject UUID和key派生稳定UUID，因此不同学科不共享课程身份。
当前没有Lesson表、生成队列或完成记录。outbox供P4消费，P3不派发正文。

## 命令与确认

Start/Revise/SaveProposal/Confirm均带requestId和expectedRevision。SQLite事务重新读取当前状态；
同requestId同命令返回原结果，异操作或异载荷返回IDEMPOTENCY_CONFLICT。Revise清除旧候选；
模型返回时以请求发起时revision条件写入，迟到响应返回REVISION_CONFLICT。确认需ackFrozen及匹配hash，
未解决澄清或范围外实践拒绝确认；确认原子写confirmedAt/revision/outbox。确认后所有计划写命令返回PLAN_FROZEN。
不重复确认产生事件，不创建正文或进度。取消令牌在提交前检查。旧已完成请求重放返回历史结果，不修改当前状态。

## 模型契约

prompts/planning-v1.txt及schemas/plan.schema.json均嵌入程序集并版本化。
TURN-20261016-015：模型预览也包含同一份完整plan.schema.json，单一文件同时嵌入Application提示与Infrastructure验证器，测试核对字节内容一致；明确H2默认值value为字符串，而顶层weeklyMinutes为整数。Schema失败只展示白名单字段路径和约束关键字（最多5项），不展示模型值或任意属性名；合法oneOf的未选分支不作为错误。JSON模式不等于Schema合规，仍需本地校验。
Schema拒绝额外字段、非法课型、正文/状态/路径字段；确定性检查DAG、已知输入、默认值一致性、
sourceKeys属于实际解析并发送的来源、实践范围及连续实验前置和工具链。
连续后课可以继承独立起点或连续实验；前课必须存在且为实验，须列入直接前置并保持工具链一致。独立课自身不可带previousLabKey；连续项目起点允许previousLabKey=null。模式描述本课的起点来源，不限制其产物能否被后课继承。TURN-20261016-013修正了原实现额外要求前课也为continuous的限制。
自然语言语义仍由模型拟定并由用户在确认前修正，不声称程序证明目标覆盖或素材语义适用性。
优先已选资料；未选时从当前库检索；不足明确提示补充，不联网。指定素材未采用时拒绝候选，要求用户调整。
每周时间为0在外发前拒绝。周安排由宿主按层号和原顺序稳定打包，单课不得超过每周预算。
TURN-20261016-017：Math+明确且独立的“学习C语言/编程”短请求，在Prepare中返回VALIDATION_ERROR，早于资料解析/Provider；窄匹配不是通用语义分类，不拦截“不要C语言，只学集合”等复杂表达。其他冲突由模型保留显式scope并给clarifications，不得改范围；澄清未解不能确认。所有非Auto scope纳入knownFields；scope永远不是允许的默认字段。
TURN-20261016-016：每次Prepare按当前草稿生成knownFields及allowedDefaultFields，加入同一外发预览；已填写字段即使值等于默认值也不得进入defaults。修订后重新计算，不复用旧默认标签。确定性校验仍拒绝重复项、已知字段误标、默认值与计划字段不一致，分别提供白名单字段级错误。
计划hash绑定subject/input/candidate/实际引用/周安排，不含可变usage。完整输入和引用属于私有本地学习数据。

## 外发与执行边界

TURN-20261017-001：计划使用按需读取协议。首轮输入及来源目录不含资料正文；模型经search_materials(query)/read_material(id)访问本次预览授权的不可变资料快照。工具没有路径/执行/写入参数。ImportReport增加本次成功导入References，可一次替换引用栏；项目导入新增UTF-8 .c/.h，80行分块，记录相对路径/行号。普通资料入口仍支持PDF/Markdown。

ModelReview/ApprovedModelRequest分别保留首轮Sources与只读ReadableSources。读取范围最多99块、单块16000字符、总正文1Mi字符。每次发送建立独立读取会话，最多8轮/12次工具调用/65536字符工具结果，总输出8192 tokens。报告累计输入超过200000 tokens即停止（这是返回后阈值，不是费用硬上限）；工具轮缺失usage不继续发送。取消、拒绝、未知工具、越界id、额外参数、预算耗尽均不保存候选、不自动重试。工具搜索只返目录项，读取才返正文；计划引用限实际读取子集，SimpleC实验还须引用指定项目源码。

ProviderReply与PlanDocument增加可选ReadSourceIds/ToolCalls；旧记录缺省为null/0，不改旧计划hash。读取记录和usage是诊断元数据；计划hash继续绑定实际引用版本及课程内容。UI保留完整潜在外发范围预览，授权覆盖这次限定工具会话；目录变动需重新导入、保存、预览。P2普通模型调试入口仍直接发送选中片段。

PlanningReview绑定不可变外发快照、草稿修订和模型。更改输入先保存再重新预览；模型更换重新授权。
每次用户点击同意对应本次计划请求，无隐式重试。模型最大输出8192 tokens在预览展示，不是成本承诺；
普通P2请求仍512。实际usage保存在计划中。凭据仅transport使用，不进入学习内容。
TURN-20261016-014：PlanningService设置JsonOutput=true，该选项随不可变预览及授权传递；仅计划请求显式发送response_format=json_object和thinking.type=disabled，保留8192上限，预览说明此模式。非计划请求不改变服务默认模式。Provider分别报告length、content_filter、空最终回复、未知结束状态和凭据回显，不输出原始正文/推理/未知结束字符串，不自动重试。JSON模式仍须经过Schema及语义规则校验。
CanStartLab只是未来学习资格；PSEC blocked，真实执行关闭。图按钮当前仅查看标题/目标和计划，不生成或运行。

## 检查点

WP-P3-002已通过Release构建和101项非Runner回归（含PlanningTests/PlanningServiceTests 20项）。
证据artifacts/p3-planning/build-2.log、non-runner.trx。首次构建xUnit2031失败已修，不改测试断言。
桌面、真实模型和完整P3验收另在WP-P3-003完成；不能以本契约校验代替人工签收。
