using System.Net.Http;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Gradients through the operation registry.
///
/// AGENTS.md §1.1: everything a person can do with a gradient must be reachable through the one
/// <c>EditorOperations</c> registry, and everything in the registry must be reachable by a person.
/// The Gradient panel already drives the model; these pin the other half - that a driver can read
/// a gradient, change any part of it, and read back what it actually paints.
/// </summary>
public class GradientOperationTests
{
    /// <summary>Everything the Gradient panel can do, as an operation.</summary>
    private static readonly string[] GradientOperations =
    {
        "gradient.get", "gradient.setKind", "gradient.setSpread", "gradient.setStops",
        "gradient.addStop", "gradient.removeStop", "gradient.moveStop", "gradient.setStop",
        "gradient.reverse", "gradient.setGeometry", "gradient.solid", "gradient.remove",
        "gradient.sample",
    };

    /// <summary>A document with one selected rectangle, and a context to run operations in.</summary>
    private static (AutomationContext Context, PathItem Path) OnePath()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        PathItem path = PathFactory.CreateRectangle("rect", new Rect2D(0, 0, 100, 100));
        layer.AddItem(path);

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "selection.set",
            JsonSerializer.SerializeToElement(new { itemIds = new[] { path.Id } }));
        return (context, path);
    }

    private static object? Run(AutomationContext context, string operation, object parameters)
        => EditorOperations.Invoke(context, operation, JsonSerializer.SerializeToElement(parameters));

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    [AvaloniaFact]
    public void EveryGradientCapabilityIsInTheRegistry()
    {
        foreach (string name in GradientOperations)
        {
            Assert.True(EditorOperations.TryGet(name, out _), $"{name} must be registered");
        }
    }

    [AvaloniaFact]
    public void SettingStopsReadsBackThroughTheRegistry()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setStops", new
        {
            stops = new object[]
            {
                new { position = 0.0, color = new[] { 1.0, 0.0, 0.0 } },
                new { position = 0.5, color = new[] { 0.0, 1.0, 0.0 }, opacity = 0.5 },
                new { position = 1.0, color = new[] { 0.0, 0.0, 1.0 } },
            },
        });

        Assert.True(path.Fill.HasGradient);

        JsonElement gradient = Json(Run(context, "gradient.get", new { })).GetProperty("gradient");
        JsonElement stops = gradient.GetProperty("stops");

        Assert.Equal("linear", gradient.GetProperty("kind").GetString());
        Assert.Equal(3, stops.GetArrayLength());
        Assert.Equal(0.5, stops[1].GetProperty("position").GetDouble(), 6);
        Assert.Equal(0.5, stops[1].GetProperty("opacity").GetDouble(), 6);
        Assert.Equal(1.0, stops[1].GetProperty("color").GetProperty("g").GetDouble(), 6);
    }

    [AvaloniaFact]
    public void SwitchingTheKindKeepsTheStops()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setStops", new
        {
            stops = new object[]
            {
                new { position = 0.0, color = new[] { 1.0, 0.0, 0.0 } },
                new { position = 1.0, color = new[] { 0.0, 0.0, 1.0 } },
            },
        });

        Run(context, "gradient.setKind", new { kind = "radial" });

        Assert.Equal(GradientKind.Radial, path.Fill.Gradient!.Kind);
        Assert.Equal(2, path.Fill.Gradient!.Stops.Count);
        Assert.Equal("radial", Json(Run(context, "gradient.get", new { }))
            .GetProperty("gradient").GetProperty("kind").GetString());
    }

    [AvaloniaFact]
    public void ReverseMirrorsTheRamp()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setStops", new
        {
            stops = new object[]
            {
                new { position = 0.0, color = new[] { 1.0, 0.0, 0.0 } },
                new { position = 0.25, color = new[] { 0.0, 1.0, 0.0 } },
                new { position = 1.0, color = new[] { 0.0, 0.0, 1.0 } },
            },
        });

        Run(context, "gradient.reverse", new { });

        IReadOnlyList<GradientStop> stops = path.Fill.Gradient!.Normalised();

        Assert.Equal(0.0, stops[0].Position, 6);
        Assert.Equal(1.0, stops[2].Position, 6);

        // The blue end is now at the start, and the green stop has mirrored to 0.75.
        Assert.Equal(1.0, stops[0].Color.B, 6);
        Assert.Equal(0.75, stops[1].Position, 6);
    }

    [AvaloniaFact]
    public void AddingAStopReturnsTheIndexTheOtherOperationsUse()
    {
        (AutomationContext context, PathItem path) = OnePath();

        JsonElement added = Json(Run(context, "gradient.addStop",
            new { position = 0.5, color = new[] { 1.0, 1.0, 0.0 } }));

        int index = added.GetProperty("index").GetInt32();
        Assert.Equal(1, index);
        Assert.Equal(3, path.Fill.Gradient!.Stops.Count);

        Run(context, "gradient.setStop", new { index, opacity = 0.25 });

        Assert.Equal(0.25, path.Fill.Gradient!.Normalised()[1].Opacity, 6);
    }

    [AvaloniaFact]
    public void RemovingTheLastStopIsRefused()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setStops", new
        {
            stops = new object[] { new { position = 0.0, color = new[] { 1.0, 0.0, 0.0 } } },
        });

        Assert.Throws<EditorOperationException>(() => Run(context, "gradient.removeStop", new { index = 0 }));
        Assert.Single(path.Fill.Gradient!.Stops);
    }

    [AvaloniaFact]
    public void SamplingReportsWhatTheRampPaints()
    {
        (AutomationContext context, _) = OnePath();

        Run(context, "gradient.setStops", new
        {
            stops = new object[]
            {
                new { position = 0.0, color = new[] { 0.0, 0.0, 0.0 } },
                new { position = 1.0, color = new[] { 1.0, 1.0, 1.0 } },
            },
        });

        JsonElement mid = Json(Run(context, "gradient.sample", new { position = 0.5 }));

        Assert.Equal(0.5, mid.GetProperty("color").GetProperty("r").GetDouble(), 3);
        Assert.Equal(1.0, mid.GetProperty("opacity").GetDouble(), 6);
    }

    [AvaloniaFact]
    public void GeometryIsSetAndReadBack()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setGeometry", new
        {
            start = new { x = 0.0, y = 0.0 },
            end = new { x = 0.0, y = 1.0 },
            angle = 45.0,
            radiusX = 0.25,
        });

        Assert.Equal(0.0, path.Fill.Gradient!.Start.X, 6);
        Assert.Equal(0.0, path.Fill.Gradient!.Start.Y, 6);
        Assert.Equal(1.0, path.Fill.Gradient!.End.Y, 6);
        Assert.Equal(45.0, path.Fill.Gradient!.Angle, 6);
        Assert.Equal(0.25, path.Fill.Gradient!.RadiusX, 6);
    }

    [AvaloniaFact]
    public void SolidReplacesTheGradientAndRemoveClearsTheFill()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.solid", new { color = new[] { 0.2, 0.4, 0.6 } });

        Assert.False(path.Fill.HasGradient);
        Assert.True(path.Fill.IsVisible);
        Assert.Equal(0.2, path.Fill.Color.R, 6);

        Run(context, "gradient.remove", new { });

        Assert.False(path.Fill.IsVisible);
    }

    [AvaloniaFact]
    public void AFreeformGradientCarriesItsPointsAndMode()
    {
        (AutomationContext context, PathItem path) = OnePath();

        Run(context, "gradient.setKind", new { kind = "freeform" });
        Run(context, "gradient.setGeometry", new
        {
            freeformMode = "lines",
            points = new object[]
            {
                new { x = 10.0, y = 10.0, color = new[] { 1.0, 0.0, 0.0 } },
                new { x = 80.0, y = 60.0, color = new[] { 0.0, 0.0, 1.0 }, opacity = 0.5 },
            },
            lines = new object[] { new { from = 0, to = 1 } },
        });

        Assert.Equal(GradientKind.Freeform, path.Fill.Gradient!.Kind);
        Assert.Equal(FreeformMode.Lines, path.Fill.Gradient!.FreeformMode);
        Assert.Equal(2, path.Fill.Gradient!.Points.Count);
        Assert.Equal(0.5, path.Fill.Gradient!.Points[1].Opacity, 6);
        Assert.Equal((0, 1), path.Fill.Gradient!.Lines[0]);
    }

    [AvaloniaFact]
    public void AnOperationWithNothingSelectedSaysSo()
    {
        var viewModel = new EditorViewModel();
        var context = new AutomationContext { ViewModel = viewModel };

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => Run(context, "gradient.get", new { }));

        Assert.Contains("No path is selected", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The issue's acceptance: the capability has to work over the real automation endpoint,
    /// changing the document, not only through an in-process call.
    /// </summary>
    [AvaloniaFact]
    public async Task TheGradientSurfaceIsReachableOverTheAutomationEndpoint()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        PathItem path = PathFactory.CreateRectangle("rect", new Rect2D(0, 0, 100, 100));
        layer.AddItem(path);

        AutomationHost host = AutomationHost.Create(
            viewModel, () => null, () => null,
            new VCCad.App.Ai.LlmOptions { ApiKey = "test" }, port: 0, startServer: true);

        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
                Timeout = TimeSpan.FromSeconds(30),
            };

            string body = JsonSerializer.Serialize(new
            {
                op = "gradient.setStops",
                @params = new
                {
                    itemId = path.Id,
                    stops = new object[]
                    {
                        new { position = 0.0, color = new[] { 1.0, 0.0, 0.0 } },
                        new { position = 1.0, color = new[] { 0.0, 0.0, 1.0 } },
                    },
                },
            });

            using HttpResponseMessage response = await http.PostAsync(
                "/api/v1/invoke",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

            // The document really changed, and it changed under the red stop at the near end.
            Assert.True(path.Fill.HasGradient);
            IReadOnlyList<GradientStop> stops = path.Fill.Gradient!.Normalised();
            Assert.Equal(2, stops.Count);
            Assert.Equal(1.0, stops[0].Color.R, 6);

            string readBack = await http.GetStringAsync("/api/v1/operations.txt");
            Assert.Contains("gradient.sample", readBack, StringComparison.Ordinal);
        }
        finally
        {
            host.Server?.Dispose();
        }
    }
}
