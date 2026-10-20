using StudyHarness.Application;
using StudyHarness.Contracts;
namespace StudyHarness.Infrastructure;
public sealed class FakeProvider(ErrorCode? failure = null) : IModelProvider
{
    public string Destination => "local:deterministic-fake";
    public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(failure is { } code
            ? Result<ProviderReply>.Fail(code, "模拟服务错误；没有连接真实模型。")
            : Result<ProviderReply>.Success(new("这是确定性测试响应，不代表真实模型连接。", true)));
    }
}
