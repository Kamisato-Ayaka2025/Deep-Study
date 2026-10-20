using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface ILabStore
{
    Task<LabPackage?> GetLabAsync(Guid subject, string node, CancellationToken ct = default);
    Task<LabJob?> GetLabJobAsync(Guid subject, string node, CancellationToken ct = default);
    Task<Result<LabJob>> BeginLabAsync(Guid subject, string node, CancellationToken ct = default);
    Task<bool> UpdateLabJobAsync(LabJob job, CancellationToken ct = default);
    Task<Result<LabPackage>> PublishLabAsync(LabJob job, LabMaterial material, string planHash,
        IReadOnlyList<PlanSource> sources, CancellationToken ct = default);
    Task<Result<LabPackage>> PublishPracticeAsync(LabJob job, GeneralPractice material, string planHash,
        IReadOnlyList<PlanSource> sources, CancellationToken ct = default);
    Task<Result<LabAttestation>> ConfirmLabAsync(Guid subject, Command<ConfirmLab> command, CancellationToken ct = default);
    Task<LabAttestation?> GetLabAttestationAsync(Guid subject, string node, CancellationToken ct = default);
    Task RecoverLabsAsync(CancellationToken ct = default);
}
public interface ILabExporter
{
    Task<Result<string>> ExportAsync(LabPackage package, string newZipPath, CancellationToken ct = default);
}
