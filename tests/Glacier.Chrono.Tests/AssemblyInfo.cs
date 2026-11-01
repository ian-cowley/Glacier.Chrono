using Xunit;

// Prevent cross-test GC.Collect() pauses and 2-vCPU contention during allocation and timeout assertions.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
