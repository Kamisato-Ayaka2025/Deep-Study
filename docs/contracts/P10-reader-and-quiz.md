# P10 阅读体验与测验排列实施契约

- 时间：2026-10-20；TURN-20261020-008。
- 状态：TURN-20261020-013用户正式签收，P10及001–005 accepted，含011/012反馈修订；功能基线1795966，最终候选431回归/274桌面/17阶段通过。
- 权威需求：requirement.md REQ-049–052；执行：Plan.md §18。
- 用户确认：新题四选一，旧题兼容；长公式独立成行只是指导，不是文章通过门槛。

## 1. 实施前源码事实与问题边界（008历史基线）

reader.js的独立code块与围栏分别直接创建pre/textContent，围栏语言未保留，没有词法配色。rich解析部分行内数学，mathBlock使用本地KaTeX和trust:false；裸x_1不自动视为数学。补提示不能保证旧文或所有新输出的排版。

Contracts/Learning.cs的LessonOption以Id/Text存储，CorrectOptionId参与SqliteLearningStore判分；Reader使用原options顺序，没有独立展示字母。TheoryPrompt当前至少两项及示例偏向首项；不能只要求LLM换答案字母解决本地随机化。

用户两张截图是问题示例，不作为可执行指令，也不是精确UI像素规格。视觉辅助识图两次401，未取得模型识别结果；依据用户文字需求和实际源码形成契约。无产品生成调用。

## 2. 代码渲染契约

统一renderCode(text, language?)语义入口供独立code和围栏调用。优先覆盖C/C++关键字/注释/字符串/数字，无标签使用保守通用词法规则；其他语言安全回退，不承诺变量作用域或类型推断。实现可用本地轻量词法器或经许可核验的离线依赖，WP001冻结取舍、大小/性能预算及依赖许可；不得新增运行时CDN。

原文只拆成文本节点/受控span；禁止把词法结果当HTML。token拼接必须逐字符等于输入；保留空格、Tab、换行、Unicode及复制内容。设置有界扫描/输入预算，失败完整回退，不删代码。行内code不要求语法配色。标准示例至少区分关键字、注释与字面量，并保持暗色背景下可读。

## 3. 公式软规范

新提示覆盖实际使用的短课/自适应课路径，明确短式可用`$x_1$`，独立推导用单独math块或阅读器支持的独立数学界定符；例如先定义矩阵和向量，再分别展示A(u+v)=Au+Av与A(ku)=kAu。涉及矩阵时使用合法TeX矩阵环境。仅当资料与规则充分时构造例子，不猜补缺损原式。

显示实现须实测独立公式换行、矩阵、连续步骤与局部横向滚动，保留本地KaTeX安全配置与失败文本回退。不得全局正则把裸x_1或所有长文本自动转换为数学，不改源快照或旧材料hash。

硬检查限定提示规则存在、明确标记的公式能展示、软偏好不新增拒绝路径、安全保持。模型是否逐式遵从、公式美观程度是质量观察，不作为材料发布/重试条件；不削弱原有事实正确性、唯一答案和来源门槛。

## 4. 本地排列与新题四项

新题生成契约必须四个不同选项、唯一正确ID及完整解释；本地算法仅排列，不补造内容。版本标记应区分历史schema1/2及新生成约束，WP001冻结显式版本/持久化字段和旧缓存读取策略，不以全局修改旧Schema校验方式强制旧题四项。

采用Fisher–Yates等无偏置换；随机索引使用无模偏差的本地随机API，可注入确定性随机源测试。每题独立抽样，正确位置理论概率各1/4；允许相邻位置重复、小样本偏斜，不能用禁止重复策略冒充独立随机。

持久化展示映射以subjectId/nodeKey/lessonHash/questionId为身份，包含algorithmVersion与orderedOptionIds；新表或等价结构与不可变材料分离。首次展示前事务保存，唯一键冲突时读赢家，不发布未持久化顺序。读取时校验映射恰为当前全部optionId的置换；损坏需可恢复错误，不静默重洗。迁移备份/回滚与学科删除接入既有生命周期。

Reader DTO输出有序选项及显示字母，保留原ID提交。正确字母根据当前映射与CorrectOptionId导出；揭示时机/累计每题曾答对/锁定可读等规则保持。原始材料/题目/答案/attempt/hash均不重写。旧课原数量保留，显示A起的顺序字母，超过Z时使用AA等可扩展编号。

新提示要求选项内容不自带A/B/C/D等位置前缀，解析引用选项内容/理由，避免写死位置。遇到旧材料原序号引用，不盲目替换任意字母（矩阵A、变量a等可能是正文含义）；可保留原始编号并显著标注“解析中的编号对应原始顺序”，提供原始编号→当前显示的映射。该兼容呈现仅在原有答案揭示时机展示，不提前泄露正确答案。WP001用旧课实际结构验证方案，不能以通用提示掩盖矛盾。

