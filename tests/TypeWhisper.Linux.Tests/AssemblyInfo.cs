using Xunit;

// Several tests in this assembly mutate process-global state — environment
// variables such as PATH and XDG_CONFIG_HOME (CliInstallServiceTests,
// YdotoolSetupHelperTests, SystemCommandAvailabilityServiceTests, ...). Those
// variables are shared across the whole process, so running such test classes
// in parallel races: one class's PATH override is observed by another class
// mid-test. xunit parallelizes across test classes by default, which makes
// the race real.
//
// Parallelism buys nothing here (the suite runs in well under a second), so
// serialize the whole assembly rather than maintain a fragile web of
// [Collection] attributes that any future env-touching test could forget to
// join — silently reintroducing the race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
