using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Verifies the optional Next Up resume threshold against the SQLite query provider.
/// </summary>
public sealed class NextUpResumeThresholdTests : SqliteDbTestFixture
{
    private const string SeriesKey = "threshold-series";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";
    private static readonly DateTime _lastPlayedDate = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly User _user = new("test", "auth-provider", "reset-provider");
    private readonly ServerConfiguration _configuration = new()
    {
        EnableNextUpResumeThreshold = true,
        MinResumePct = 2
    };

    private readonly NextUpService _service;

    public NextUpResumeThresholdTests()
    {
        using (var context = CreateDbContext())
        {
            context.Users.Add(_user);
            context.SaveChanges();
        }

        var itemTypeLookup = new ItemTypeLookup();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.Configuration).Returns(() => _configuration);
        _service = new NextUpService(
            CreateDbContextFactory(),
            itemTypeLookup,
            CreateBaseItemRepository(itemTypeLookup),
            configurationManager.Object);
    }

    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(2, 199, false)]
    [InlineData(2, 200, true)]
    [InlineData(2, 201, true)]
    [InlineData(7, 699, false)]
    [InlineData(7, 700, true)]
    [InlineData(7, 701, true)]
    [InlineData(29, 2_900, true)]
    public void GetNextUpSeriesKeys_ResumeThreshold_UsesConfiguredPercentage(int minResumePct, long positionTicks, bool included)
    {
        _configuration.MinResumePct = minResumePct;
        SeedEpisode(SeriesKey, 10_000, positionTicks);

        var result = _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1));

        Assert.Equal(included, result.Contains(SeriesKey));
    }

    [Fact]
    public void GetNextUpSeriesKeys_ConfigurationChanges_UsesCurrentPercentageAndSwitch()
    {
        SeedEpisode(SeriesKey, 10_000, 300);

        Assert.Contains(SeriesKey, _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));

        _configuration.MinResumePct = 7;
        Assert.Empty(_service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));

        _configuration.EnableNextUpResumeThreshold = false;
        Assert.Contains(SeriesKey, _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));
    }

    [Fact]
    public void GetNextUpSeriesKeys_PlayedEpisodeWithClearedPosition_RemainsEligible()
    {
        SeedEpisode(SeriesKey, null, 0, played: true);

        Assert.Contains(SeriesKey, _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void GetNextUpSeriesKeys_UnplayedEpisodeWithoutPositiveRuntime_IsExcluded(long? runtimeTicks)
    {
        SeedEpisode(SeriesKey, runtimeTicks, 200);

        Assert.Empty(_service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));
    }

    [Fact]
    public void GetNextUpSeriesKeys_ThresholdDisabled_PreservesDateOnlyEligibility()
    {
        _configuration.EnableNextUpResumeThreshold = false;
        SeedEpisode(SeriesKey, 10_000, 0);

        Assert.Contains(SeriesKey, _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));
    }

    [Theory]
    [InlineData(200L, false)]
    [InlineData(400L, true)]
    public void GetNextUpSeriesKeys_ProgressOnAlternateVersion_UsesThatVersionsRuntime(long positionTicks, bool included)
    {
        var primaryId = SeedEpisode(SeriesKey, 10_000, 0);
        SeedEpisode(SeriesKey, 20_000, positionTicks, primaryVersionId: primaryId);

        var result = _service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1));

        Assert.Equal(included, result.Contains(SeriesKey));
    }

    [Fact]
    public void GetNextUpSeriesKeys_QualifyingProgress_RespectsUserLibraryTypeAndDate()
    {
        var otherUser = new User("other", "auth-provider", "reset-provider");
        using (var context = CreateDbContext())
        {
            context.Users.Add(otherUser);
            context.SaveChanges();
        }

        var episodeId = SeedEpisode("other-user-series", 10_000, 0);
        using (var context = CreateDbContext())
        {
            context.UserData.Add(new UserData
            {
                ItemId = episodeId,
                UserId = otherUser.Id,
                CustomDataKey = episodeId.ToString("N"),
                PlaybackPositionTicks = 200,
                LastPlayedDate = _lastPlayedDate,
                Item = null!,
                User = null!
            });
            context.SaveChanges();
        }

        SeedEpisode("other-library-series", 10_000, 200, libraryId: Guid.NewGuid());
        SeedEpisode("movie-series", 10_000, 200, type: "MediaBrowser.Controller.Entities.Movies.Movie");
        SeedEpisode("old-series", 10_000, 200, lastPlayedDate: _lastPlayedDate.AddDays(-2));

        Assert.Empty(_service.GetNextUpSeriesKeys(CreateFilter(), _lastPlayedDate.AddDays(-1)));
    }

    [Fact]
    public void GetNextUpSeriesKeys_ThresholdAppliedBeforeLimit_ReturnsMostRecentQualifyingSeries()
    {
        SeedEpisode("most-recent-below-threshold", 10_000, 199);
        SeedEpisode("most-recent-qualified", 10_000, 200, lastPlayedDate: _lastPlayedDate.AddHours(-1));
        SeedEpisode("older-qualified", 10_000, 200, lastPlayedDate: _lastPlayedDate.AddHours(-2));
        var filter = CreateFilter();
        filter.Limit = 1;

        var result = _service.GetNextUpSeriesKeys(filter, _lastPlayedDate.AddDays(-1));

        Assert.Equal("most-recent-qualified", Assert.Single(result));
    }

    private InternalItemsQuery CreateFilter() => new(_user)
    {
        TopParentIds = [_libraryId]
    };

    private Guid SeedEpisode(
        string seriesKey,
        long? runtimeTicks,
        long positionTicks,
        bool played = false,
        Guid? primaryVersionId = null,
        Guid? libraryId = null,
        string type = EpisodeType,
        DateTime? lastPlayedDate = null)
    {
        using var context = CreateDbContext();
        var id = Guid.NewGuid();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = type,
            Name = seriesKey,
            SeriesPresentationUniqueKey = seriesKey,
            PresentationUniqueKey = (primaryVersionId ?? id).ToString("N"),
            PrimaryVersionId = primaryVersionId,
            TopParentId = libraryId ?? _libraryId,
            RunTimeTicks = runtimeTicks
        });
        context.UserData.Add(new UserData
        {
            ItemId = id,
            UserId = _user.Id,
            CustomDataKey = id.ToString("N"),
            PlaybackPositionTicks = positionTicks,
            LastPlayedDate = lastPlayedDate ?? _lastPlayedDate,
            Played = played,
            Item = null!,
            User = null!
        });
        context.SaveChanges();

        return id;
    }
}
