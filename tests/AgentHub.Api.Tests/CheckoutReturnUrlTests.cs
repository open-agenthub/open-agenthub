using AgentHub.Api.Controllers;
using Xunit;

namespace AgentHub.Api.Tests;

public class CheckoutReturnUrlTests
{
    [Theory]
    [InlineData("https://hub.example.com/license/activate", "https://hub.example.com")]
    [InlineData("http://localhost:8080/license/activate", "http://localhost:8080")]
    [InlineData("HTTPS://HUB.EXAMPLE.COM/x", "https://hub.example.com")]
    public void Accepts_urls_on_the_same_origin(string returnUrl, string origin)
        => Assert.True(AdminController.IsSameOrigin(returnUrl, origin));

    [Theory]
    [InlineData("https://evil.example.com/license/activate", "https://hub.example.com")]
    [InlineData("https://hub.example.com:444/x", "https://hub.example.com")]
    [InlineData("http://hub.example.com/x", "https://hub.example.com")]
    [InlineData("/relative", "https://hub.example.com")]
    [InlineData("javascript:alert(1)", "https://hub.example.com")]
    public void Rejects_foreign_or_invalid_urls(string returnUrl, string origin)
        => Assert.False(AdminController.IsSameOrigin(returnUrl, origin));
}
