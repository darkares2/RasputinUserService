using System;
using Microsoft.Extensions.Logging;
using Moq;
using UserService;
using Xunit;

namespace UserServiceTests;

public class UnitTestUserService
{
    [Fact]
    public async void UnitTestUserServiceInvalidCommandAsync()
    {
        var sut = new QueueTriggerUserService();
        var message = new Message
        {
            Body = "{\"command\":\"invalid\",\"user\":{\"id\":\"1\",\"name\":\"John Doe\"}}"
        };
        var loggerMock = new Mock<ILogger>();

        // RunAsync and expect ArgumentNullException
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await sut.RunAsync(message.Body, loggerMock.Object));

    }
}