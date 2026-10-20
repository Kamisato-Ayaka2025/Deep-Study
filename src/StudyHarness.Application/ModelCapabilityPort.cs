using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface IModelCapabilitySource
{
    Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default);
}
