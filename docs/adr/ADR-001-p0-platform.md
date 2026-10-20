# ADR-001: P0 Windows desktop baseline

- Status: accepted for implementation; P0 validation recorded separately.
- Scope: WP-P0-001; REQ-001/002/003/004/032.
- Runtime: .NET 10 LTS, SDK 10.0.401 pinned without roll-forward; C# / WPF MVVM.
- Target: Windows 11 x64 desktop. This host is the initial verification environment, not a claim of coverage of every supported Windows release. P1 validates both C toolchains and isolation.
- Dependencies: Microsoft.Data.Sqlite and ProtectedData 10.0.12; xUnit 2.9.3 and pinned test tools. Restore produces committed per-project lock files.
- The portable candidate is framework-dependent and requires .NET 10 Windows Desktop Runtime. A full installer is a P7 deliverable.
- SDK stays under ignored .tools; bootstrap verifies the official SHA-512.
- P0 uses native WPF empty states. ReaderAssets is reserved for later bundled WebView2 content.
- No local HTTP service or network model requests. Fake Provider is explicitly identified in responses/UI.
- SQLite schema v1 stores settings and protected credentials; v2 adds transactional idempotency results. Migration uses SQLite backup before upgrading v1 and rejects newer versions. P0 does not create future business tables.
- Credentials use Windows DPAPI CurrentUser. settings stores a credential reference; the separate credentials table contains only ciphertext. Keeping encrypted blobs and settings in one transaction avoids inconsistent cross-file updates. Settings DTOs never expose key values.
- DPAPI protects data at rest under the current Windows identity; it is not a sandbox against processes already running as that user. Runner isolation remains P1 and Runner currently refuses execution.
- Logs accept only fixed fields: operation ID, UTC timestamp, success/failure, error enum. Exception text, prompts, model responses and secret values have no logging parameter.
- Test framework: xUnit; contract schemas evaluated with JsonSchema.Net. No arbitrary percentage target; the P0 gate checks behaviors and disallows zero/skipped tests.

## Official sources checked 2026-10-13

- [SDK downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Release metadata and checksums](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Windows support matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [DPAPI documentation](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection)
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12)
- [xUnit 2.9.3](https://xunit.net/releases/v2/2.9.3)
