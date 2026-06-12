using System.Reflection;
using Xunit;
using Hydra.Domain.Enums;

namespace Hydra.Tests.Unit;

public class FeeCalculationTests
{
    public static TheoryData<FeeTypeEnum, decimal, decimal, decimal> GetFeeCalculationData()
    {
        var data = new TheoryData<FeeTypeEnum, decimal, decimal, decimal>();

        data.Add(FeeTypeEnum.FIXED, 0.5m, 1000m, 0.5m);
        data.Add(FeeTypeEnum.FIXED, 5000m, 5000m, 5000m);
        data.Add(FeeTypeEnum.FIXED, 0, 10000m, 0);
        data.Add(FeeTypeEnum.PERCENTAGE, 10, 1000m, 100);
        data.Add(FeeTypeEnum.PERCENTAGE, 5, 50000m, 2500);
        data.Add(FeeTypeEnum.PERCENTAGE, 0, 10000m, 0);
        data.Add(FeeTypeEnum.PERCENTAGE, 100, 1000m, 1000);

        return data;
    }

    [Theory]
    [MemberData(nameof(GetFeeCalculationData))]
    public void CalculateFee_VariousScenarios_ReturnsExpectedValue(
        FeeTypeEnum feeType, decimal feeValue, decimal amount, decimal expectedFee)
    {
        var tenant = new Hydra.Domain.Entities.Tenant
        {
            Id = System.Guid.NewGuid(),
            FeeType = feeType,
            FeeValue = feeValue
        };

        var method = typeof(Hydra.Application.Services.TransactionService).GetMethod(
            "CalculateFee",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = (decimal)method!.Invoke(null, new object[] { tenant, amount })!;

        Assert.Equal(expectedFee, result);
    }
}
