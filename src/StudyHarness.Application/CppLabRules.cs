using System.Text;
using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// A material contract, never a compiler or a proof of program correctness.
public static class CppLabRules
{
    public const string MainWrapper = "#ifndef STUDY_HARNESS_TEST\nint main() {\n    solve(std::cin, std::cout);\n    return 0;\n}\n#endif";
    public const string Assessment = "\n\n## C++17 外部测评接口\n完成 solution.cpp 中 solve(std::istream& in, std::ostream& out) 的 TODO，使用 in/out 处理题目规定的标准输入输出。\n- 普通提交：仅编译 solution.cpp，例如 `g++ -std=c++17 solution.cpp -o solution`。\n- 断言测试：仅编译 test.cpp，例如 `g++ -std=c++17 test.cpp -o test`；它已包含 solution.cpp，切勿再同时编译两者。\n- 不得定义 NDEBUG；测试使用内存流逐例调用你的 solve 并精确比较输出（含空格和换行）。成功正常退出，错误触发断言；编译错误请先核对接口与宏保护。\n这些命令只供你在外部自行执行；软件没有运行它们。预期输出由模型提供，需人工核对，测试通过也不是完整正确性证明。\n";

    public static string TestSource(IReadOnlyList<LabCase> cases)
    {
        var code = new StringBuilder("#ifdef NDEBUG\n#error NDEBUG disables assertions\n#endif\n#define STUDY_HARNESS_TEST\n#include \"solution.cpp\"\n#include <sstream>\n#include <cassert>\n#include <string>\nint main() {\n");
        foreach (var item in cases)
            code.Append("    { std::istringstream in(").Append(Literal(item.Input)).Append("); std::ostringstream out; solve(in, out); assert(out.str() == ").Append(Literal(item.Expected)).Append("); }\n");
        return code.Append("    return 0;\n}\n").ToString();
    }

    private static string Literal(string value) => "\"" + string.Concat(Encoding.UTF8.GetBytes(value).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + "\"";

    public static bool Valid(LabMaterial material)
    {
        if (material.ExperimentKind != "exercise" || material.SourceFiles.Count != 1 || material.SourceFiles[0].Path != "solution.cpp" ||
            material.TestFiles.Count != 2 || material.TestFiles.FirstOrDefault(f => f.Path == "test.cpp")?.Text != TestSource(material.Cases)) return false;
        var source = material.SourceFiles[0].Text.Replace("\r\n", "\n").TrimEnd();
        if (!source.EndsWith(MainWrapper, StringComparison.Ordinal)) return false;
        var body = LabMaterialRules.WithoutComments(source[..^MainWrapper.Length]);
        // Only include directives before the fixed wrapper; user/model macros cannot disable the harness.
        if (Regex.IsMatch(body, @"(?m)^\s*#\s*(?!include\b)") ||
            Regex.IsMatch(body, @"\b(main|STUDY_HARNESS_TEST|NDEBUG|assert|cin|cout|cerr|clog|scanf|printf|exit|abort|quick_exit|_Exit)\b") ||
            !Regex.IsMatch(body, @"void\s+solve\s*\(\s*std::istream\s*&\s*in\s*,\s*std::ostream\s*&\s*out\s*\)\s*\{")) return false;
        return LabMaterialRules.CodeAllowed(source, true) && !Regex.IsMatch(body, "#\\s*include\\s*\"");
    }

    public static Result<LabMaterial> Prepare(LabMaterial material, IReadOnlyList<string> sources)
    {
        // Model provides one canonical case file. Test code is deterministically derived, never an answer.
        if (material.Cases is null || material.Cases.Count is < 1 or > 20 ||
            material.Cases.Any(c => c is null || c.Input is null || c.Expected is null || c.Input.Length > 16384 || c.Expected.Length > 16384) ||
            material.TestFiles is null || material.TestFiles.Count != 1 || material.TestFiles[0] is null || material.TestFiles[0].Path != "tests/cases.json")
            return Result<LabMaterial>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "C++材料必须只提供cases.json，宿主据用例生成test.cpp。");
        return LabMaterialRules.Validate(material with
        {
            TestFiles = [.. material.TestFiles, new("test.cpp", TestSource(material.Cases))],
            Assessment = material.Assessment + Assessment
        }, sources);
    }
}
