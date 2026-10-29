using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

[TestFixture]
public class ImmichFrameAuthenticationHandlerTests
{
    [Test]
    public void SecretMatches_EqualSecret_ReturnsTrue()
    {
        Assert.That(ImmichFrameAuthenticationHandler.SecretMatches("s3cret-value", "s3cret-value"), Is.True);
    }

    [TestCase("s3cret-valuE")]
    [TestCase("s3cret-value-and-more")]
    [TestCase("s3cret")]
    [TestCase("")]
    [TestCase("S3CRET-VALUE")]
    public void SecretMatches_DifferentSecret_ReturnsFalse(string given)
    {
        Assert.That(ImmichFrameAuthenticationHandler.SecretMatches("s3cret-value", given), Is.False);
    }
}
