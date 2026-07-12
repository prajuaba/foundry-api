using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using MongoDB.Bson;
using FoundryMongo.Domain.Entities;
using FoundryMongo.Domain.Search;
using FoundryMongo.Domain.Paging;
using Foundry.Api.Manifest;
using Foundry.Api.MediatR;

namespace Foundry.Api.Endpoints;

public static class DynamicEndpointRouteBuilder
{
    public static IEndpointRouteBuilder MapDynamicEndpoints(this IEndpointRouteBuilder endpoints, ApiManifest manifest)
    {
        if (manifest == null || manifest.Endpoints == null) return endpoints;

        // Retrieve the entity types from loaded assemblies that match the manifest Namespace
        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        foreach (var config in manifest.Endpoints)
        {
            // Find Entity Type matching the configured name and namespace
            var entityTypeName = $"{manifest.Namespace}.{config.Entity}";
            var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);

            if (entityType == null)
            {
                // Fallback to simple name match if namespace doesn't fully match
                entityType = allTypes.FirstOrDefault(t => t.Name.Equals(config.Entity, StringComparison.OrdinalIgnoreCase) == true 
                    && typeof(IEntity<ObjectId>).IsAssignableFrom(t));
            }

            if (entityType == null)
            {
                Console.WriteLine($"[Warning] Entity type '{config.Entity}' could not be resolved. Skipping route mapping for '{config.Route}'.");
                continue;
            }

            foreach (var method in config.Methods)
            {
                var upperMethod = method.ToUpperInvariant();
                switch (upperMethod)
                {
                    case "POST":
                        MapCreateRoute(endpoints, config, entityType);
                        break;
                    case "PUT":
                        MapUpdateRoute(endpoints, config, entityType);
                        break;
                    case "DELETE":
                        MapDeleteRoute(endpoints, config, entityType);
                        break;
                    case "GET_BY_ID":
                        MapGetByIdRoute(endpoints, config, entityType);
                        break;
                    case "GET":
                        MapGetManyRoute(endpoints, config, entityType);
                        break;
                }
            }
        }

