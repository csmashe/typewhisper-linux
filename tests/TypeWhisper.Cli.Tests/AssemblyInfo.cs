using Xunit;

// Mirrors TypeWhisper.Linux.Tests: several classes here mutate process-global
// state — XDG_CONFIG_HOME (DiscoveryFileReaderTests, ProgramDispatchTests) and
// Console.Out/Console.Error (UsageTextTests, ProgramDispatchTests). xunit
// parallelizes across test classes by default, so one class's discovery file or
// redirected console is observed by another mid-test.
//
// The suite runs in well under a second, so serialize the assembly rather than
// maintain [Collection] attributes that a future env-touching test could forget
// to join — silently reintroducing the race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
