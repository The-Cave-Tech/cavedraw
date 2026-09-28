# =============================================================================
# VCCad - Dockerfile
#
# Multi-stage build:
#   build   - restore, build, and run the full test suite (SDK image)
#   wasm    - publish the Avalonia WebAssembly editor
#   final   - publish the API and merge the wasm wwwroot for static serving
#
# Runtime image is Ubuntu-based .NET 8 ASP.NET and runs as a non-root user.
# Build and run on the docker host:  ssh user@host (see project plan §6).
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Emscripten (from the wasm-tools workload) requires python on PATH.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && ln -sf /usr/bin/python3 /usr/local/bin/python \
 && rm -rf /var/lib/apt/lists/*

# Restore first (cache-friendly layer). wasm-tools (emscripten) must be
# present before restore because the browser project enables WasmBuildNative
# to link the SkiaSharp/HarfBuzz native stubs.
COPY VCCad.sln Directory.Build.props ./
COPY src/VCCad.Geometry/VCCad.Geometry.csproj src/VCCad.Geometry/
COPY src/VCCad.Core/VCCad.Core.csproj src/VCCad.Core/
COPY src/VCCad.Pdf/VCCad.Pdf.csproj src/VCCad.Pdf/
COPY src/VCCad.Api/VCCad.Api.csproj src/VCCad.Api/
COPY src/VCCad.App/VCCad.App.csproj src/VCCad.App/
COPY src/VCCad.App.Browser/VCCad.App.Browser.csproj src/VCCad.App.Browser/
COPY tests/VCCad.Geometry.Tests/VCCad.Geometry.Tests.csproj tests/VCCad.Geometry.Tests/
COPY tests/VCCad.Core.Tests/VCCad.Core.Tests.csproj tests/VCCad.Core.Tests/
COPY tests/VCCad.Pdf.Tests/VCCad.Pdf.Tests.csproj tests/VCCad.Pdf.Tests/
COPY tests/VCCad.Api.Integration.Tests/VCCad.Api.Integration.Tests.csproj tests/VCCad.Api.Integration.Tests/
COPY tests/VCCad.App.Tests/VCCad.App.Tests.csproj tests/VCCad.App.Tests/
RUN dotnet workload install wasm-tools \
 && dotnet restore VCCad.sln

# Full source for build + test.
COPY . .
RUN dotnet build VCCad.sln -c Release --no-restore

# Gate: run every test project before producing images.
RUN dotnet test tests/VCCad.Geometry.Tests -c Release --no-build --no-restore --logger "console;verbosity=minimal" \
 && dotnet test tests/VCCad.Core.Tests -c Release --no-build --no-restore --logger "console;verbosity=minimal" \
 && dotnet test tests/VCCad.Pdf.Tests -c Release --no-build --no-restore --logger "console;verbosity=minimal" \
 && dotnet test tests/VCCad.Api.Integration.Tests -c Release --no-build --no-restore --logger "console;verbosity=minimal"

# Publish the API host (its wwwroot is filled from the wasm output below).
RUN dotnet publish src/VCCad.Api -c Release -o /app/api --no-restore

# Publish the WebAssembly editor. Runs in the same stage as the restore so the
# NuGet cache is present; the wasm-tools workload (emscripten) is required to
# link the SkiaSharp/HarfBuzz native stubs (WasmBuildNative=true).
RUN dotnet workload install wasm-tools \
 && dotnet publish src/VCCad.App.Browser -c Release -o /app/www --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN useradd --create-home --uid 10001 vccad

COPY --from=build /app/api ./
COPY --from=build /app/www/wwwroot ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_NOLOGO=1
EXPOSE 8080

USER vccad
ENTRYPOINT ["dotnet", "VCCad.Api.dll"]
