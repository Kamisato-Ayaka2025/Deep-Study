using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Conservative local routing, not a claim of automatic fact verification.
public static class TheoryKnowledgePolicy
{
    private static readonly string[] Dynamic = ["NOIP", "CSP", "ICPC", "竞赛", "比赛", "赛制", "规定", "政策", "法律", "价格", "最新", "当前", "现行", "今年", "版本", "教材", "原文", "定理编号"];
    private static readonly string[] Stable = ["集合", "矩阵", "向量", "线性代数", "函数", "导数", "积分", "概率", "算法", "复杂度", "排序", "二分", "递归", "循环", "变量", "数组", "指针", "C++", "C语言", "cin", "cout", "输入输出", "程序结构", "命题", "逻辑", "整数", "加法", "元素", "关系", "网络", "IP", "CIDR", "子网", "掩码", "前缀", "路由", "协议", "分组", "TCP", "UDP", "HTTP", "DNS", "DHCP", "NAT", "以太网", "链路", "传输", "交换", "拥塞", "检错", "CRC", "计算机", "存储", "缓存", "处理器", "指令", "寄存器", "电路", "编码"];
    public static bool NeedsEvidence(string text) => Dynamic.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
        Regex.IsMatch(text, @"\b20\d{2}\b");
    public static bool CanUseKnowledgeOnly(PlannedNode node) =>
        node.Type == "theory" && !NeedsEvidence(node.Title) && node.Objectives.Count > 0 &&
        node.Objectives.All(o => !NeedsEvidence(o) && Stable.Any(t => o.Contains(t, StringComparison.OrdinalIgnoreCase)));

    public static string? MissingEvidence(PlannedNode node, IReadOnlyList<ModelSource> materials)
    {
        if (materials.Count == 0)
            return CanUseKnowledgeOnly(node) ? null : "本课含动态规则、指定资料或未确认的知识范围，需要对应原文；稳定基础知识可由模型补充。";
        foreach (var objective in node.Objectives.Prepend(node.Title).Where(NeedsEvidence))
        {
            var anchors = new[] { "NOIP", "CSP", "ICPC" }.Where(t => objective.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!materials.Any(m => LessonSourceSelector.HasBody(m.Text) &&
                anchors.All(a => m.Text.Contains(a, StringComparison.OrdinalIgnoreCase)) &&
                LessonSourceSelector.Score(m.Text, LessonSourceSelector.Terms(objective)) >= 2))
                return "本课动态规则或指定资料目标缺少对应正文，未使用模型记忆替代。请补充相关资料。";
        }
        return null;
    }

    public const string Guidance = """

        混合资料规则：通用学科知识（包括数学、逻辑、编程、算法和计算机网络概念）允许使用模型已有知识补充，补充段落明确标注“基础知识补充”，自行核算例题和测验。
        不把模型记忆称为已检索的知识库或来源；引用sourceKeys只能指本次实际原文，补充知识不得冒用资料引用。
        动态规则、赛事语言规定、政策、版本行为和指定教材结论只能依据对应原文，并说明来源适用范围；旧网页不能证明当前规定。无法核实的当前规则明确说明不确定，不凭记忆填写，不据此出题。
        不猜补缺损原式、未取得的include或图示中实际不清晰/缺失的部分；清楚的OCR公式和图示细节可以正常使用。可以另构造并核算基础示例，明确不是原文复原。
        """;
    public static TheoryLesson Label(TheoryLesson lesson)
    {
        var id = "host_knowledge_notice";
        while (lesson.Blocks.Any(b => b.Id == id)) id += "_";
        var text = lesson.SourceKeys.Count == 0
            ? "本课为模型基础知识讲解，未引用外部资料。例题和测验未经人工逐项核实。"
            : "本课采用资料与基础知识补充的混合方式。基础知识补充不代表原文引用；动态规则仅按所引资料的适用范围解释，不保证为最新规定。";
        return lesson with { Blocks = new[] { new LessonBlock(id, "paragraph", text) }.Concat(lesson.Blocks).ToArray() };
    }
}
