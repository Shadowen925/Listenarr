/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Text.Json;
using Listenarr.Api.Attributes;
using Listenarr.Api.Features.DownloadClients.Models;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Configuration
{
    public class ConfigurationControllerDownloadClientTests
    {
        [Fact]
        public async Task TestDownloadClientConfiguration_RemoteCaller_AllowsPrivateHost_AndRedactsSecrets()
        {
            // Arrange
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var downloadClientGateway = new Mock<IDownloadClientGateway>(MockBehavior.Strict);
            var logger = NullLogger<DownloadClientController>.Instance;

            var testedClient = new DownloadClientConfiguration
            {
                Id = "client-1",
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            downloadClientGateway
                .Setup(x => x.TestConnectionAsync(It.IsAny<DownloadClientConfiguration>()))
                .ReturnsAsync((true, "Connection successful"));

            var controller = new DownloadClientController(
                configurationService.Object,
                downloadClientGateway.Object,
                logger);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IStartupConfigService>(new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "false" }))
                .BuildServiceProvider();
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var request = new DownloadClientConfiguration
            {
                // Keep ID empty so the endpoint does not try to merge from an existing saved config.
                Id = string.Empty,
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            // Act
            var actionResult = await controller.TestDownloadClientConfiguration(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;

            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);

            var clientProp = payload.GetType().GetProperty("client");
            Assert.NotNull(clientProp);
            var redactedClient = Assert.IsType<DownloadClientConfiguration>(clientProp!.GetValue(payload));
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedClient.Username);
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedClient.Password);
            Assert.True(redactedClient.Settings.TryGetValue("apiKey", out var redactedApiKey));
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedApiKey?.ToString());

            downloadClientGateway.Verify(
                x => x.TestConnectionAsync(It.Is<DownloadClientConfiguration>(c =>
                    c.Host == "192.168.1.50" && c.Port == 6789)),
                Times.Once);
            configurationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TestDownloadClientConfiguration_PrivateNetworkCaller_StillRedactsSecrets()
        {
            // Arrange
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var downloadClientGateway = new Mock<IDownloadClientGateway>(MockBehavior.Strict);
            var logger = NullLogger<DownloadClientController>.Instance;

            var testedClient = new DownloadClientConfiguration
            {
                Id = "client-2",
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            downloadClientGateway
                .Setup(x => x.TestConnectionAsync(It.IsAny<DownloadClientConfiguration>()))
                .ReturnsAsync((true, "Connection successful"));

            var controller = new DownloadClientController(
                configurationService.Object,
                downloadClientGateway.Object,
                logger);

            var httpContext = new DefaultHttpContext();
            // Simulate a trusted LAN/Synology-Docker caller.
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.23");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var request = new DownloadClientConfiguration
            {
                Id = string.Empty,
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            // Act
            var actionResult = await controller.TestDownloadClientConfiguration(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;

            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);

            var clientProp = payload.GetType().GetProperty("client");
            Assert.NotNull(clientProp);
            var returnedClient = Assert.IsType<DownloadClientConfiguration>(clientProp!.GetValue(payload));
            Assert.Equal(ApiResponseRedactor.RedactedValue, returnedClient.Username);
            Assert.Equal(ApiResponseRedactor.RedactedValue, returnedClient.Password);
            Assert.True(returnedClient.Settings.TryGetValue("apiKey", out var apiKey));
            Assert.Equal(ApiResponseRedactor.RedactedValue, apiKey?.ToString());

            downloadClientGateway.Verify(
                x => x.TestConnectionAsync(It.Is<DownloadClientConfiguration>(c =>
                    c.Host == "192.168.1.50" && c.Port == 6789)),
                Times.Once);
            configurationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SaveDownloadClientConfiguration_RedactedApiKey_PreservesStoredCredential()
        {
            var existing = new DownloadClientConfiguration
            {
                Id = "client-3",
                Type = "sabnzbd",
                Settings = new Dictionary<string, object> { ["apiKey"] = "stored-secret" }
            };
            DownloadClientConfiguration? saved = null;
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync("client-3"))
                .ReturnsAsync(existing);
            configurationService.Setup(service =>
                    service.SaveDownloadClientConfigurationAsync(It.IsAny<DownloadClientConfiguration>()))
                .Callback<DownloadClientConfiguration>(configuration => saved = configuration)
                .ReturnsAsync("client-3");
            var controller = new DownloadClientController(
                configurationService.Object,
                Mock.Of<IDownloadClientGateway>(),
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            var result = await controller.SaveDownloadClientConfiguration(new DownloadClientConfigurationRequest
            {
                Id = "client-3",
                Type = "sabnzbd",
                Settings = new Dictionary<string, object> { ["apiKey"] = ApiResponseRedactor.RedactedValue }
            });

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.NotNull(saved);
            Assert.Equal("stored-secret", saved!.Settings["apiKey"].ToString());
            configurationService.VerifyAll();
        }

        [Fact]
        public async Task TestDownloadClientConfiguration_RedactedApiKey_UsesStoredCredentialAndRedactsResponse()
        {
            var existing = new DownloadClientConfiguration
            {
                Id = "client-4",
                Type = "sabnzbd",
                Settings = new Dictionary<string, object> { ["apiKey"] = "stored-secret" }
            };
            DownloadClientConfiguration? tested = null;
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync("client-4"))
                .ReturnsAsync(existing);
            var gateway = new Mock<IDownloadClientGateway>(MockBehavior.Strict);
            gateway.Setup(service => service.TestConnectionAsync(It.IsAny<DownloadClientConfiguration>()))
                .Callback<DownloadClientConfiguration, CancellationToken>((configuration, _) => tested = configuration)
                .ReturnsAsync((true, "Connection successful"));
            var controller = new DownloadClientController(
                configurationService.Object,
                gateway.Object,
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.TestDownloadClientConfiguration(new DownloadClientConfiguration
            {
                Id = "client-4",
                Type = "sabnzbd",
                Settings = new Dictionary<string, object> { ["apiKey"] = ApiResponseRedactor.RedactedValue }
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            Assert.NotNull(tested);
            Assert.Equal("stored-secret", tested!.Settings["apiKey"].ToString());
            var returnedClient = Assert.IsType<DownloadClientConfiguration>(
                ok.Value!.GetType().GetProperty("client")!.GetValue(ok.Value));
            Assert.Equal(ApiResponseRedactor.RedactedValue, returnedClient.Settings["apiKey"].ToString());
            configurationService.VerifyAll();
            gateway.VerifyAll();
        }

        [Fact]
        public async Task GetDownloadClientConfiguration_ReturnsPathAndPasswordPresenceWithoutReturningPassword()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var stored = new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Type = "deluge",
                Username = "saved-user",
                DownloadPath = "/data/deluge/completed/",
                Password = "saved-secret"
            };
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync(stored.Id))
                .ReturnsAsync(stored);
            var controller = new DownloadClientController(
                configurationService.Object,
                Mock.Of<IDownloadClientGateway>(),
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.GetDownloadClientConfiguration(stored.Id);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = ok.Value!;
            Assert.Equal(stored.DownloadPath, response.GetType().GetProperty("DownloadPath")!.GetValue(response));
            Assert.Equal(true, response.GetType().GetProperty("HasSavedPassword")!.GetValue(response));
            Assert.Equal(true, response.GetType().GetProperty("HasSavedUsername")!.GetValue(response));
            Assert.Equal(ApiResponseRedactor.RedactedValue, response.GetType().GetProperty("Username")!.GetValue(response));
            Assert.Null(response.GetType().GetProperty("Password"));
            configurationService.VerifyAll();
        }

        [Fact]
        public async Task SaveDownloadClientConfiguration_OmittedPathSettingsAndPasswordPreserveStoredValues()
        {
            var existing = new DownloadClientConfiguration
            {
                Id = "deluge-2",
                Type = "deluge",
                DownloadPath = "/data/deluge/completed/",
                Password = "saved-password",
                Settings = new Dictionary<string, object>
                {
                    ["stalledEnabled"] = false,
                    ["stalledAutoBlocklist"] = true
                }
            };
            DownloadClientConfiguration? saved = null;
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync(existing.Id))
                .ReturnsAsync(existing);
            configurationService.Setup(service =>
                    service.SaveDownloadClientConfigurationAsync(It.IsAny<DownloadClientConfiguration>()))
                .Callback<DownloadClientConfiguration>(configuration => saved = configuration)
                .ReturnsAsync(existing.Id);
            var controller = new DownloadClientController(
                configurationService.Object,
                Mock.Of<IDownloadClientGateway>(),
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.SaveDownloadClientConfiguration(new DownloadClientConfigurationRequest
            {
                Id = existing.Id,
                Name = "Deluge updated",
                Type = "deluge"
            });

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.NotNull(saved);
            Assert.Equal(existing.DownloadPath, saved!.DownloadPath);
            Assert.Equal(existing.Password, saved.Password);
            Assert.Equal(JsonValueKind.False, Assert.IsType<JsonElement>(saved.Settings["stalledEnabled"]).ValueKind);
            Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(saved.Settings["stalledAutoBlocklist"]).ValueKind);
            configurationService.VerifyAll();
        }

        [Fact]
        public async Task SaveDownloadClientConfiguration_EmptyPathAndReplacementPasswordAreApplied()
        {
            var existing = new DownloadClientConfiguration
            {
                Id = "deluge-3",
                Type = "deluge",
                DownloadPath = "/data/deluge/completed/",
                Password = "old-password"
            };
            DownloadClientConfiguration? saved = null;
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync(existing.Id))
                .ReturnsAsync(existing);
            configurationService.Setup(service =>
                    service.SaveDownloadClientConfigurationAsync(It.IsAny<DownloadClientConfiguration>()))
                .Callback<DownloadClientConfiguration>(configuration => saved = configuration)
                .ReturnsAsync(existing.Id);
            var controller = new DownloadClientController(
                configurationService.Object,
                Mock.Of<IDownloadClientGateway>(),
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.SaveDownloadClientConfiguration(new DownloadClientConfigurationRequest
            {
                Id = existing.Id,
                Type = "deluge",
                DownloadPath = string.Empty,
                Password = "replacement-password"
            });

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.NotNull(saved);
            Assert.Equal(string.Empty, saved!.DownloadPath);
            Assert.Equal("replacement-password", saved.Password);
            configurationService.VerifyAll();
        }

        [Fact]
        public async Task TestDownloadClientConfiguration_UsesSavedPasswordAndReturnsAuthenticationFailure()
        {
            var existing = new DownloadClientConfiguration
            {
                Id = "deluge-auth",
                Type = "deluge",
                Username = "saved-user",
                Password = "saved-password"
            };
            DownloadClientConfiguration? tested = null;
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync(existing.Id))
                .ReturnsAsync(existing);
            var gateway = new Mock<IDownloadClientGateway>(MockBehavior.Strict);
            gateway.Setup(service => service.TestConnectionAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<CancellationToken>()))
                .Callback<DownloadClientConfiguration, CancellationToken>((configuration, _) => tested = configuration)
                .ReturnsAsync((false, "Deluge authentication failed (401)"));
            var controller = new DownloadClientController(
                configurationService.Object,
                gateway.Object,
                NullLogger<DownloadClientController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.TestDownloadClientConfiguration(new DownloadClientConfiguration
            {
                Id = existing.Id,
                Type = "deluge",
                Username = string.Empty,
                Password = string.Empty
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            Assert.NotNull(tested);
            Assert.Equal(existing.Username, tested!.Username);
            Assert.Equal(existing.Password, tested.Password);
            Assert.Equal(false, ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value));
            Assert.Equal(
                "Deluge authentication failed (401)",
                ok.Value.GetType().GetProperty("message")!.GetValue(ok.Value));
            gateway.VerifyAll();
            configurationService.VerifyAll();
        }

        [Fact]
        public async Task ApiKeyManagementFilter_RemoteCaller_WhenAuthenticationDisabled_ReturnsForbidden()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            var filterContext = new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object?>(),
                controller: null);

            var nextWasCalled = false;
            var startupConfigService = Mock.Of<IStartupConfigService>(service =>
                !service.IsAuthenticationRequired());

            await new RequireApiKeyManagementAccessFilter(startupConfigService).OnActionExecutionAsync(
                filterContext,
                () =>
                {
                    nextWasCalled = true;
                    return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: null));
                });

            Assert.False(nextWasCalled);
            var denied = Assert.IsType<ObjectResult>(filterContext.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
        }

        [Fact]
        public async Task GetApiKey_PrivateNetworkCaller_WhenAuthenticationDisabled_ReturnsApiKey()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var logger = NullLogger<ApiKeyController>.Instance;
            var userService = Mock.Of<IUserService>();

            configurationService
                .Setup(x => x.GetStartupConfigAsync())
                .ReturnsAsync(new StartupConfig { ApiKey = "server-api-key" });

            var controller = new ApiKeyController(
                configurationService.Object,
                userService,
                logger);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.23");
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IStartupConfigService>(new StartupConfigServiceMock(new StartupConfig { AuthenticationRequired = "false" }))
                .BuildServiceProvider();
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var actionResult = await controller.GetApiKey();

            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.NotNull(ok.Value);
            var apiKeyProp = ok.Value!.GetType().GetProperty("apiKey");
            Assert.NotNull(apiKeyProp);
            Assert.Equal("server-api-key", apiKeyProp!.GetValue(ok.Value)?.ToString());
            configurationService.Verify(x => x.GetStartupConfigAsync(), Times.Once);
            configurationService.VerifyNoOtherCalls();
        }
    }
}
