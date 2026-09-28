using Xunit;

// The embedded-font registry is process-wide static state, and Avalonia's headless
// font manager is a singleton too, so these tests must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
