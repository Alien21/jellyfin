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

public class SystemManagerEnhancedTrackSelectionTests
{
    [Fact]
    public void ServerConfiguration_EnhancedTrackSelection_IsDisabledByDefault()
    {
        var configuration = new ServerConfiguration();

        Assert.False(configuration.EnableEnhancedSubtitleSelection);
        Assert.False(configuration.EnableEnhancedAudioSelection);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void GetSystemInfo_PublicAndAuthenticatedResponses_ExposeIndependentTrackSelectionState(bool subtitles, bool audio, bool authenticated)
    {
        var configuration = new ServerConfiguration
        {
            EnableEnhancedSubtitleSelection = subtitles,
            EnableEnhancedAudioSelection = audio
        };
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

        Assert.Equal(subtitles, info.EnableEnhancedSubtitleSelection);
        Assert.Equal(audio, info.EnableEnhancedAudioSelection);
    }
}
