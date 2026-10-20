# P7 C++算法与逐算法覆盖契约

关联R-CPP-ACCEPT-01、WP-P7-001、REQ-014/020/021/022/023/032/033/034/037。

## 计划

scope增加CppAlgorithms，实验toolchain固定portable-cpp（语言元数据，不探测工具链）。计划schema1增加可选algorithms:[{key,name}]，节点增加可选algorithmKeys；旧值null不序列化，旧冻结hash不重算。新C++计划要求唯一算法ID及名称、所有节点显式映射，基础概念课可为空，实验不可为空；每算法至少一理论课及一实验，实验直接/间接依赖相应理论。一课多个算法逐个检查；未知/重复/遗漏映射拒绝。目录与映射随计划冻结，在确认摘要中展示。

确定性检查验证显式清单的覆盖关系，不能从任意自然语言证明所有算法都已列全；用户确认前需核对清单语义。算法全集由确认计划定义，不要求穷尽竞赛算法。

## C++材料与测试接口

schema4专用于单文件C++17算法exercise；schema3继续用于C，legacy2仍可读取导出。冻结计划与材料语言在Service及事务发布两层检查。无DB迁移，learning.db保持v5。

模型提供solution.cpp、tests/cases.json、题意/输入输出/数据范围/示例、TODO框架及用例。模型不能提供完整答案。cases.json与cases必须完全一致。宿主从cases逐条构造test.cpp及固定外部编译说明，再按总量/单文件限额检查。模型测试文件只允许cases.json；已发布包另含确定性test.cpp，其内容须与宿主生成结果完全一致。

solution.cpp固定void solve(std::istream& in, std::ostream& out)接口与STUDY_HARNESS_TEST保护的main包装。正常提交只编译solution.cpp；测试只编译test.cpp，它先定义宏再包含solution.cpp，避免重复入口。宿主测试使用istringstream/ostringstream，每例重新建流，调用学生solve，assert精确比较输出（包含空白），NDEBUG直接编译报错。字面量按UTF-8字节八进制转义，避免用例注入C++语法。输入输出要求不因分类而删除。

单文件算法题始终exercise；连续/独立只影响课程组织，不构成project分类。C++工程、任意本地include、非白名单标准库/系统头、脚本、二进制、宏替换、任意文件/网络/进程执行仍不允许。源内容保守词法过滤不是C++解析器、安全证明或正确性证明。TODO、用例及预期仍需用户在外部检查，软件没有编译执行入口。

## 验证和兼容

CppTests覆盖多算法单课遗漏、未知映射/依赖/语言冲突、旧字段序列化、分类、入口/宏/危险头文件、生成导出与重启。P7DesktopCheck覆盖实际WPF显示与确认。P7Harness在独立目录发送合成二分查找/前缀和教材，真实生成计划、理论与题目；遍历测验/确认仅为合成测试驱动，不冒充用户学习或外部测评。

旧程序不理解CppAlgorithms/schema4，不应打开含这些新内容的数据；回退须用升级前独立备份及旧二进制。不会自动重写旧学科、旧材料或旧完成记录。
