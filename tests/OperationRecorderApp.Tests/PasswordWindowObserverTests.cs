using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class PasswordWindowObserverTests
{
    static readonly Type Trace = typeof(PasswordWindowObserver).GetNestedType("StructureTrace", BindingFlags.NonPublic);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static JsonElement Describe(bool nativeEnabled, bool? uiaEnabled, bool hashAvailable, string failure = null,
        int visited = 1, int deepest = 0)
    {
        var trace = Activator.CreateInstance(Trace, Instance, null, new object[] { nativeEnabled, 256, 16 }, null);
        Trace.GetProperty("VisitedElements", Instance).SetValue(trace, visited);
        Trace.GetProperty("DeepestDepth", Instance).SetValue(trace, deepest);
        Trace.GetProperty("UiaRootEnabled", Instance).SetValue(trace, uiaEnabled);
        if (failure != null) Trace.GetMethod("Fail", Instance).Invoke(trace, new object[] { failure });
        var result = Trace.GetMethod("Describe", Instance).Invoke(trace, new object[] { hashAvailable ? new string('a', 64) : null });
        return JsonSerializer.SerializeToElement(result, result.GetType(), EvidenceProtocol.Json);
    }

    [Theory]
    [InlineData(false, true, true, "uia-root-enabled-mismatch")]
    [InlineData(true, false, true, "uia-root-enabled-mismatch")]
    [InlineData(false, false, true, "ok")]
    [InlineData(true, true, true, "ok")]
    [InlineData(false, true, false, "uia-structure-hash-unavailable")]
    public void Root_enabled_mismatch_requires_a_complete_structure(bool win32, bool uia, bool hashAvailable, string expected)
    {
        var result = Describe(win32, uia, hashAvailable);
        Assert.Equal(expected, result.GetProperty("code").GetString());
        Assert.Equal(win32, result.GetProperty("win32RootEnabled").GetBoolean());
        Assert.Equal(uia, result.GetProperty("uiaRootEnabled").GetBoolean());
    }

    [Fact]
    public void Unavailable_root_has_no_uia_state_and_no_visited_nodes()
    {
        var result = Describe(false, null, false, "uia-root-unavailable", visited: 0, deepest: -1);
        Assert.Equal("uia-root-unavailable", result.GetProperty("code").GetString());
        Assert.False(result.GetProperty("win32RootEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("uiaRootEnabled").ValueKind);
        Assert.Equal(0, result.GetProperty("visitedElements").GetInt32());
        Assert.Equal(-1, result.GetProperty("deepestDepth").GetInt32());
    }

    [Fact]
    public void Depth_failure_reports_attempted_depth_and_bounded_visited_count()
    {
        var result = Describe(false, false, false, "uia-depth-limit", visited: 17, deepest: 17);
        Assert.Equal("uia-depth-limit", result.GetProperty("code").GetString());
        Assert.Equal(17, result.GetProperty("visitedElements").GetInt32());
        Assert.Equal(17, result.GetProperty("deepestDepth").GetInt32());
        Assert.Equal(16, result.GetProperty("maxDepth").GetInt32());
        Assert.Equal(256, result.GetProperty("maxElements").GetInt32());
        Assert.Equal(7, result.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(50004, false, false, false, false)]
    [InlineData(50004, false, false, true, true)]
    [InlineData(50025, false, false, true, true)]
    [InlineData(50004, true, false, false, true)]
    [InlineData(50004, false, true, false, true)]
    [InlineData(50020, false, false, true, false)]
    public void Enabled_mismatch_checks_disabled_authentication_shaped_children(int type, bool enabled, bool isPassword,
        bool enabledMismatch, bool expected)
    {
        var elementType = typeof(PasswordWindowObserver).GetNestedType("ObservedElement", BindingFlags.NonPublic);
        var elements = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
        var bounds = new Rectangle(5, 5, 100, 20);
        var node = new EvidenceNode(type, bounds.X, bounds.Y, bounds.Width, bounds.Height, isPassword, enabled);
        elements.Add(Activator.CreateInstance(elementType, Instance, null, new object[] { null, 0L, bounds, node }, null));
        var background = new WindowObservation(Guid.NewGuid(), 1, 1, 0, 0, 10, false, true, false, false, bounds, 96, "unowned");
        var guard = typeof(PasswordWindowObserver).GetMethod("HasBackgroundAuthentication", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Equal(expected, (bool)guard.Invoke(null, new object[] { elements, background, enabledMismatch }));
    }

    [Fact]
    public void Expected_uia_failures_do_not_include_cancellation()
    {
        var classify = typeof(PasswordWindowObserver).GetMethod("IsUiaObservationError", BindingFlags.Static | BindingFlags.NonPublic);
        bool IsUia(Exception error) => (bool)classify.Invoke(null, new object[] { error });
        Assert.True(IsUia(new COMException()));
        Assert.True(IsUia(new InvalidOperationException()));
        Assert.True(IsUia(new ArgumentException()));
        Assert.False(IsUia(new OperationCanceledException(new CancellationToken(canceled: true))));
    }
}
