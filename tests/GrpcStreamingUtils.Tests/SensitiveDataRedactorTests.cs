using Niarru.GrpcStreamingUtils.Logging;

namespace GrpcStreamingUtils.Tests;

public class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_Null_ReturnsNullString()
    {
        Assert.Equal("null", SensitiveDataRedactor.Redact(null));
    }

    [Fact]
    public void Redact_PlainString_ReturnsAsIs()
    {
        Assert.Equal("hello", SensitiveDataRedactor.Redact("hello"));
    }

    [Fact]
    public void Redact_TooLargeString_ReturnsTooLarge()
    {
        var large = new string('x', 16384);
        Assert.Equal("[TOO LARGE]", SensitiveDataRedactor.Redact(large));
    }

    [Fact]
    public void Redact_StringJustUnderLimit_ReturnsAsIs()
    {
        var str = new string('x', 16383);
        Assert.Equal(str, SensitiveDataRedactor.Redact(str));
    }

    [Fact]
    public void Redact_Integer_ReturnsToString()
    {
        Assert.Equal("42", SensitiveDataRedactor.Redact(42));
    }

    [Theory]
    [InlineData("token", true)]
    [InlineData("accessToken", true)]
    [InlineData("password", true)]
    [InlineData("secret", true)]
    [InlineData("credential", true)]
    [InlineData("ticket", true)]
    [InlineData("Token", true)]
    [InlineData("tokenType", false)]
    [InlineData("token_type", false)]
    [InlineData("username", false)]
    [InlineData("email", false)]
    public void IsSensitiveField_CorrectlyIdentifiesSensitiveFields(string fieldName, bool expected)
    {
        Assert.Equal(expected, SensitiveDataRedactor.IsSensitiveField(fieldName));
    }
}
