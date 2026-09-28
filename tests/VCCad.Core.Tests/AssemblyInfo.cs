using Xunit;

// TextMeasurement.Current is process-wide static state: a test that installs a fixed
// measurer changes what every other test in the process measures. Running these classes
// concurrently made TheInstalledMeasurerDecidesTheWidth pass alone and fail in the suite.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
