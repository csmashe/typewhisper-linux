// PluginLoaderTests adds and removes a Trace listener to capture the loader's skip
// diagnostic. Trace.Listeners is process-global, so with xunit's default cross-class
// parallelism any other class loading a plugin writes into that capture — and the
// add/remove races the enumeration those writes do.
//
// Serializing the assembly matches the three sibling test projects and needs no
// [Collection] attribute that a future Trace-touching class could forget to join.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
