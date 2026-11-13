using Cadastro.Application.Users;
using NSubstitute;

namespace Cadastro.Application.Tests.Users;

public sealed class GetMyProfileHandlerTests
{
    private readonly IIdentityUserReadService _users = Substitute.For<IIdentityUserReadService>();

    [Fact]
    public async Task Handle_ReturnsTheProfileOfTheSessionUser()
    {
        var profile = new MyProfile("ana.souza", "Ana", "Souza", "ana@example.com", null, null);
        _users.FindProfileAsync("user-1", Arg.Any<CancellationToken>()).Returns(profile);

        var result = await new GetMyProfileHandler(_users).HandleAsync("user-1", TestContext.Current.CancellationToken);

        result.ShouldBe(profile);
    }

    [Fact]
    public async Task Handle_WhenUserNoLongerExists_ReturnsNull()
    {
        _users.FindProfileAsync("gone", Arg.Any<CancellationToken>()).Returns((MyProfile?)null);

        (await new GetMyProfileHandler(_users).HandleAsync("gone", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WithoutSessionUserId_FailsFastWithoutCallingKeycloak(string userId)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            new GetMyProfileHandler(_users).HandleAsync(userId, TestContext.Current.CancellationToken));

        await _users.DidNotReceiveWithAnyArgs().FindProfileAsync(default!, TestContext.Current.CancellationToken);
    }
}
