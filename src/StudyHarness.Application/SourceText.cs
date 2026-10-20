using System.Text.RegularExpressions;

namespace StudyHarness.Application;

// Parsing/model views only. Stored snapshots, offsets and source hashes remain unchanged.
public static class SourceText
{
    public static string Markers(string text) => Regex.Replace(text, @"\\([`~$#?+""])","$1");
    public static string DecodeTransport(string text) => Regex.Replace(text, @"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", "$1");
    public static bool IsRepositorySource(string label) => label.Contains("https://github.com/OI-wiki/OI-wiki/raw/refs/heads/master/docs/", StringComparison.Ordinal);
    public static string ForModel(string label, string body)
    {
        if (IsRepositorySource(label))
        {
            body = DecodeTransport(body);
            if (body.Contains("--8<--", StringComparison.Ordinal) || Regex.IsMatch(body, @"!\[[^\]]*\]\("))
                body = "[宿主来源质量：正文包含未取得的外部代码/图片引用；引用占位不是代码或图像证据，不补造该文件/图示内容。可以基于完整文字规则另构造明确标注的教学例子。]\n" + body;
        }
        // An explicit, reviewed contextual error, not an automatic fact-checker.
        if (Regex.IsMatch(DecodeTransport(body), @"返回值不为\s*0\s*会导致运行时错误"))
            body = "[宿主来源勘误 CXX-EXIT-001：下面“返回值不为0会导致运行时错误(RE)”不能泛化为C++语言规则。main的return把值交给宿主环境，0表示成功；评测平台可能自行将非零退出归类RE。其他退出状态由实现定义，不能把任意应用退出码等同Windows系统错误码。不要据该错误泛化出题。依据C++工作草案basic.start.main第5段和support.start.term第9.3段（2026-10-20核验），https://eel.is/c++draft/basic.start.main 和 https://eel.is/c++draft/support.start.term 。原文保留供核对。]\n" + body;
        if (body.Contains("空白字符", StringComparison.Ordinal) && body.Contains("单行", StringComparison.Ordinal))
            body = "[宿主来源勘误 CXX-LAYOUT-002：原文的空白等价/必须单行是入门简化，不能作为无条件语言规则或正确选项。空白用于分隔词法记号，不能随意拆开或合并名字/运算符；字符串内容与预处理换行有语义。反斜杠紧接换行可拼接物理行（C++17也支持），预处理指令因此可跨物理行；普通字符串不能直接含未转义换行，原始字符串可含换行；//注释到逻辑行末。入门只讨论不使用续行、原始字符串且保持词法记号的示例，须明确该前提。依据C++工作草案lex.phases、lex.string、lex.comment（2026-10-20核验），https://eel.is/c++draft/lex.phases 、https://eel.is/c++draft/lex.string 、https://eel.is/c++draft/lex.comment 。原文保留。]\n" + body;
        return body;
    }
}
