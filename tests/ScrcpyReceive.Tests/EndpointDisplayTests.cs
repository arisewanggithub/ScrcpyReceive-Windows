using System.Net;
using System.Text.Json;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class EndpointDisplayTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1 : 27184")]
    [InlineData("192.0.2.1", "192.0.2.1 : 27184")]
    [InlineData("::1", "[::1] : 27184")]
    public void DisplayUsesSpacesWithoutChangingAddresses(string address, string expected) =>
        Assert.Equal(expected, Discovery.DisplayEndpoint(IPAddress.Parse(address), 27184));

    [Fact]
    public void AdvertisementKeepsANumericPort()
    {
        using var document = JsonDocument.Parse(Discovery.Advertisement("Test", 27184, false));
        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("port").ValueKind);
        Assert.Equal(27184, document.RootElement.GetProperty("port").GetInt32());
    }
}
