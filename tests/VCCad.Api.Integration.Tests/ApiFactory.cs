using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VCCad.Api.Integration.Tests;

/// <summary>
/// Hosts the real <c>VCCad.Api</c> application in-process so integration tests
/// exercise the actual HTTP pipeline (routing, middleware, model binding, DI).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public ApiFactory()
    {
        // Point the content root at the Api project so webroot/static resolution
        // matches the app's own layout (the published editor is not required).
        string assemblyDir = AppContext.BaseDirectory;
        string? projectDir = FindProjectDirectory(assemblyDir, "VCCad.Api.csproj");
        if (projectDir is not null)
        {
            _contentRoot = projectDir;
        }
    }

    private readonly string? _contentRoot;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_contentRoot is not null)
        {
            builder.UseContentRoot(_contentRoot);
        }
    }

    /// <summary>Walks up from the test binaries looking for the named csproj file.</summary>
    private static string? FindProjectDirectory(string start, string fileName)
    {
        DirectoryInfo? dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, fileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>Shared collection so every integration test reuses one host instance.</summary>
[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;

[Collection("api")]
public abstract class ApiTestBase
{
    protected ApiFactory Factory { get; }

    protected ApiTestBase(ApiFactory factory)
    {
        Factory = factory;
    }
}
