using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AtSpiCaretContextRoleTests
{
    [Theory]
    [InlineData(40u, false)] // PASSWORD_TEXT
    [InlineData(0u, false)] // INVALID
    [InlineData(67u, false)] // UNKNOWN
    [InlineData(61u, true)] // TEXT
    [InlineData(79u, true)] // ENTRY
    public void OnlyNamedNonPasswordRolesAreRead(uint role, bool readable) =>
        Assert.Equal(readable, AtSpiEventClient.IsReadableCaretContextRole(role));
}
