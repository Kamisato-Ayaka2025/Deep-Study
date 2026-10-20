using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class LabExecutionBoundary
{
    // No setting, model response or fixture result can grant real-code readiness.
    // Replace only after ADR-006 / PSEC S1-S8 evidence and human acceptance.
    public static LabAvailability Availability(bool prerequisitesMet) => new(false, false, false, false, false,
        (prerequisitesMet ? "前置课程已满足。" : "前置课程尚未完成。") +
        "PSEC 安全门禁尚未通过：磁盘硬限制及完整安全集成待完成。实验暂不可生成、验证或开始；未创建用户实验文件，也没有实验通过记录。");

    public static Result<T> Refuse<T>() => Result<T>.Fail(ErrorCode.ISOLATION_UNAVAILABLE,
        "PSEC 尚未通过，真实参考解和学习者代码执行关闭。");
}
