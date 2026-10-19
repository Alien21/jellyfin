using System;
using Emby.Server.Implementations;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.System;
using MediaBrowser.Model.Updates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests;

public class SystemManagerGlobalSearchTests
{
    [Fact]
    public void ServerConfiguration_GlobalSearch_IsDisabledByDefault()
    {
        Assert.False(new ServerConfiguration().EnableGlobalSearch);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GetSystemInfo_PublicAndAuthenticatedResponses_ExposeGlobalSearchState(bool enabled, bool authenticated)
    {
        var configuration = new ServerConfiguration { EnableGlobalSearch = enabled };
        var paths = new Mock<IServerApplicationPaths>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(x => x.Configuration).Returns(configuration);
        configurationManager.Setup(x => x.CommonConfiguration).Returns(configuration);
        configurationManager.Setup(x => x.CommonApplicationPaths).Returns(paths.Object);
        configurationManager.Setup(x => x.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = "test-transcodes" });
        var installationManager = new Mock<IInstallationManager>();
        installationManager.Setup(x => x.CompletedInstallations).Returns(Array.Empty<InstallationInfo>());
        var manager = new SystemManager(
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<IServerApplicationHost>().Object,
            paths.Object,
            configurationManager.Object,
            new Mock<IStartupOptions>().Object,
            installationManager.Object,
            new Mock<ILibraryManager>().Object);
        var request = new DefaultHttpContext().Request;

        PublicSystemInfo info = authenticated ? manager.GetSystemInfo(request) : manager.GetPublicSystemInfo(request);

        Assert.Equal(enabled, info.EnableGlobalSearch);
    }
}
