using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-location-tests", Guid.NewGuid().ToString("N"));
    private string Dir(string name) { var path = Path.GetFullPath(Path.Combine(root, name)); Directory.CreateDirectory(path); return path; }
    private DataDirectorySettings Locations(string? profile = null) => new(Dir("app"), Dir("local"), profile);
    private static void Seed(string path) => _ = new SqliteSettingsStore(path);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void NewInstall_UsesAdjacentData_ExistingLegacyAndExplicitProfileRemainCompatible()
    {
        var locations = Locations();
        Assert.Equal(Path.Combine(root, "app", "data"), locations.Resolve());
        var legacy = Dir("local/StudyHarness"); Seed(legacy);
        Assert.Equal(legacy, locations.Resolve());
        var portable = Dir("app/data"); Seed(portable);
        Assert.Equal(portable, locations.Resolve());
        var explicitRoot = Dir("explicit");
        Assert.Equal(explicitRoot, Locations(explicitRoot).Resolve());
    }
    [Fact]
    public async Task OfflineMigration_PreservesCredentialLearningAndReferences_RebasesInternalLibrary()
    {
        var source = Dir("original"); var target = Dir("moved"); var locations = Locations(source);
        var seed = await LabFixtureProvider.SeedAsync(source);
        var plans = new SqlitePlanningStore(source); var learning = new SqliteLearningStore(source, plans);
        var job = (await learning.BeginLabAsync(seed.Id, "L1")).Value!;
        var lab = (await learning.PublishLabAsync(job, LabFixtureProvider.Material, seed.Plan!.Hash, seed.Plan.Sources)).Value!;
        Assert.True((await learning.ConfirmLabAsync(seed.Id, new(Guid.NewGuid(), 0, new("L1", lab.Hash, true)))).Ok);
        var settings = new SettingsService(new SqliteSettingsStore(source)); var initial = await settings.GetAsync();
        var key = "fake-key-" + Guid.NewGuid();
        Assert.True((await settings.SaveAsync(new(Guid.NewGuid(), initial.Revision, new("deepseek-chat", GenerationMode.Click, ConsentMode.Each, key)))).Ok);
        var oldAppHash = Hash(Path.Combine(source, "app.db")); var oldRegistryHash = Hash(Path.Combine(source, "libraries.db"));
        var library = await new KnowledgeStore(source).CurrentAsync();
        Directory.CreateDirectory(Path.Combine(source, "reader-profile")); File.WriteAllText(Path.Combine(source, "reader-profile", "lockfile"), "temporary browser cache");
        locations.Schedule(source, target, true);
        await locations.ApplyPendingAsync();
        Assert.False(locations.Read()!.CopyPending);
        Assert.Equal(target, Locations(source).Resolve()); // Same launcher still resolves to the chosen location.
        Assert.Equal(oldAppHash, Hash(Path.Combine(source, "app.db")));
        Assert.Equal(oldRegistryHash, Hash(Path.Combine(source, "libraries.db")));
        Assert.Equal(oldAppHash, Hash(Path.Combine(target, "app.db")));
        Assert.Equal(key, await new SqliteSettingsStore(target).ReadKeyAsync(default));
        var movedPlans = new SqlitePlanningStore(target); var movedLearning = new SqliteLearningStore(target, movedPlans);
        Assert.Equal(seed.Plan.Hash, (await movedPlans.GetAsync(seed.Id))!.Plan!.Hash);
        Assert.Contains("L1", await movedLearning.CompletedNodesAsync(seed.Id));
        Assert.Equal(lab.Hash, (await movedLearning.GetLabAsync(seed.Id, "L1"))!.Hash);
        var movedKnowledge = new KnowledgeStore(target);
        Assert.Equal(library!.Id, (await movedKnowledge.CurrentAsync())!.Id);
        Assert.StartsWith(target, (await movedKnowledge.CurrentAsync())!.Root, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull((await movedKnowledge.ResolveAsync(seed.Plan.Sources[0].Reference)).Text);
        Assert.False(Directory.Exists(Path.Combine(target, "reader-profile")));
    }
    [Fact]
    public async Task ExternalLibrary_RemainsAtOriginalLocation()
    {
        var source = Dir("source"); Seed(source); var target = Dir("target"); var external = Dir("external");
        var knowledge = new KnowledgeStore(source);
        var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(external)))).Value!;
        var hash = Hash(Path.Combine(external, "knowledge.db"));
        var locations = Locations(); locations.Schedule(source, target, true); await locations.ApplyPendingAsync();
        Assert.Equal(library.Root, (await new KnowledgeStore(target).CurrentAsync())!.Root);
        Assert.Equal(hash, Hash(Path.Combine(external, "knowledge.db")));
    }
    [Fact]
    public async Task SwitchExisting_DoesNotMerge_AndMissingChoiceDoesNotCreateEmptyDatabase()
    {
        var source = Dir("source"); Seed(source); var target = Dir("target"); Seed(target);
        var original = Hash(Path.Combine(source, "app.db")); var locations = Locations();
        locations.Schedule(source, target, false); await locations.ApplyPendingAsync();
        Assert.Equal(target, locations.Resolve()); Assert.Equal(original, Hash(Path.Combine(source, "app.db")));
        File.Delete(Path.Combine(target, "app.db"));
        await Assert.ThrowsAsync<IOException>(() => locations.ApplyPendingAsync());
        Assert.False(File.Exists(Path.Combine(target, "app.db")));
        locations.CancelChange(source); Assert.Equal(source, locations.Resolve());
    }
    [Fact]
    public async Task PopulatedOrNestedTargetAndCancellation_DoNotOverwriteData()
    {
        var source = Dir("source"); Seed(source); var target = Dir("target"); var locations = Locations();
        Assert.Throws<IOException>(() => locations.Schedule(source, Path.Combine(source, "nested"), true));
        locations.Schedule(source, target, true);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must remain");
        await Assert.ThrowsAsync<IOException>(() => locations.ApplyPendingAsync());
        Assert.Equal("must remain", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.True(locations.Read()!.CopyPending);
        var empty = Dir("empty"); locations.Schedule(source, empty, true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locations.ApplyPendingAsync(new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
    }
    [Fact]
    public void ActiveSource_IsNotCopied()
    {
        var source = Dir("source"); Seed(source); var locations = Locations(); var target = Dir("target");
        locations.Schedule(source, target, true);
        using var running = new Mutex(true, DataDirectorySettings.MutexName(source), out var acquired);
        Assert.True(acquired);
        try { Assert.Throws<IOException>(() => locations.ApplyPendingAsync().GetAwaiter().GetResult()); }
        finally { running.ReleaseMutex(); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
    [Fact]
    public async Task UnsavedKeyBlocksLocationChange_CancelRestoresCurrentProfile()
    {
        var source = Dir("source"); var target = Dir("target"); var locations = Locations(source);
        var vm = new ShellViewModel(new(new SqliteSettingsStore(source)), new(new FakeProvider()), new SafeLog(Path.Combine(source, "test.log")), source, locations);
        vm.SetPendingApiKey(true);
        Assert.False(await vm.ChangeDataDirectoryAsync(target, true)); Assert.Null(locations.Read());
        vm.DiscardPendingApiKey(); Assert.True(await vm.ChangeDataDirectoryAsync(target, true));
        Assert.Contains(source, vm.DataLocation); Assert.Contains(target, vm.DataDirectoryStatus);
        var differentProfile = Dir("different-profile");
        Assert.Equal(differentProfile, Locations(differentProfile).Resolve());
        await vm.CancelDataDirectoryChangeAsync(); Assert.Equal(source, locations.Resolve());
    }
    [Fact]
    public async Task InterruptedPreferenceCommit_ResumesCompletedCopyWithoutOverwrite()
    {
        var source = Dir("source"); Seed(source); var target = Dir("target"); var locations = Locations();
        locations.Schedule(source, target, true); var pending = locations.Read()!;
        await locations.ApplyPendingAsync();
        File.WriteAllText(Path.Combine(target, "preserve.txt"), "completed target");
        File.WriteAllText(Path.Combine(root, "local", "StudyHarnessLocations", "default.json"), System.Text.Json.JsonSerializer.Serialize(pending, Wire.Options));
        await locations.ApplyPendingAsync();
        Assert.False(locations.Read()!.CopyPending);
        Assert.Equal("completed target", File.ReadAllText(Path.Combine(target, "preserve.txt")));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
