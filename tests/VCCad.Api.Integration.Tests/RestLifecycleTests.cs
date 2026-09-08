using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Api.Integration.Tests;

[Collection("api")]
public class RestLifecycleTests : ApiTestBase
{
    public RestLifecycleTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task HealthIsOk()
    {
        using HttpClient client = Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateReturnsDefaultA4LandscapeDocument()
    {
        using HttpClient client = Factory.CreateClient();
        using HttpResponseMessage created = await client.PostAsync(
            "/api/v1/documents", JsonContent.Create(new { name = "rest-fixture" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument summary = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string id = summary.RootElement.GetProperty("id").GetString()!;
        Assert.NotNull(id);

        // Fetch the canonical model and verify the A4-landscape default.
        using HttpResponseMessage get = await client.GetAsync($"/api/v1/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using JsonDocument model = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        JsonElement artboard = model.RootElement.GetProperty("Artboards")[0];
        Assert.Equal(841.8897637795276, artboard.GetProperty("Width").GetDouble(), 6);
        Assert.Equal(595.2755905511812, artboard.GetProperty("Height").GetDouble(), 6);
        Assert.Equal(1, artboard.GetProperty("Layers").GetArrayLength());
    }

    [Fact]
    public async Task PdfExportIsDownloadedAndRoundTrips()
    {
        using HttpClient client = Factory.CreateClient();
        HttpResponseMessage created = await client.PostAsync(
            "/api/v1/documents", JsonContent.Create(new { name = "pdf-fixture" }));
        string id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage pdf = await client.GetAsync($"/api/v1/documents/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("pdf-fixture.pdf", pdf.Content.Headers.ContentDisposition?.FileNameStar
                     ?? pdf.Content.Headers.ContentDisposition?.FileName);
        byte[] bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-1.7", System.Text.Encoding.ASCII.GetString(bytes, 0, 9));

        // The bytes must carry the lossless sidecar: our reader restores the doc.
        byte[] sidecar = VCCad.Pdf.PdfSidecarReader.ExtractSidecar(bytes);
        Assert.NotEmpty(sidecar);
    }

    [Fact]
    public async Task DeleteRemovesAndSubsequentGetIs404()
    {
        using HttpClient client = Factory.CreateClient();
        HttpResponseMessage created = await client.PostAsync(
            "/api/v1/documents", JsonContent.Create(new { name = "temp" }));
        string id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage deleted = await client.DeleteAsync($"/api/v1/documents/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage gone = await client.GetAsync($"/api/v1/documents/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task UpsertRoundTripsTheCanonicalModel()
    {
        // Build a document locally, push it with PUT, and confirm the server's
        // GET returns byte-identical canonical JSON (save/load contract).
        CadDocument original = CadDocument.CreateDefault("upsert-doc");
        original.Artboards[0].Layers[0].AddItem(
            VCCad.Core.Model.PathFactory.CreateEllipse("dot", new Geometry.Point2D(50, 50), 20, 10));
        string payload = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(original);

        using HttpClient client = Factory.CreateClient();
        using var putContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage put = await client.PutAsync($"/api/v1/documents/{original.Id}", putContent);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using HttpResponseMessage get = await client.GetAsync($"/api/v1/documents/{original.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        string fetched = await get.Content.ReadAsStringAsync();
        Assert.Equal(payload, fetched);
    }

    [Fact]
    public async Task MissingDocumentReturns404Json()
    {
        using HttpClient client = Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/documents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}

internal static class JsonContent
{
    public static HttpContent Create(object value)
        => new StringContent(
            JsonSerializer.Serialize(value),
            new MediaTypeHeaderValue("application/json"));
}
