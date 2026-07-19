using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;
using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Foundry.Rules;
using Foundry.Api.Manifest;
using Foundry.Core.User;
using FoundryMongo.Repositories;
using Paperclip.OrderingSystem.Domain; // Target entity types

namespace Foundry.Api.Tests;

public class WorkflowEngineTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowEngine _workflowEngine;

    public WorkflowEngineTests()
    {
        var services = new ServiceCollection();
        
        // Register default workflow services
        services.AddFoundryRules();
        
        // Mock dependencies
        var mockSender = Substitute.For<ISender>();
        services.AddSingleton(mockSender);

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<IWorkflowEngine>();
    }

    [Fact]
    public void ValidatePermission_WithAuthorizedRole_Succeeds()
    {
        // Act & Assert
        _workflowEngine.ValidatePermission(
            "transition_1",
            "Draft",
            new List<string> { "Manager" },
            new List<string> { "Admin" },
            new List<string> { "Admin", "Manager" });
    }

    [Fact]
    public void ValidatePermission_WithUnauthorizedTransitionRole_ThrowsWorkflowException()
    {
        // Act & Assert
        Assert.Throws<WorkflowException>(() =>
            _workflowEngine.ValidatePermission(
                "transition_1",
                "Draft",
                new List<string> { "Manager" },
                new List<string> { "Admin" },
                new List<string> { "Admin" })); // Missing Manager role
    }

    [Theory]
    [InlineData("TotalAmount", "GreaterThan", "100", 150.0, true)]
    [InlineData("TotalAmount", "GreaterThan", "100", 50.0, false)]
    [InlineData("TotalAmount", "LessThanOrEqual", "100.50", 100.50, true)]
    [InlineData("Status", "Equal", "Pending", "Pending", true)]
    [InlineData("Status", "NotEqual", "Approved", "Draft", true)]
    public void EvaluateCondition_WithDifferentOperators_ReturnsExpectedResult(
        string property, string op, string expected, object value, bool expectedResult)
    {
        // Arrange
        var request = new DummyRequest { Property = value };
        // We map the requested property dynamically in the test source object
        var requestSource = new DictionaryRequestSource(property, value);

        // Act
        var result = _workflowEngine.EvaluateCondition(property, op, expected, requestSource);

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task ExecuteAction_WithInternalMediatRCommand_DispatchesSuccessfully()
    {
        // Arrange
        var mockSender = Substitute.For<ISender>();
        var services = new ServiceCollection();
        services.AddSingleton(mockSender);
        services.AddSingleton<IWorkflowEngine, WorkflowEngine>();
        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();

        var request = new DummyRequest { Property = "Order-123" };

        // Act
        var detail = await engine.ExecuteActionAsync(
            "InternalApi",
            "DummyCommand",
            "{ \"Id\": \"{{Property}}\" }",
            null, null, null, null,
            request,
            CancellationToken.None);

        // Assert
        Assert.True(detail.Success);
        Assert.Equal(200, detail.StatusCode);
        await mockSender.Received(1).Send(Arg.Is<object>(c => c is DummyCommand && ((DummyCommand)c).Id == "Order-123"), Arg.Any<CancellationToken>());
    }

    private class DictionaryRequestSource
    {
        private readonly string _propName;
        private readonly object _value;

        public DictionaryRequestSource(string propName, object value)
        {
            _propName = propName;
            _value = value;
        }

        public object? TotalAmount => _propName == "TotalAmount" ? _value : null;
        public string? Status => _propName == "Status" ? (string)_value : null;
    }

    public class DummyRequest
    {
        public object? Property { get; set; }
    }

    public class DummyCommand : IRequest
    {
        public string Id { get; set; } = string.Empty;
    }
}
