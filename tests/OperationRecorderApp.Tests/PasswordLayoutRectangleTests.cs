using System.Drawing;
using System.Windows;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class PasswordLayoutRectangleTests
{
    [Fact]
    [Trait("Category", "Core")]
    public void EmptyRectIsDistinctFromFiniteZeroSize()
    {
        Assert.Equal("empty", PasswordWindowObserver.RectangleFailure(Rect.Empty, out var rejected));
        Assert.Equal(Rectangle.Empty, rejected);
    }

    [Theory]
    [InlineData(10, 20, 0, 40, "non-positive-size")]
    [InlineData(10, 20, 30, 0, "non-positive-size")]
    [InlineData(double.NaN, 20, 30, 40, "non-finite")]
    [InlineData(10, 20, double.PositiveInfinity, 40, "non-finite")]
    [InlineData(10.5, 20, 30, 40, "non-integer")]
    [InlineData(10, 20, 30, 40.5, "non-integer")]
    [InlineData(int.MaxValue, 20, 1, 40, "out-of-range")]
    [InlineData(10, int.MinValue - 1d, 30, 40, "out-of-range")]
    [InlineData(10, 20, 16385, 40, "out-of-range")]
    [InlineData(10, 20, 30, 16385, "out-of-range")]
    [Trait("Category", "Core")]
    public void UnsafeEdgesAndSpansReturnFixedCodes(double x, double y, double width, double height, string code)
    {
        Assert.Equal(code, PasswordWindowObserver.RectangleFailure(new Rect(x, y, width, height), out var rejected));
        Assert.Equal(Rectangle.Empty, rejected);
    }

    [Theory]
    [InlineData(-40, -30, 20, 10)]
    [InlineData(int.MinValue, int.MinValue, 16384, 16384)]
    [InlineData(int.MaxValue - 1, int.MaxValue - 1, 1, 1)]
    [Trait("Category", "Core")]
    public void WholePixelEdgesAndSupportedSpansAreAccepted(int x, int y, int width, int height)
    {
        Assert.Null(PasswordWindowObserver.RectangleFailure(new Rect(x, y, width, height), out var rectangle));
        Assert.Equal(new Rectangle(x, y, width, height), rectangle);
    }
}
