using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Chunking;
using Neadocs.Engine.Infrastructure.Http;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Evaluation;
using Neadocs.Engine.Infrastructure.Retrieval;
using Neadocs.Engine.Infrastructure.Security;
using Neadocs.Engine.Infrastructure.Serialization;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using OpenTelemetry.Exporter;
using Serilog.Sinks.OpenTelemetry;

const string ServiceName = "Neadocs.Engine";
const string ServiceVersion = "1.0";

if (args.Length > 0 && args[0] == "--healthcheck")
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

IConfigurationSection engineSection = builder.Configuration.GetSection("DocumentEngine");
builder.Services.Configure<DocumentEngineOptions>(engineSection);

DocumentEngineOptions engineOptions = engineSection.Get<DocumentEngineOptions>() ?? new DocumentEngineOptions();

DocumentEngineOptionsValidator.ThrowIfInvalid(engineOptions);

SchemaTables schemaTables = new(engineOptions);
ApiKeyValidator apiKeyValidator = new(engineOptions);

NeadocsMeters.SetBuildInfo(ServiceVersion, schemaTables.Name);

IReadOnlyDictionary<string, LoadedRuleSet> ruleSets =
    RuleSetLoader.Load(engineOptions.Text.NormalizersDirectory);
NormalizerRegistry normalizers = new(ruleSets);

string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

LoggerConfiguration logConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service.name", ServiceName)
    .WriteTo.Console(new CompactJsonFormatter());

if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    logConfiguration = logConfiguration.WriteTo.OpenTelemetry(otlp =>
    {
        otlp.Endpoint = otlpEndpoint;
        otlp.Protocol = ResolveSerilogProtocol(otlpEndpoint);
        otlp.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = ServiceVersion,
            ["deployment.environment"] = builder.Environment.EnvironmentName,
        };
    });
}

Log.Logger = logConfiguration.CreateLogger();
builder.Host.UseSerilog(Log.Logger, dispose: true);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = engineOptions.MaxRequestBodyBytes;
    kestrel.AddServerHeader = false;
});

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, NeadocsJsonContext.Default);
});

builder.Services.AddCors(cors => cors.AddPolicy("neadocs", policy =>
{
    if (string.IsNullOrWhiteSpace(engineOptions.CorsAllowedOrigins))
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        policy.WithOrigins(engineOptions.CorsAllowedOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    policy.WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Content-Type", "Authorization", ApiKeyValidator.HeaderName, CorrelationId.HeaderName)
        .WithExposedHeaders(CorrelationId.HeaderName)
        .SetPreflightMaxAge(TimeSpan.FromHours(24));
}));

if (!string.IsNullOrWhiteSpace(engineOptions.JwtSymmetricKey))
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(jwt =>
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(engineOptions.JwtSymmetricKey)),
                ClockSkew = TimeSpan.FromSeconds(engineOptions.JwtClockSkewSeconds),
            };
        });
}
else
{
    builder.Services.AddAuthentication();
}

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        TenantResolutionMiddleware.IsAnonymous(context.Request.Path)
            ? RateLimitPartition.GetNoLimiter("exempt")
            : RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKey.For(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = engineOptions.RateLimitPermitCount,
                    Window = TimeSpan.FromSeconds(engineOptions.RateLimitWindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = engineOptions.RateLimitQueueSize,
                }));

    limiter.OnRejected = async (context, cancellationToken) =>
    {
        await Problem.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "Too Many Requests",
            $"This credential exceeded {engineOptions.RateLimitPermitCount} requests per "
            + $"{engineOptions.RateLimitWindowSeconds}s.");
    };
});

