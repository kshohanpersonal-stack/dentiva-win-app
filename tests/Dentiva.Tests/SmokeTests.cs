using Dentiva.Core.Services;
using Xunit;

namespace Dentiva.Tests;

public class BillingCalculatorTests
{
    [Fact]
    public void LineTotal_AppliesDiscount()
    {
        Assert.Equal(900m, BillingCalculator.LineTotal(2, 500, 100));
    }
}