        // Map custom endpoints dynamically via reflection
        if (manifest.CustomEndpoints != null)
        {
            foreach (var customConfig in manifest.CustomEndpoints)
            {
                var requestType = allTypes.FirstOrDefault(t => t.Name.Equals(customConfig.RequestType, StringComparison.OrdinalIgnoreCase) || t.FullName?.Equals(customConfig.RequestType, StringComparison.OrdinalIgnoreCase) == true);
                if (requestType == null)
                {
                    Console.WriteLine($"[Warning] Custom RequestType '{customConfig.RequestType}' could not be resolved. Skipping route mapping.");
                    continue;
                }

                var routeBuilder = endpoints.MapMethods(customConfig.Route, new[] { customConfig.Method.ToUpperInvariant() }, async (HttpContext context, ISender sender) =>
                {
                    var requestBody = await JsonSerializer.DeserializeAsync(context.Request.Body, requestType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (requestBody == null) return Results.BadRequest("Invalid request body.");

                    var sendMethod = GetSendMethod();
                    var requestInterface = requestType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));
                    var responseType = requestInterface != null ? requestInterface.GetGenericArguments()[0] : typeof(object);

                    var responseTask = (Task)sendMethod.MakeGenericMethod(responseType)
                        .Invoke(sender, new[] { requestBody, context.RequestAborted })!;

                    await responseTask;
                    var result = ((dynamic)responseTask).Result;

                    if (result == null) return Results.NoContent();
                    return Results.Text(JsonSerializer.Serialize(result), "application/json");
                });

                var endpointMeta = new EndpointConfig
                {
                    Route = customConfig.Route,
                    Entity = customConfig.RequestType,
                    Methods = new List<string> { customConfig.Method.ToUpperInvariant() },
                    Roles = new Dictionary<string, List<string>> { { customConfig.Method.ToUpperInvariant(), customConfig.Roles } }
                };
                routeBuilder.WithMetadata(endpointMeta)
                            .WithName($"{customConfig.Method.ToUpperInvariant()}_{customConfig.RequestType}");
            }
        }

        return endpoints;
    }

    private static void MapCreateRoute(IEndpointRouteBuilder endpoints, EndpointConfig config, Type entityType)
    {
        var route = config.Route;
        var builder = endpoints.MapPost(route, async (HttpContext context, ISender sender) =>
        {
            var entity = await JsonSerializer.DeserializeAsync(context.Request.Body, entityType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entity == null)
            {
                return Results.BadRequest("Invalid body or entity serialization failed.");
            }

            var commandType = typeof(InsertCommand<>).MakeGenericType(entityType);
            var command = Activator.CreateInstance(commandType, entity);
            if (command == null) return Results.Problem("Failed to create Command instance.");

            var responseTask = (Task)GetSendMethod()
                .MakeGenericMethod(entityType)
                .Invoke(sender, new[] { command, context.RequestAborted })!;

            await responseTask;
            var result = ((dynamic)responseTask).Result;

            var idProperty = entityType.GetProperty("Id");
            var id = idProperty?.GetValue(result)?.ToString() ?? string.Empty;

            context.Response.StatusCode = 201;
            context.Response.Headers.Location = $"{route}/{id}";
            return Results.Text(JsonSerializer.Serialize(result), "application/json");
        });

        ConfigureMetadata(builder, config, "POST", entityType, 201);
    }

    private static void MapUpdateRoute(IEndpointRouteBuilder endpoints, EndpointConfig config, Type entityType)
    {
        var route = $"{config.Route}/{{id}}";
        var builder = endpoints.MapPut(route, async (string id, HttpContext context, ISender sender) =>
        {
            if (!ObjectId.TryParse(id, out var objectId))
            {
                return Results.BadRequest("Invalid ObjectId format.");
            }

            var entity = await JsonSerializer.DeserializeAsync(context.Request.Body, entityType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entity == null)
            {
                return Results.BadRequest("Invalid body or entity serialization failed.");
            }

            // Sync the route parameter id to the entity ID
            var idProperty = entityType.GetProperty("Id");
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(entity, objectId);
            }

            var commandType = typeof(UpdateCommand<>).MakeGenericType(entityType);
            var command = Activator.CreateInstance(commandType, entity);
            if (command == null) return Results.Problem("Failed to create Command instance.");

            var responseTask = (Task)GetSendMethod()
                .MakeGenericMethod(entityType)
                .Invoke(sender, new[] { command, context.RequestAborted })!;

            await responseTask;
            var result = ((dynamic)responseTask).Result;

            return Results.Text(JsonSerializer.Serialize(result), "application/json");
        });

        ConfigureMetadata(builder, config, "PUT", entityType, 200);
    }

    private static void MapDeleteRoute(IEndpointRouteBuilder endpoints, EndpointConfig config, Type entityType)
    {
        var route = $"{config.Route}/{{id}}";
        var builder = endpoints.MapDelete(route, async (string id, ISender sender, FoundryMongo.Domain.Context.ICurrentUserContext userContext, HttpContext context) =>
        {
            if (!ObjectId.TryParse(id, out var objectId))
            {
                return Results.BadRequest("Invalid ObjectId format.");
            }

            var operatorId = userContext.OperatorId ?? "anonymous";
            var commandType = typeof(DeleteCommand<>).MakeGenericType(entityType);
            var command = Activator.CreateInstance(commandType, new object[] { objectId, operatorId });
            if (command == null) return Results.Problem("Failed to create Command instance.");

            var responseTask = (Task<bool>)GetSendMethod()
                .MakeGenericMethod(typeof(bool))
                .Invoke(sender, new[] { command, context.RequestAborted })!;

            var result = await responseTask;
            return result ? Results.NoContent() : Results.NotFound();
        });

        ConfigureMetadata(builder, config, "DELETE", entityType, 24);
    }

    private static void MapGetByIdRoute(IEndpointRouteBuilder endpoints, EndpointConfig config, Type entityType)
    {
        var route = $"{config.Route}/{{id}}";
        var builder = endpoints.MapGet(route, async (string id, ISender sender, HttpContext context) =>
        {
            if (!ObjectId.TryParse(id, out var objectId))
            {
                return Results.BadRequest("Invalid ObjectId format.");
            }

            var queryType = typeof(GetByIdQuery<>).MakeGenericType(entityType);
            var query = Activator.CreateInstance(queryType, objectId);
            if (query == null) return Results.Problem("Failed to create Query instance.");

            // GetByIdQuery returns TEntity?
            var responseTask = (Task)GetSendMethod()
                .MakeGenericMethod(entityType.MakeNullableType())
                .Invoke(sender, new[] { query, context.RequestAborted })!;

            await responseTask;
            var result = ((dynamic)responseTask).Result;

            return result != null ? Results.Text(JsonSerializer.Serialize(result), "application/json") : Results.NotFound();
        });

        ConfigureMetadata(builder, config, "GET_BY_ID", entityType, 200);
    }

    private static void MapGetManyRoute(IEndpointRouteBuilder endpoints, EndpointConfig config, Type entityType)
    {
        var route = config.Route;
        var builder = endpoints.MapGet(route, async (HttpContext context, ISender sender) =>
        {
            // Build dynamic Filter Expression based on query parameters
            var filterMethod = typeof(DynamicEndpointRouteBuilder).GetMethod(nameof(BuildFilterExpression), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(entityType);
            var filter = filterMethod.Invoke(null, new object[] { context });

            var sortBy = context.Request.Query["sortBy"].ToString();
            var limitStr = context.Request.Query["limit"].ToString();
            var limit = int.TryParse(limitStr, out var parsedLimit) ? parsedLimit : 100;
            
            var sortOrder = SortOrder.Descending;
            var sortOrderStr = context.Request.Query["sortOrder"].ToString();
            if (sortOrderStr.Equals("Ascending", StringComparison.OrdinalIgnoreCase) || sortOrderStr.Equals("Asc", StringComparison.OrdinalIgnoreCase))
            {
                sortOrder = SortOrder.Ascending;
            }

            var criteriaJson = context.Request.Query["criteria"].ToString();
            SearchCriterion[]? criteria = null;
            if (!string.IsNullOrEmpty(criteriaJson))
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    criteria = JsonSerializer.Deserialize<SearchCriterion[]>(criteriaJson, options);
                }
                catch {}
            }

            var queryType = typeof(FindManyQuery<>).MakeGenericType(entityType);
            var query = Activator.CreateInstance(queryType, new object?[] { filter, sortBy, sortOrder, limit, criteria });
            if (query == null) return Results.Problem("Failed to create Query instance.");

            var returnType = typeof(IReadOnlyList<>).MakeGenericType(entityType);
            var responseTask = (Task)GetSendMethod()
                .MakeGenericMethod(returnType)
                .Invoke(sender, new[] { query, context.RequestAborted })!;

            await responseTask;
            var result = ((dynamic)responseTask).Result;

            return Results.Text(JsonSerializer.Serialize(result), "application/json");
        });

        ConfigureMetadata(builder, config, "GET", entityType, 200);
    }

    private static void ConfigureMetadata(RouteHandlerBuilder builder, EndpointConfig config, string method, Type entityType, int successStatusCode)
    {
        var rolesStr = config.Roles != null && config.Roles.TryGetValue(method, out var roles)
            ? string.Join(", ", roles)
            : "Admin";

        builder
            .WithMetadata(config)
            .WithName($"{method}_{config.Entity}")
            .WithTags(config.Entity)
            .WithSummary($"{GetVerbLabel(method)} endpoint for {config.Entity} collection")
            .WithDescription($"Access {config.Entity} documents. Requires roles: {rolesStr}")
            .Produces(successStatusCode, entityType)
            .Produces(400, typeof(string))
            .Produces(401, typeof(void))
            .Produces(403, typeof(void))
            .Produces(404, typeof(void))
            .Produces(500, typeof(string));
    }

    private static string GetVerbLabel(string method) => method switch
    {
        "GET" => "List and Search",
        "GET_BY_ID" => "Fetch by ID",
        "POST" => "Insert new record",
        "PUT" => "Update existing record",
        "DELETE" => "Delete record",
        _ => method
    };

    public static Expression<Func<TEntity, bool>>? BuildFilterExpression<TEntity>(HttpContext context) where TEntity : class
    {
        var query = context.Request.Query;
        if (query.Count == 0) return null;

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        Expression? body = null;

        var properties = typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var item in query)
        {
            var key = item.Key;
            // Ignore system routing and page settings parameters
            if (key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || 
                key.Equals("limit", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("sortOrder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prop = properties.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop == null) continue;

            object? val = null;
            try
            {
                var strVal = item.Value.ToString();
                if (prop.PropertyType == typeof(ObjectId))
                {
                    val = ObjectId.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(int))
                {
                    val = int.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(decimal))
                {
                    val = decimal.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(bool))
                {
                    val = bool.Parse(strVal);
                }
                else if (prop.PropertyType.IsEnum)
                {
                    val = Enum.Parse(prop.PropertyType, strVal, true);
                }
                else
                {
                    val = Convert.ChangeType(strVal, prop.PropertyType);
                }
            }
            catch
            {
                continue;
            }

            var propExpr = Expression.Property(parameter, prop);
            var valExpr = Expression.Constant(val, prop.PropertyType);
            var eqExpr = Expression.Equal(propExpr, valExpr);

            body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);
        }

        if (body == null) return null;
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private static Type MakeNullableType(this Type type)
    {
        if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
        {
            return typeof(Nullable<>).MakeGenericType(type);
        }
        return type;
    }

    private static MethodInfo GetSendMethod()
    {
        return typeof(ISender).GetMethods()
            .First(m => m.Name == "Send" && m.IsGenericMethod);
    }

    public static IServiceCollection AddDynamicHandlers(this IServiceCollection services, ApiManifest manifest)
    {
        if (manifest == null || manifest.Endpoints == null) return services;

        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        foreach (var config in manifest.Endpoints)
        {
            var entityTypeName = $"{manifest.Namespace}.{config.Entity}";
            var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);
            if (entityType == null)
            {
                entityType = allTypes.FirstOrDefault(t => t.Name.Equals(config.Entity, StringComparison.OrdinalIgnoreCase) == true 
                    && typeof(IEntity<ObjectId>).IsAssignableFrom(t));
            }

            if (entityType == null) continue;

            // Register InsertCommand Handler
            var insertRequestType = typeof(InsertCommand<>).MakeGenericType(entityType);
            var insertHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(insertRequestType, entityType);
            var insertHandlerImpl = typeof(InsertCommandHandler<>).MakeGenericType(entityType);
            services.AddTransient(insertHandlerInterface, insertHandlerImpl);

            // Register UpdateCommand Handler
            var updateRequestType = typeof(UpdateCommand<>).MakeGenericType(entityType);
            var updateHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(updateRequestType, entityType);
            var updateHandlerImpl = typeof(UpdateCommandHandler<>).MakeGenericType(entityType);
            services.AddTransient(updateHandlerInterface, updateHandlerImpl);

            // Register DeleteCommand Handler
            var deleteRequestType = typeof(DeleteCommand<>).MakeGenericType(entityType);
            var deleteHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(deleteRequestType, typeof(bool));
            var deleteHandlerImpl = typeof(DeleteCommandHandler<>).MakeGenericType(entityType);
            services.AddTransient(deleteHandlerInterface, deleteHandlerImpl);

            // Register GetByIdQuery Handler
            var getByIdRequestType = typeof(GetByIdQuery<>).MakeGenericType(entityType);
            var getByIdHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(getByIdRequestType, entityType.MakeNullableType());
            var getByIdHandlerImpl = typeof(GetByIdQueryHandler<>).MakeGenericType(entityType);
            services.AddTransient(getByIdHandlerInterface, getByIdHandlerImpl);

            // Register FindManyQuery Handler
            var findManyRequestType = typeof(FindManyQuery<>).MakeGenericType(entityType);
            var listType = typeof(IReadOnlyList<>).MakeGenericType(entityType);
            var findManyHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(findManyRequestType, listType);
            var findManyHandlerImpl = typeof(FindManyQueryHandler<>).MakeGenericType(entityType);
            services.AddTransient(findManyHandlerInterface, findManyHandlerImpl);

            // Register SearchPagedQuery Handler
            var searchPagedRequestType = typeof(SearchPagedQuery<>).MakeGenericType(entityType);
            var pagedResultType = typeof(PagedResult<>).MakeGenericType(entityType);
            var searchPagedHandlerInterface = typeof(IRequestHandler<,>).MakeGenericType(searchPagedRequestType, pagedResultType);
            var searchPagedHandlerImpl = typeof(SearchPagedQueryHandler<>).MakeGenericType(entityType);
            services.AddTransient(searchPagedHandlerInterface, searchPagedHandlerImpl);
        }

        return services;
    }
}