builder.Services.AddSingleton(schemaTables);
builder.Services.AddSingleton(apiKeyValidator);
builder.Services.AddSingleton(normalizers);
builder.Services.AddSingleton(new SynonymExpander(engineOptions.Text, normalizers));
builder.Services.AddSingleton(new EmbeddingModelRegistry(engineOptions));
builder.Services.AddSingleton<VectorTypeInfo>();
builder.Services.AddSingleton<MigrationState>();
builder.Services.AddSingleton<NpgsqlDataSourceFactory>();
builder.Services.AddSingleton<PostgresSchemaMigrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresSchemaMigrator>());
builder.Services.AddSingleton<MarkdownChunker>();
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<DocumentReader>();
builder.Services.AddSingleton<LexicalSearch>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<EmbeddingChain>();
builder.Services.AddSingleton<EmbeddingStore>();
builder.Services.AddSingleton<VectorSearch>();
builder.Services.AddSingleton<ChunkDetailReader>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<ReindexService>();
builder.Services.AddSingleton<EvalRunner>();
builder.Services.AddHostedService<EmbeddingBacklogWorker>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: ServiceName,
            serviceVersion: ServiceVersion,
            serviceInstanceId: Environment.MachineName)
        .AddAttributes([
            new KeyValuePair<string, object>(
                "deployment.environment",
                builder.Environment.EnvironmentName),
        ]))
    .WithTracing(tracing =>
    {
        foreach (string source in NeadocsActivitySources.All)
        {
            tracing.AddSource(source);
        }

        tracing.AddAspNetCoreInstrumentation();
        tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(NeadocsMeters.MeterName);
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddOtlpExporter();

        if (engineOptions.EnablePrometheusScrape)
        {
            metrics.AddPrometheusExporter();
        }
    });

WebApplication app = builder.Build();

await app.Services.GetRequiredService<EmbeddingChain>().ProbeDimensionsAsync(CancellationToken.None);

if (CommandLine.Handles(args))
{
    await app.Services.GetRequiredService<PostgresSchemaMigrator>().MigrateAsync(CancellationToken.None);

    return await CommandLine.RunAsync(args, app.Services, CancellationToken.None);
}

app.Use(async (HttpContext context, RequestDelegate next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next(context);
});

app.UseNeadocsCorrelationId();
app.UseCors("neadocs");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseNeadocsTenantResolution();

if (engineOptions.EnablePrometheusScrape)
{
    app.MapPrometheusScrapingEndpoint();
}

app.MapNeadocs();

app.MapGet("/health", () => Results.Ok(new StatusResponse("ok")));

app.MapGet("/ready", async (
    HttpContext context,
    NpgsqlDataSourceFactory connections,
    MigrationState migrationState) =>
{
    if (!migrationState.Completed)
    {
        return Problem.Result(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Not Ready",
            "The schema migration has not completed.");
    }

    bool reachable = await connections.CanConnectAsync(context.RequestAborted);

    return reachable
        ? Results.Ok(new StatusResponse("ready"))
        : Problem.Result(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Not Ready",
            "Postgres did not answer.");
});

try
{
    Log.Information(
        "{ServiceName} starting. Schema {Schema}, {LocaleCount} locale(s), {ModelCount} embedding "
        + "model(s), rule sets [{RuleSetTags}], normalization {NormalizationSupport}.",
        ServiceName,
        schemaTables.Name,
        engineOptions.Text.Locales.Count,
        engineOptions.EmbeddingModels.Count,
        string.Join(", ", normalizers.Tags),
        TextRuntime.SupportsNormalization ? "available" : "unavailable (folding is explicit)");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{ServiceName} refused to start.", ServiceName);
    throw;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

static OtlpProtocol ResolveSerilogProtocol(string endpoint)
{
    string? configured = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")?.Trim();

    if (string.Equals(configured, "grpc", StringComparison.OrdinalIgnoreCase))
    {
        return OtlpProtocol.Grpc;
    }

    if (string.Equals(configured, "http/protobuf", StringComparison.OrdinalIgnoreCase))
    {
        return OtlpProtocol.HttpProtobuf;
    }

    return endpoint.Contains(":4317", StringComparison.Ordinal)
        ? OtlpProtocol.Grpc
        : OtlpProtocol.HttpProtobuf;
}

public partial class Program
{
}
