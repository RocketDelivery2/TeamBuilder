using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Api.Hosting;

/// <summary>
/// OpenAPI documents. The app's own API (<c>v1</c>) stays a Development convenience behind
/// Swagger:Enabled. The public integration API (<see cref="DocumentName"/>) is a published
/// contract, so its document is served at <see cref="DocumentPath"/> in every environment,
/// holds only the <c>/api/integration/v1</c> operations and declares the API key scheme.
/// </summary>
public static class IntegrationOpenApi
{
    public const string DocumentName = "integration-v1";
    public const string DocumentPath = "/api/integration/v1/openapi.json";
    private const string SecuritySchemeName = "ApiKey";

    public static void Configure(SwaggerGenOptions options)
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "TeamBuilder API", Version = "v1" });
        options.SwaggerDoc(DocumentName, new OpenApiInfo
        {
            Title = "TeamBuilder Integration API",
            Version = "1.0",
            Description =
                $"Read-only access for other applications to the games an API key's owner hosts, in the {IntegrationRosterDto.SchemaId} contract. " +
                $"Create a key in TeamBuilder (POST /api/v1/players/me/api-keys) and send it in the {IntegrationApiKeyAuthentication.HeaderName} header. " +
                "Guide: docs/integration-api.md."
        });

        // Each operation belongs to exactly one document.
        options.DocInclusionPredicate((document, api) =>
            document == DocumentName ? api.GroupName == DocumentName : api.GroupName != DocumentName);
        options.DocumentFilter<ApiKeySecurityFilter>();

        foreach (var assembly in new[] { typeof(IntegrationOpenApi).Assembly, typeof(IntegrationRosterDto).Assembly })
        {
            var xml = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
            if (File.Exists(xml))
                options.IncludeXmlComments(xml, includeControllerXmlComments: true);
        }
    }

    /// <summary>
    /// Serves the integration document as OpenAPI 3.0 JSON, anonymously, in every environment. The
    /// document cannot change while the app runs, so it is generated once, on first request.
    /// </summary>
    public static IEndpointConventionBuilder MapIntegrationOpenApi(this IEndpointRouteBuilder endpoints)
    {
        string? json = null;
        return endpoints.MapGet(DocumentPath, async (ISwaggerProvider provider, HttpContext context) =>
            {
                json ??= Serialize(provider.GetSwagger(DocumentName));
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(json, context.RequestAborted);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static string Serialize(OpenApiDocument document)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        document.SerializeAsV3(new OpenApiJsonWriter(writer));
        return writer.ToString();
    }

    private sealed class ApiKeySecurityFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument document, DocumentFilterContext context)
        {
            if (context.DocumentName != DocumentName)
                return;

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = IntegrationApiKeyAuthentication.HeaderName,
                Description = "An integration API key (tbk_...). Keys are created and revoked by their owner in TeamBuilder."
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = [] }];
        }
    }
}
