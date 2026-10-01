using System.Text.Json;

namespace VCCad.App.Ai;

/// <summary>
/// Where the model endpoint, its key and the model name are configured - and why they are not in this
/// repository.
///
/// An endpoint and an API key are **deployment** values, not source. Baking them in as defaults meant that
/// publishing this repository published the key, and a key that has been published has to be treated as
/// compromised whether or not it is later removed: anybody who fetched the repository while it was public has
/// it, and rewriting history does not take it back. So there is no default key and no default endpoint, and a
/// missing one is reported rather than filled in with a baked-in value.
///
/// The values come from the first of these that answers:
///
/// 1. a command-line argument, which <see cref="LlmOptions"/> applies on top of whatever this produced;
/// 2. an environment variable - <c>VCCAD_LLM_BASE</c>, <c>VCCAD_LLM_KEY</c>, <c>VCCAD_LLM_MODEL</c>;
/// 3. a settings file, <c>settings.json</c> by default, or whatever <c>VCCAD_SETTINGS</c> names. This
///    repository **ignores** that file, and ships <c>settings.example.json</c> to show its shape.
///
/// A settings file is small and forgiving on purpose: an absent file, unreadable JSON or a missing member all
/// mean "not configured here", never an exception on the way to drawing something.
/// </summary>
public static class LlmSettings
{
    /// <summary>The file read when <c>VCCAD_SETTINGS</c> does not name another.</summary>
    public const string DefaultFileName = "settings.json";

    /// <summary>The variable that names the settings file, for a machine that keeps it elsewhere.</summary>
    public const string PathVariable = "VCCAD_SETTINGS";

    private static int _loaded;
    private static string? _baseUrl;
    private static string? _apiKey;
    private static string? _model;

    /// <summary>
    /// One value, resolved in the documented order. The environment wins over the file, because the environment
    /// is what a build machine sets and the file is what a person keeps on their own machine.
    /// </summary>
    public static string Resolve(string variable, string member, string fallback = "")
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        Load();
        string? fromFile = member switch
        {
            "llmBase" => _baseUrl,
            "llmKey" => _apiKey,
            "llmModel" => _model,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(fromFile) ? fallback : fromFile;
    }

    /// <summary>Whether an endpoint and a key are both configured, which is what the assistant needs to run.</summary>
    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Resolve("VCCAD_LLM_BASE", "llmBase"))
           && !string.IsNullOrWhiteSpace(Resolve("VCCAD_LLM_KEY", "llmKey"));

    /// <summary>The file that would be read, for a message that has to name it.</summary>
    public static string CurrentPath
        => Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.CurrentDirectory, DefaultFileName);

    /// <summary>Reads the settings file once. Anything that goes wrong means "nothing configured here".</summary>
    private static void Load()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return;
        }

        try
        {
            string path = CurrentPath;
            if (!File.Exists(path))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _baseUrl = Read(document.RootElement, "llmBase");
            _apiKey = Read(document.RootElement, "llmKey");
            _model = Read(document.RootElement, "llmModel");
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            // Not configured here is not an error: the caller reports a missing endpoint or key itself, with a
            // message that can name the file, which is more use than a stack trace from a settings reader.
        }
    }

    private static string? Read(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
