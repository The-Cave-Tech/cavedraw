using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `filter.list` reports every parameter a step's kind declares, not a fixed six (issue #241).
///
/// `filter.setPrimitiveParameter` can set any parameter `filter.kinds` declares, and `filter.list` reported
/// `radius`, `dx`, `dy`, `floodOpacity`, `op` and `mode` whichever kind the step was. So a turbulence's `seed`,
/// a colour matrix's `values`, a displacement's `scale` and a light's `azimuth` could be written and **never read
/// back**. A parameter that can be set and cannot be read is a state a driver can put the editor into and never
/// check - the half of capability parity that is easy to miss, because the operation succeeds.
///
/// The last test here is the one that keeps it from coming back: it compares what the declaration says each kind
/// takes, for every kind in the build, with what the readout gives - so a parameter added to the declaration
/// without a reader fails here rather than in a driver's hands.
/// </summary>
public class FilterListParametersTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host() => new() { ViewModel = new EditorViewModel() };

    private static JsonElement Invoke(AutomationContext context, string operation, object parameters)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, operation, Params(parameters)));

    /// <summary>The first primitive of the named filter, as filter.list reports it.</summary>
    private static JsonElement FirstPrimitive(AutomationContext context, string filter)
        => Invoke(context, "filter.list", new { }).EnumerateArray()
            .First(entry => entry.GetProperty("name").GetString() == filter)
            .GetProperty("primitives").EnumerateArray().First();

    private static JsonElement ParametersOf(AutomationContext context, string filter)
        => FirstPrimitive(context, filter).GetProperty("parameters");

    [Fact]
    public void ATurbulenceReportsTheSeedItWasGivenAndTheSeedItIsGiven()
    {
        AutomationContext context = Host();
        Invoke(context, "filter.create", new
        {
            name = "grain",
            primitives = new[] { new { kind = "turbulence", baseFrequency = 0.05, numOctaves = 3, seed = 1.0 } },
        });

        JsonElement before = ParametersOf(context, "grain");
        Assert.Equal(1, before.GetProperty("seed").GetInt32());
        Assert.Equal(3, before.GetProperty("numOctaves").GetInt32());
        Assert.Equal(0.05, before.GetProperty("baseFrequency").GetDouble(), 4);

        Invoke(context, "filter.setPrimitiveParameter",
            new { name = "grain", index = 0, parameter = "seed", value = 7.0 });

        JsonElement after = ParametersOf(context, "grain");
        Assert.Equal(7, after.GetProperty("seed").GetInt32());
    }

    [Fact]
    public void AColourMatrixReportsItsValuesAndType()
    {
        AutomationContext context = Host();
        Invoke(context, "filter.create", new
        {
            name = "grey",
            primitives = new[] { new { kind = "colorMatrix", type = "saturate", values = new[] { 0.25 } } },
        });

        JsonElement parameters = ParametersOf(context, "grey");
        Assert.Equal("saturate", parameters.GetProperty("type").GetString());
        Assert.Equal(0.25, parameters.GetProperty("values").EnumerateArray().First().GetDouble(), 4);
    }

    [Fact]
    public void AnOffsetReportsBothDistances()
    {
        AutomationContext context = Host();
        Invoke(context, "filter.create", new
        {
            name = "move",
            primitives = new[] { new { kind = "offset", dx = 12.0, dy = -4.0 } },
        });

        JsonElement parameters = ParametersOf(context, "move");
        Assert.Equal(12.0, parameters.GetProperty("dx").GetDouble(), 4);
        Assert.Equal(-4.0, parameters.GetProperty("dy").GetDouble(), 4);
    }

    /// <summary>
    /// The declaration and the readout agree, for **every** kind this build has: what `filter.kinds` says a kind
    /// takes (leaving out the buffers, which are wiring and reported on their own) is exactly what `filter.list`
    /// reports for a step of that kind. A parameter added to the declaration without a reader fails here.
    /// </summary>
    [Fact]
    public void EveryKindReportsEveryParameterItsDeclarationNames()
    {
        AutomationContext context = Host();
        JsonElement kinds = Invoke(context, "filter.kinds", new { });

        foreach (JsonElement definition in kinds.EnumerateArray())
        {
            string kind = definition.GetProperty("kind").GetString()!;
            var wanted = new List<string>();
            var primitives = new Dictionary<string, object>();

            foreach (JsonElement parameter in definition.GetProperty("parameters").EnumerateArray())
            {
                string name = parameter.GetProperty("name").GetString()!;
                if (parameter.GetProperty("kind").GetString() == "buffer")
                {
                    continue;
                }

                wanted.Add(name);

                // Required values only; anything else takes the kind's own default, which is the case a readout
                // should also report.
                if (parameter.GetProperty("required").GetBoolean())
                {
                    primitives[name] = ValueFor(parameter);
                }
            }

            primitives["kind"] = kind;
            Invoke(context, "filter.create", new { name = $"f-{kind}", primitives = new[] { primitives } });

            JsonElement reported = ParametersOf(context, $"f-{kind}");
            string[] expected = [.. wanted.Order(StringComparer.OrdinalIgnoreCase)];
            string[] actual = [.. reported.EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase)];

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>A required value of the right shape for a declared parameter.</summary>
    private static object ValueFor(JsonElement parameter)
    {
        string kind = parameter.GetProperty("kind").GetString()!;
        if (parameter.TryGetProperty("choices", out JsonElement choices) &&
            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            return choices[0].GetString()!;
        }

        return kind switch
        {
            "array" => new[] { 1.0 },
            "colour" or "color" => new[] { 0, 0, 0 },
            "string" => "turbulence",
            _ => 1.0,
        };
    }
}
