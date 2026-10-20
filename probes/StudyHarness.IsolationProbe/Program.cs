using System.Text.Json;
using StudyHarness.Runner;

// Developer-only mechanism probe. Not the product Runner IPC entrypoint: it accepts a
// whole command line so that isolation variants can be compared while P1 selects a
// mechanism. Deleted once the production Runner owns the launch path.
if (args.Length != 3 || args[0] != "--spec")
{
    Console.Error.WriteLine("Usage: --spec <spec.json> <result.json>");
    return 64;
}
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var spec = JsonSerializer.Deserialize<SandboxSpec>(File.ReadAllText(args[1]), options);
if (spec is null)
{
    Console.Error.WriteLine("Unreadable spec.");
    return 64;
}
Sandbox.Run(spec, out var result);
File.WriteAllText(args[2], JsonSerializer.Serialize(new
{
    result.ExitCode,
    journal = result.Journal
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return 0;