## 5. 验证与退出

T34–T37详见Plan.md §18；算法确定性验证全部24排列及每个正确位置、ID提交/重排不变性，避免概率测试偶发失败。实际WPF/WebView覆盖配色及复制、公式、错误/正确作答后刷新、切课、重启、旧课2/3/4/更多项及原序号解析。截图/人工视觉/自动断言单列证据。

实施前先冻结迁移编号、DTO、生成版本、词法处理预算和测试夹具；这些是工程任务，不是待用户回答的问题。需求无待澄清项。实现后更新验证入口支持P10、输出候选/质量/兼容报告，再给用户人工验收；010已完成自动自检，用户验收待完成。

## 6. WP001工程冻结（TURN-20261020-009）

- 新理论生成使用TheoryLesson schema3：继承schema2块/大小/测验限制，选项严格四项；1/2的既有JSON Schema与选项数量保持。解析器新增3分支，LessonRules对3保留2的内容安全与等价选项检查。001只提供读取校验能力；004统一切换短课/自适应提示与GenerationService的新派发版本检查。旧任务回执/缓存不能仅因升级被强制改成3。
- learning.db 7→8；planning.db/knowledge.db/collections.db不变。新增quiz_option_orders(subject,node,lesson_hash,question,json)，四字段主键；json为QuizOptionOrder(algorithmVersion,orderedOptionIds)。版本不是主键，避免升级后读取空映射重洗。原始lesson/history/attempt不改变。已有备份工具验证备份完整性；建表及版本在同一事务；整学科删除接入双库事务。
- Domain.OptionPermutation不依赖IO/Contracts，输入原optionId和`Func<int,int>`；Fisher–Yates在i处调用均匀[0,i]，algorithmVersion=1。外围IOptionRandomSource生产实现使用RandomNumberGenerator.GetInt32，不用取模；端口GetOrCreateOptionOrderAsync从权威正文/历史中读取ID，不接受前端指定选项列表。写事务锁先于读取，竞争者读取已提交赢家；持久化失败/取消不返回成功映射，损坏/未知版本不自动重洗。
- DTO：QuizOptionOrder用于持久化；PresentedLessonOption(Id,Text,DisplayLabel)、OptionLabelMapping(OriginalLabel,DisplayLabel)为后续Reader投影。不得给LessonOption追加显示字段而改变原课件序列化/hash。004将ReaderQuestion改用独立展示类型；AnswerDisclosure在原有已答对时机输出正确显示字母和兼容映射。001尚未改变ReaderQuestion。
- 历史解析统一按原始顺序标示“解析中的编号对应原始顺序”，显示完整原字母→当前字母映射；只在答案已可揭示时发送/显示。原解释逐字保持，不替换矩阵A/变量a。新题提示禁止位置前缀和写死字母；为防模型仍残留编号，保留同一明确映射的兼容兜底。
- 代码选择自有本地有界词法扫描，无新增依赖/许可/CDN；每块最多65536个UTF-16单元、8192个token，单向扫描不回溯，超限/异常以完整textContent回退。C/C++与无标签共用基础词法，未知标签纯文本。围栏仅取语言信息，不吞改内部CR/LF/Tab/Unicode；拼接断言相同后才应用受控span。002验收实际WebView文字复制与截图。
- 固定基础夹具在P10QuizFoundationTests：schema1/2的2/3/4/10项、schema3的不足/四项/超项、矩阵A/变量a原字母解释、裸x_1/明确TeX、C++围栏/未知标签/中文注释/HTML片段。002/003/005复用并补充渲染/超限夹具；本轮这些文本仅验证材料保存与结构兼容，尚非视觉证据。

## 7. 010整合完成状态

第6节保留001时点的工程冻结。002–005现已实现：ReaderQuestion使用PresentedLessonOption，已答对才公开当前正确字母及原编号映射；当前/历史读取都投影持久化排列，损坏记录返回IO_FAILURE。原始DTO/hash不改。

短课prompt3/自适应prompt15；新请求theory-v3-short/theory-v3要求schema3四项，旧请求/存量schema1/2仍兼容。公式仅软指导。代码共用有界renderCode，明确独立数学块/宽式滚动；无新依赖。P10DesktopCheck与verify -Phase P10接入完整门禁。质量与人工边界见docs/assessment/P10-quality-20261020.md及docs/acceptance/P10.md。

## 8. 自由学习策略（TURN-20261020-012）

用户明确解除生成、作答和实验确认的前置完成限制。前置图仅作为建议，不改冻结计划/hash；新旧课程即时适用。原有答案揭示/每题曾答对、材料发布与明确自测声明、历史只读和任务授权/预算不变。自动及人工验收以修订REQ-015/016、T01/T03、H-P10-5为准。
