using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Foundry.Api.SourceGenerators
{
    [Generator]
    public class ApiRouteGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            // No initialization needed for simple generators
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // Find additional files ending with api-manifest.json
            var manifestFile = context.AdditionalFiles
                .FirstOrDefault(f => f.Path.EndsWith("api-manifest.json", StringComparison.OrdinalIgnoreCase));

            if (manifestFile == null)
            {
                return;
            }

            var jsonText = manifestFile.GetText()?.ToString();
            if (string.IsNullOrEmpty(jsonText))
            {
                return;
            }

            try
            {
                var ns = ExtractValue(jsonText, "Namespace");
                if (string.IsNullOrEmpty(ns)) ns = "Domain";

                var endpoints = new List<GeneratedEndpoint>();
                var customEndpoints = new List<GeneratedCustomEndpoint>();

                // Parse standard Endpoints
                var endpointsIndex = jsonText.IndexOf("\"Endpoints\"");
                if (endpointsIndex != -1)
                {
                    int start = jsonText.IndexOf("[", endpointsIndex);
                    if (start != -1)
                    {
                        int end = FindClosingBracket(jsonText, start, '[', ']');
                        if (end != -1)
                        {
                            var arrayBlock = jsonText.Substring(start, end - start + 1);
                            int scanIdx = 0;
                            while (true)
                            {
                                int itemStart = arrayBlock.IndexOf("{", scanIdx);
                                if (itemStart == -1) break;
                                int itemEnd = FindClosingBracket(arrayBlock, itemStart, '{', '}');
                                if (itemEnd == -1) break;

                                var itemBlock = arrayBlock.Substring(itemStart, itemEnd - itemStart + 1);
                                var entity = ExtractValue(itemBlock, "Entity");
                                var route = ExtractValue(itemBlock, "Route");
                                if (!string.IsNullOrEmpty(entity) && !string.IsNullOrEmpty(route))
                                {
                                    var methods = ExtractArrayValues(itemBlock, "Methods");
                                    endpoints.Add(new GeneratedEndpoint { Entity = entity, Route = route, Methods = methods });
                                }
                                scanIdx = itemEnd + 1;
                            }
                        }
                    }
                }

                // Parse custom Endpoints
                var customIndex = jsonText.IndexOf("\"CustomEndpoints\"");
                if (customIndex != -1)
                {
                    int start = jsonText.IndexOf("[", customIndex);
                    if (start != -1)
                    {
                        int end = FindClosingBracket(jsonText, start, '[', ']');
                        if (end != -1)
                        {
                            var arrayBlock = jsonText.Substring(start, end - start + 1);
                            int scanIdx = 0;
                            while (true)
                            {
                                int itemStart = arrayBlock.IndexOf("{", scanIdx);
                                if (itemStart == -1) break;
                                int itemEnd = FindClosingBracket(arrayBlock, itemStart, '{', '}');
                                if (itemEnd == -1) break;

                                var itemBlock = arrayBlock.Substring(itemStart, itemEnd - itemStart + 1);
                                var route = ExtractValue(itemBlock, "Route");
                                var method = ExtractValue(itemBlock, "Method");
                                var requestType = ExtractValue(itemBlock, "RequestType");
                                if (!string.IsNullOrEmpty(route) && !string.IsNullOrEmpty(method) && !string.IsNullOrEmpty(requestType))
                                {
                                    var roles = ExtractArrayValues(itemBlock, "Roles");
                                    customEndpoints.Add(new GeneratedCustomEndpoint
                                    {
                                        Route = route,
                                        Method = method,
                                        RequestType = requestType,
                                        Roles = roles
                                    });
                                }
                                scanIdx = itemEnd + 1;
                            }
                        }
                    }
                }

                // Generate MediatR closed generic DI registrations
                var servicesCode = GenerateServicesCode(ns, endpoints);
                context.AddSource("GeneratedServices.g.cs", SourceText.From(servicesCode, Encoding.UTF8));

                // Generate static endpoint mappings
                var endpointsCode = GenerateEndpointsCode(ns, endpoints, customEndpoints);
                context.AddSource("GeneratedEndpoints.g.cs", SourceText.From(endpointsCode, Encoding.UTF8));

                // Generate compile-time filter expression builders
                var filterBuildersCode = GenerateFilterBuildersCode(context.Compilation, ns, endpoints);
                context.AddSource("GeneratedFilterBuilders.g.cs", SourceText.From(filterBuildersCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // Emit build warning on generator failure
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "FNDRYGEN001",
                        "Source Generator Failure",
                        $"Failed to generate routes from manifest: {ex.Message}",
                        "Design",
                        DiagnosticSeverity.Warning,
                        true),
                    Location.None));
            }
        }

        private string GenerateServicesCode(string ns, List<GeneratedEndpoint> endpoints)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using MediatR;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using MongoDB.Bson;");
            sb.AppendLine("using Foundry.Core.Paging;");
            sb.AppendLine("using Foundry.Api.MediatR;");
            sb.AppendLine();
            sb.AppendLine("namespace Foundry.Api.Endpoints;");
            sb.AppendLine();
            sb.AppendLine("public static class GeneratedServices");
            sb.AppendLine("{");
            sb.AppendLine("    public static IServiceCollection AddGeneratedHandlers(this IServiceCollection services)");
            sb.AppendLine("    {");

            foreach (var ep in endpoints)
            {
                var fullEntityType = $"{ns}.{ep.Entity}";
                sb.AppendLine($"        // DI Registrations for {ep.Entity}");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<InsertCommand<{fullEntityType}>, {fullEntityType}>, InsertCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<UpdateCommand<{fullEntityType}>, {fullEntityType}>, UpdateCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<DeleteCommand<{fullEntityType}>, bool>, DeleteCommandHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<GetByIdQuery<{fullEntityType}>, {fullEntityType}?>, GetByIdQueryHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<FindManyQuery<{fullEntityType}>, IReadOnlyList<{fullEntityType}>>, FindManyQueryHandler<{fullEntityType}>>();");
                sb.AppendLine($"        services.AddTransient<IRequestHandler<SearchPagedQuery<{fullEntityType}>, PagedResult<{fullEntityType}>>, SearchPagedQueryHandler<{fullEntityType}>>();");
                sb.AppendLine();
            }

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GenerateEndpointsCode(string ns, List<GeneratedEndpoint> endpoints, List<GeneratedCustomEndpoint> customEndpoints)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Microsoft.AspNetCore.Builder;");
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
            sb.AppendLine("using Microsoft.AspNetCore.Routing;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using System.Text.Json;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using MediatR;");
            sb.AppendLine("using MongoDB.Bson;");
            sb.AppendLine("using Foundry.Api.Manifest;");
            sb.AppendLine("using Foundry.Api.MediatR;");
            sb.AppendLine("using Foundry.Core.Search;");
            sb.AppendLine();
            sb.AppendLine("namespace Foundry.Api.Endpoints;");
            sb.AppendLine();
            sb.AppendLine("public static class GeneratedEndpoints");
            sb.AppendLine("{");
            sb.AppendLine("    public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder endpoints, ApiManifest manifest)");
            sb.AppendLine("    {");

            foreach (var ep in endpoints)
            {
                var fullEntityType = $"{ns}.{ep.Entity}";
                sb.AppendLine($"        // Endpoint Config for {ep.Entity}");
                sb.AppendLine($"        var config_{ep.Entity} = manifest.Endpoints.Find(e => e.Entity == \"{ep.Entity}\")!;");
                sb.AppendLine();

                foreach (var method in ep.Methods)
                {
                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderPost = endpoints.MapPost(\"{ep.Route}\", async ({fullEntityType} entity, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var command = new InsertCommand<{fullEntityType}>(entity);");
                        sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine($"                context.Response.Headers.Location = \"{ep.Route}/\" + ((dynamic)result).Id;");
                        sb.AppendLine($"                return Results.Text(JsonSerializer.Serialize(result), \"application/json\", statusCode: 201);");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderPost, config_{ep.Entity}, \"POST\", typeof({fullEntityType}), 201);");
                    }
                    else if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderPut = endpoints.MapPut(\"{ep.Route}/{{id}}\", async (string id, {fullEntityType} entity, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine("                var updatedEntity = entity with { Id = objectId };");
                        sb.AppendLine($"                var command = new UpdateCommand<{fullEntityType}>(updatedEntity);");
                        sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine($"                return Results.Text(JsonSerializer.Serialize(result), \"application/json\", statusCode: 200);");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderPut, config_{ep.Entity}, \"PUT\", typeof({fullEntityType}), 200);");
                    }
                    else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderDelete = endpoints.MapDelete(\"{ep.Route}/{{id}}\", async (string id, HttpContext context, ISender sender, Foundry.Core.User.ICurrentUserContext userContext) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine($"                var command = new DeleteCommand<{fullEntityType}>(objectId, userContext.OperatorId ?? string.Empty);");
                        sb.AppendLine("                await sender.Send(command, context.RequestAborted);");
                        sb.AppendLine("                return Results.NoContent();");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderDelete, config_{ep.Entity}, \"DELETE\", typeof({fullEntityType}), 204);");
                    }
                    else if (method.Equals("GET_BY_ID", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderGetId = endpoints.MapGet(\"{ep.Route}/{{id}}\", async (string id, HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest(\"Invalid ObjectId.\");");
                        sb.AppendLine($"                var query = new GetByIdQuery<{fullEntityType}>(objectId);");
                        sb.AppendLine("                var result = await sender.Send(query, context.RequestAborted);");
                        sb.AppendLine("                return result != null ? Results.Text(JsonSerializer.Serialize(result), \"application/json\") : Results.NotFound();");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderGetId, config_{ep.Entity}, \"GET_BY_ID\", typeof({fullEntityType}), 200);");
                    }
                    else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"            var builderGet = endpoints.MapGet(\"{ep.Route}\", async (HttpContext context, ISender sender) =>");
                        sb.AppendLine("            {");
                        sb.AppendLine("                var sortBy = context.Request.Query[\"sortBy\"].ToString();");
                        sb.AppendLine("                var limitStr = context.Request.Query[\"limit\"].ToString();");
                        sb.AppendLine("                var limit = int.TryParse(limitStr, out var parsedLimit) ? parsedLimit : 100;");
                        sb.AppendLine("                var sortOrder = string.Equals(context.Request.Query[\"sortOrder\"].ToString(), \"asc\", System.StringComparison.OrdinalIgnoreCase) || string.Equals(context.Request.Query[\"sortOrder\"].ToString(), \"ascending\", System.StringComparison.OrdinalIgnoreCase) ? Foundry.Core.Paging.SortOrder.Ascending : Foundry.Core.Paging.SortOrder.Descending;");
                        sb.AppendLine();
                        sb.AppendLine("                // Advanced Criteria Support");
                        sb.AppendLine("                var criteriaJson = context.Request.Query[\"criteria\"].ToString();");
                        sb.AppendLine("                SearchCriterion[]? criteria = null;");
                        sb.AppendLine("                if (!string.IsNullOrEmpty(criteriaJson))");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    try {");
                        sb.AppendLine("                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };");
                        sb.AppendLine("                        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());");
                        sb.AppendLine("                        criteria = JsonSerializer.Deserialize<SearchCriterion[]>(criteriaJson, options);");
                        sb.AppendLine("                    } catch {}");
                        sb.AppendLine("                }");
                        sb.AppendLine();
                        sb.AppendLine($"                var filterExpr = GeneratedFilterBuilders.BuildFilterExpression<{fullEntityType}>(context) ?? DynamicEndpointRouteBuilder.BuildFilterExpression<{fullEntityType}>(context);");
                        sb.AppendLine($"                var query = new FindManyQuery<{fullEntityType}>(filterExpr, sortBy, sortOrder, limit, criteria);");
                        sb.AppendLine("                var result = await sender.Send(query, context.RequestAborted);");
                        sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result), \"application/json\");");
                        sb.AppendLine("            });");
                        sb.AppendLine($"            ConfigureMetadata(builderGet, config_{ep.Entity}, \"GET\", typeof({fullEntityType}), 200);");
                    }
                }
            }

            // Map custom endpoints at compile-time
            foreach (var customEp in customEndpoints)
            {
                var method = customEp.Method.ToUpperInvariant();
                sb.AppendLine($"            // Custom Endpoint: {customEp.Route} -> {customEp.RequestType}");
                if (method == "GET" || method == "DELETE")
                {
                    sb.AppendLine($"            var builder_{customEp.RequestType} = endpoints.MapMethods(\"{customEp.Route}\", new[] {{ \"{method}\" }}, async (HttpContext context, ISender sender) =>");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                var command = new {ns}.{customEp.RequestType}();");
                    sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                    sb.AppendLine("                if (result == null) return Results.NoContent();");
                    sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result), \"application/json\");");
                    sb.AppendLine("            });");
                }
                else
                {
                    sb.AppendLine($"            var builder_{customEp.RequestType} = endpoints.MapMethods(\"{customEp.Route}\", new[] {{ \"{method}\" }}, async ({ns}.{customEp.RequestType} command, HttpContext context, ISender sender) =>");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var result = await sender.Send(command, context.RequestAborted);");
                    sb.AppendLine("                if (result == null) return Results.NoContent();");
                    sb.AppendLine("                return Results.Text(JsonSerializer.Serialize(result), \"application/json\");");
                    sb.AppendLine("            });");
                }

                sb.AppendLine($"            var config_{customEp.RequestType} = new EndpointConfig");
                sb.AppendLine("            {");
                sb.AppendLine($"                Route = \"{customEp.Route}\",");
                sb.AppendLine($"                Entity = \"{customEp.RequestType}\",");
                sb.AppendLine($"                Methods = new List<string> {{ \"{method}\" }},");
                sb.AppendLine($"                Roles = new Dictionary<string, List<string>> {{ {{ \"{method}\", new List<string> {{ {string.Join(", ", customEp.Roles.Select(r => $"\"{r}\""))} }} }} }}");
                sb.AppendLine("            };");
                sb.AppendLine($"            builder_{customEp.RequestType}.WithMetadata(config_{customEp.RequestType})");
                sb.AppendLine($"                         .WithName(\"{method}_{customEp.RequestType}\")");
                sb.AppendLine($"                         .WithTags(\"{customEp.RequestType}\")");
                sb.AppendLine("                         .Produces(200)");
                sb.AppendLine("                         .Produces(400, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
                sb.AppendLine("                         .Produces(401)");
                sb.AppendLine("                         .Produces(403, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
                sb.AppendLine("                         .Produces(500, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails));");
            }

            sb.AppendLine("        return endpoints;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static void ConfigureMetadata(RouteHandlerBuilder builder, EndpointConfig config, string method, Type entityType, int successStatusCode)");
            sb.AppendLine("    {");
            sb.AppendLine("        var rolesStr = config.Roles != null && config.Roles.TryGetValue(method, out var roles)");
            sb.AppendLine("            ? string.Join(\", \", roles)");
            sb.AppendLine("            : \"Admin\";");
            sb.AppendLine("        string summary = $\"{(method == \"GET_BY_ID\" ? \"Fetch by ID\" : method == \"GET\" ? \"List and Search\" : method == \"POST\" ? \"Insert new\" : method == \"PUT\" ? \"Update existing\" : \"Delete\")} endpoint for {entityType.Name} collection\";");
            sb.AppendLine("        builder.WithMetadata(config)");
            sb.AppendLine("               .WithName($\"{method}_{entityType.Name}\")");
            sb.AppendLine("               .WithTags(entityType.Name)");
            sb.AppendLine("               .WithSummary(summary)");
            sb.AppendLine("               .WithDescription($\"Access {entityType.Name} documents. Requires roles: {rolesStr}\")");
            sb.AppendLine("               .Produces(successStatusCode, entityType)");
            sb.AppendLine("               .Produces(400, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
            sb.AppendLine("               .Produces(401)");
            sb.AppendLine("               .Produces(403, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))");
            sb.AppendLine("               .Produces(500, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails));");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static int FindClosingBracket(string text, int startIndex, char openChar, char closeChar)
        {
            int depth = 0;
            for (int i = startIndex; i < text.Length; i++)
            {
                if (text[i] == openChar)
                {
                    depth++;
                }
                else if (text[i] == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static string ExtractValue(string json, string key)
        {
            var search = $"\"{key}\"";
            var idx = json.IndexOf(search);
            if (idx == -1) return string.Empty;
            var colonIdx = json.IndexOf(":", idx + search.Length);
            if (colonIdx == -1) return string.Empty;
            var startQuote = json.IndexOf("\"", colonIdx + 1);
            if (startQuote == -1) return string.Empty;
            var endQuote = json.IndexOf("\"", startQuote + 1);
            if (endQuote == -1) return string.Empty;
            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        private static List<string> ExtractArrayValues(string json, string key)
        {
            var list = new List<string>();
            var search = $"\"{key}\"";
            var idx = json.IndexOf(search);
            if (idx == -1) return list;
            var colonIdx = json.IndexOf(":", idx + search.Length);
            if (colonIdx == -1) return list;
            var startBracket = json.IndexOf("[", colonIdx + 1);
            if (startBracket == -1) return list;
            var endBracket = FindClosingBracket(json, startBracket, '[', ']');
            if (endBracket == -1) return list;

            var arrayContent = json.Substring(startBracket + 1, endBracket - startBracket - 1);
            var items = arrayContent.Split(',');
            foreach (var item in items)
            {
                var trimmed = item.Trim().Trim('"');
                if (!string.IsNullOrEmpty(trimmed))
                {
                    list.Add(trimmed);
                }
            }
            return list;
        }

    private string GenerateFilterBuildersCode(Compilation compilation, string ns, List<GeneratedEndpoint> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Linq.Expressions;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using MongoDB.Bson;");
        sb.AppendLine();
        sb.AppendLine("namespace Foundry.Api.Endpoints;");
        sb.AppendLine();
        sb.AppendLine("public static class GeneratedFilterBuilders");
        sb.AppendLine("{");
        sb.AppendLine("    public static Expression<Func<T, bool>>? BuildFilterExpression<T>(HttpContext context) where T : class");
        sb.AppendLine("    {");
        foreach (var ep in endpoints)
        {
            var fullEntityType = $"{ns}.{ep.Entity}";
            sb.AppendLine($"        if (typeof(T) == typeof({fullEntityType}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            return (Expression<Func<T, bool>>?)(object?)Build_{ep.Entity}_Filter(context);");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var ep in endpoints)
        {
            var fullEntityType = $"{ns}.{ep.Entity}";
            sb.AppendLine($"    private static Expression<Func<{fullEntityType}, bool>>? Build_{ep.Entity}_Filter(HttpContext context)");
            sb.AppendLine("    {");
            sb.AppendLine("        var query = context.Request.Query;");
            sb.AppendLine("        if (query.Count == 0) return null;");
            sb.AppendLine();
            sb.AppendLine($"        var parameter = Expression.Parameter(typeof({fullEntityType}), \"x\");");
            sb.AppendLine("        Expression? body = null;");
            sb.AppendLine();

            var typeSymbol = compilation.GetTypeByMetadataName(fullEntityType);
            if (typeSymbol != null)
            {
                var properties = typeSymbol.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsWriteOnly);

                foreach (var prop in properties)
                {
                    var propName = prop.Name;
                    var propTypeStr = prop.Type.ToDisplayString();

                    if (propName == "Id" || propName == "CreatedAtUtc" || propName == "UpdatedAtUtc" || propName == "Version" || propName == "IsDeleted")
                        continue;

                    sb.AppendLine($"        if (query.TryGetValue(\"{propName}\", out var val_{propName}))");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var stringVal = val_{propName}.ToString();");

                    if (propTypeStr == "string" || propTypeStr == "System.String")
                    {
                        sb.AppendLine($"            var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"            var valExpr = Expression.Constant(stringVal, typeof(string));");
                        sb.AppendLine("            var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("            body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                    }
                    else if (propTypeStr == "MongoDB.Bson.ObjectId" || propTypeStr == "ObjectId")
                    {
                        sb.AppendLine($"            if (MongoDB.Bson.ObjectId.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(MongoDB.Bson.ObjectId));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "int" || propTypeStr == "System.Int32")
                    {
                        sb.AppendLine($"            if (int.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(int));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "decimal" || propTypeStr == "System.Decimal")
                    {
                        sb.AppendLine($"            if (decimal.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(decimal));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (propTypeStr == "bool" || propTypeStr == "System.Boolean")
                    {
                        sb.AppendLine($"            if (bool.TryParse(stringVal, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof(bool));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    else if (prop.Type.TypeKind == TypeKind.Enum)
                    {
                        sb.AppendLine($"            if (Enum.TryParse<{propTypeStr}>(stringVal, true, out var parsed_{propName}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                var propExpr = Expression.Property(parameter, \"{propName}\");");
                        sb.AppendLine($"                var valExpr = Expression.Constant(parsed_{propName}, typeof({propTypeStr}));");
                        sb.AppendLine("                var eqExpr = Expression.Equal(propExpr, valExpr);");
                        sb.AppendLine("                body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);");
                        sb.AppendLine("            }");
                    }
                    sb.AppendLine("        }");
                }
            }

            sb.AppendLine();
            sb.AppendLine("        if (body == null) return null;");
            sb.AppendLine($"        return Expression.Lambda<Func<{fullEntityType}, bool>>(body, parameter);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    }

    internal class GeneratedEndpoint
    {
        public string Entity { get; set; } = string.Empty;
        public string Route { get; set; } = string.Empty;
        public List<string> Methods { get; set; } = new();
    }

    internal class GeneratedCustomEndpoint
    {
        public string Route { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
    }
}
