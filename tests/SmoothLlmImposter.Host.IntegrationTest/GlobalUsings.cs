global using Shouldly;
global using Xunit;

// StreamingDisconnectTests asserts on the process-global Serilog Log.Logger (where the request-logging
// middleware surfaces an escaping streaming exception). Run integration tests serially so that global is
// not clobbered by another host build mid-test. The suite is small (~1s), so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
