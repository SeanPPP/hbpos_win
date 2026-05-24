using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class DevicesControllerTests
{
    [Fact]
    public void DeviceEndpoints_KeepExpectedRoutes()
    {
        Assert.Equal("register", GetHttpPostTemplate(nameof(DevicesController.Register)));
        Assert.Equal("verify", GetHttpPostTemplate(nameof(DevicesController.Verify)));
        Assert.Equal("reregister", GetHttpPostTemplate(nameof(DevicesController.Reregister)));
        Assert.NotNull(typeof(DevicesController)
            .GetMethod(nameof(DevicesController.Reregister))?
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task Register_ReturnsWrappedResponseWithPendingStatus()
    {
        var expected = new DeviceRegisterResponse(
            "POS_1002_1011",
            "1002",
            "Lutwyche",
            -1,
            false,
            "Device registration is pending approval.");
        var service = new FakeDeviceService { RegisterResponse = expected };
        var controller = new DevicesController(service);
        var request = new DeviceRegisterRequest("1002", "HW-001", "Counter 1");

        var result = await controller.Register(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceRegisterResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Same(request, service.LastRegisterRequest);
    }

    [Fact]
    public async Task Verify_ReturnsWrappedDeniedResponseWithStatus()
    {
        var expected = new DeviceVerifyResponse(
            "POS_1002_1011",
            "1002",
            "Lutwyche",
            -1,
            false,
            "Device registration is pending approval.");
        var service = new FakeDeviceService { VerifyResponse = expected };
        var controller = new DevicesController(service);
        var request = new DeviceVerifyRequest("POS_1002_1011", "1002", "HW-001", "Counter 1");

        var result = await controller.Verify(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceVerifyResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Same(request, service.LastVerifyRequest);
    }

    [Fact]
    public async Task Reregister_RequiresAuthenticatedDeviceClaims()
    {
        var service = new FakeDeviceService();
        var controller = new DevicesController(service);

        var result = await controller.Reregister(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 1"),
            CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceReregisterResponse>>(unauthorized.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_AUTH_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public void CreateDeviceCode_UsesStoreCodeAndLocalHourMinute()
    {
        var deviceCode = DeviceService.CreateDeviceCode(
            "1009",
            new DateTime(2026, 5, 22, 10, 11, 0));

        Assert.Equal("POS_1009_1011", deviceCode);
    }

    private static string? GetHttpPostTemplate(string methodName)
    {
        return typeof(DevicesController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template;
    }

    private sealed class FakeDeviceService : IDeviceService
    {
        public DeviceRegisterResponse? RegisterResponse { get; init; }

        public DeviceVerifyResponse? VerifyResponse { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public DeviceRegisterRequest? LastRegisterRequest { get; private set; }

        public DeviceVerifyRequest? LastVerifyRequest { get; private set; }

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public DeviceReregisterContext? LastReregisterContext { get; private set; }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken)
        {
            LastRegisterRequest = request;
            return Task.FromResult(RegisterResponse!);
        }

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken)
        {
            LastVerifyRequest = request;
            return Task.FromResult(VerifyResponse!);
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            DeviceReregisterContext currentDevice,
            CancellationToken cancellationToken)
        {
            LastReregisterRequest = request;
            LastReregisterContext = currentDevice;
            return Task.FromResult(ReregisterResponse!);
        }
    }
}
