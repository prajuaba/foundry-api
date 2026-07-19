using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;
using Xunit;
using MongoDB.Bson;
using MongoDB.Driver;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Paging;
using Foundry.Core.User;
using FoundryMongo.Repositories;
using Paperclip.OrderingSystem.Domain;

namespace Foundry.Api.Tests;

public class DynamicApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    static DynamicApiTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    public DynamicApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetDocSpec_ReturnsHtmlDocument()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/docs/spec");

        // Assert
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Foundry.API Dynamic Specification", html);
        Assert.Contains("Order", html);
    }

    [Fact]
    public async Task GetOrders_WithMockRepository_ReturnsMappedEntities()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        var orderId = ObjectId.GenerateNewId();
        var ordersList = new List<Order>
        {
            new() { Id = orderId, OrderNumber = "ORD-001", CustomerId = "cust-1", TotalAmount = 99.99m }
        };

        mockRepo.FindManyAsync(
            Arg.Any<Expression<Func<Order, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<SortOrder>(),
            Arg.Any<int>(),
            Arg.Any<MongoDB.Driver.IClientSessionHandle>(),
            Arg.Any<CancellationToken>()
        ).Returns(ordersList);

        // Stub ICurrentUserContext to bypass authorization or provide Admin role
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/api/v1/orders");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {response.StatusCode}: {body}");
        }

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ORD-001", result[0].OrderNumber);
    }

    [Fact]
    public async Task PostOrder_ValidationFails_ReturnsBadRequest()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // Invalid order details (empty order number, negative amount)
        var invalidOrder = new Order
        {
            Id = ObjectId.GenerateNewId(),
            OrderNumber = "", // Empty order number (length < 3)
            TotalAmount = -50.0m // Negative amount
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/orders", invalidOrder);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostOrder_SecurityFails_WhenRoleIsInvalid_ReturnsForbidden()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        // Bypassing auth check by passing a "User" role, but POST /api/v1/orders requires "Admin" role
        mockUserContext.OperatorId.Returns("normal-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "User") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        var order = new Order
        {
            Id = ObjectId.GenerateNewId(),
            OrderNumber = "ORD-999",
            TotalAmount = 50.0m
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/orders", order);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("User does not have permission to execute POST on /api/v1/orders", body);
    }

    [Fact]
    public async Task CachingBehavior_IdempotencyAndInvalidation_CachesCallsAndEvictsOnMutation()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        var orderId = ObjectId.GenerateNewId();
        var order = new Order { Id = orderId, OrderNumber = "ORD-CACHED", TotalAmount = 100m };

        // Setup repository response
        mockRepo.GetByIdAsync(orderId, Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>())
            .Returns(order);

        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // 1. First GET request - Cache Miss
        var response1 = await client.GetAsync($"/api/v1/orders/{orderId}");
        response1.EnsureSuccessStatusCode();
        var result1 = await response1.Content.ReadFromJsonAsync<Order>();
        Assert.NotNull(result1);

        // 2. Second GET request - Cache Hit (Repository should not be called again)
        var response2 = await client.GetAsync($"/api/v1/orders/{orderId}");
        response2.EnsureSuccessStatusCode();
        var result2 = await response2.Content.ReadFromJsonAsync<Order>();
        Assert.NotNull(result2);

        // Verify repository GetByIdAsync was called exactly ONCE
        await mockRepo.Received(1).GetByIdAsync(orderId, Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>());

        // 3. Perform Mutation (PUT update) which should invalidate cache
        var putResponse = await client.PutAsJsonAsync($"/api/v1/orders/{orderId}", order);
        putResponse.EnsureSuccessStatusCode();

        // 4. Third GET request - Cache Evicted, should hit Repository again (called twice total)
        var response3 = await client.GetAsync($"/api/v1/orders/{orderId}");
        response3.EnsureSuccessStatusCode();

        await mockRepo.Received(2).GetByIdAsync(orderId, Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LiveMongoDb_CRUD_Operations_Succeed()
    {
        // 1. Check if a local MongoDB is reachable
        var client = new MongoClient("mongodb://localhost:27017");
        try
        {
            await client.ListDatabaseNamesAsync().ConfigureAwait(false);
        }
        catch
        {
            // Gracefully skip the test if MongoDB daemon is not running
            return;
        }

        // 2. Build the client using the real database connection (no repository mocks!)
        var httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                // We do NOT register a mock IRepository here, so it uses the real database!
                // Register mock current user context for security checks
                var mockUserContext = Substitute.For<ICurrentUserContext>();
                mockUserContext.OperatorId.Returns("admin-user");
                var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
                var identity = new ClaimsIdentity(claims, "TestAuth");
                mockUserContext.User.Returns(new ClaimsPrincipal(identity));
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // 3. Test POST order (insert)
        var orderId = ObjectId.GenerateNewId();
        var order = new Order
        {
            Id = orderId,
            OrderNumber = "ORD-LIVE-001",
            TotalAmount = 250.0m
        };

        var postResponse = await httpClient.PostAsJsonAsync("/api/v1/orders", order);
        postResponse.EnsureSuccessStatusCode();

        // 4. Test GET order (retrieve)
        var getResponse = await httpClient.GetAsync($"/api/v1/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var retrieved = await getResponse.Content.ReadFromJsonAsync<Order>();
        Assert.NotNull(retrieved);
        Assert.Equal("ORD-LIVE-001", retrieved.OrderNumber);

        // 5. Cleanup the database collection after testing
        var db = client.GetDatabase("foundry_ordering_system");
        var collection = db.GetCollection<Order>("Order");
        await collection.DeleteOneAsync(o => o.Id == orderId);
    }

    [Fact]
    public async Task Get_Endpoints_CachingBehavior_With_L2_DistributedCache_Works_Correctly()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        var mockDistributedCache = Substitute.For<IDistributedCache>();
        var orderId = ObjectId.GenerateNewId();
        var order = new Order { Id = orderId, OrderNumber = "ORD-L2-CACHED", TotalAmount = 450m };

        mockRepo.GetByIdAsync(orderId, Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>())
            .Returns(order);

        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddSingleton<IDistributedCache>(mockDistributedCache);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // 1. First GET request - L1 & L2 Cache Miss. Should query repository, and populate both L1 and L2 caches
        var response1 = await httpClient.GetAsync($"/api/v1/orders/{orderId}");
        response1.EnsureSuccessStatusCode();

        // Verify repository was queried
        await mockRepo.Received(1).GetByIdAsync(orderId, Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>());

        // Verify IDistributedCache.SetAsync was called to populate L2 cache
        await mockDistributedCache.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );

        // 2. Perform Mutation (PUT update) which should invalidate cache in both L1 and L2
        var putResponse = await httpClient.PutAsJsonAsync($"/api/v1/orders/{orderId}", order);
        putResponse.EnsureSuccessStatusCode();

        // Verify IDistributedCache.RemoveAsync was called to evict key from L2 cache
        await mockDistributedCache.Received().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task Get_Endpoints_With_Advanced_Criteria_Filters_Correctly()
    {
        // Arrange
        var mockRepo = Substitute.For<IRepository<Order>>();
        mockRepo.FindByCriteriaAsync(Arg.Any<SearchCriterion[]>(), Arg.Any<MongoDB.Driver.IClientSessionHandle>(), Arg.Any<CancellationToken>())
            .Returns(new List<Order>());

        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Order>>(mockRepo);
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        // Act - Call GET many with search criteria JSON
        var criteriaJson = "[{\"Field\":\"OrderNumber\",\"Operator\":\"Equals\",\"Value\":\"ORD-100\"}]";
        var response = await httpClient.GetAsync($"/api/v1/orders?criteria={Uri.EscapeDataString(criteriaJson)}");
        response.EnsureSuccessStatusCode();

        // Assert
        await mockRepo.Received(1).FindByCriteriaAsync(
            Arg.Is<SearchCriterion[]>(c => c.Length == 1 && c[0].Field == "OrderNumber" && c[0].Operator == SearchOperator.Equals && c[0].Value.ToString() == "ORD-100"),
            Arg.Any<MongoDB.Driver.IClientSessionHandle>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task CustomEndpoint_Dispatches_Successfully()
    {
        // Arrange
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("admin-user");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        mockUserContext.User.Returns(new ClaimsPrincipal(identity));

        var httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
            });
        }).CreateClient();

        var payload = new { CustomerId = "cust-1", ItemIds = new List<string> { "item-1", "item-2" } };

        // Act - Call custom POST endpoint
        var response = await httpClient.PostAsJsonAsync("/api/v1/orders/checkout", payload);
        response.EnsureSuccessStatusCode();

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Paperclip.OrderingSystem.Domain.PlaceOrderResult>();
        Assert.NotNull(result);
        Assert.Equal("ORD-12345", result.OrderId);
        Assert.Equal("Processed", result.Status);
    }
}
