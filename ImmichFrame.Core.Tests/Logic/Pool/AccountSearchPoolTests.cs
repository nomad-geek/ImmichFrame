using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class AccountSearchPoolTests
{
    private Mock<IApiCache> _cache;
    private Mock<ImmichApi> _api;
    private Mock<IAccountSettings> _settings;
    private AccountSearchPool _pool;

    [SetUp]
    public void Setup()
    {
        _cache = new Mock<IApiCache>();
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<ICollection<TagResponseDto>>>>()))
            .Returns<string, Func<Task<ICollection<TagResponseDto>>>>((_, factory) => factory());
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<long>>>()))
            .Returns<string, Func<Task<long>>>((_, factory) => factory());
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<IReadOnlyList<Guid>>>>()))
            .Returns<string, Func<Task<IReadOnlyList<Guid>>>>((_, factory) => factory());
        _api = new Mock<ImmichApi>(null, null);
        _api.Setup(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetResponseDto>());
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 0 });
        _settings = new Mock<IAccountSettings>();
        _settings.SetupGet(s => s.ImmichServerUrl).Returns("https://photos.example");
        _settings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _settings.SetupGet(s => s.People).Returns(new List<Guid>());
        _settings.SetupGet(s => s.Tags).Returns(new List<string>());
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>());
        _pool = new AccountSearchPool(_cache.Object, _api.Object, _settings.Object);
    }

    [Test]
    public async Task GetAssets_WithoutSources_SearchesTheLibrary()
    {
        await _pool.GetAssets(5);

        VerifyRandom(dto =>
            dto.Size == 5 &&
            dto.WithExif == true &&
            dto.WithPeople == true &&
            dto.Filter!.Or == null &&
            dto.Filter.TrashedAt != null &&
            dto.Filter.AlbumIds == null &&
            dto.Filter.Visibility!.In.Contains(AssetVisibility.Timeline) &&
            !dto.Filter.Visibility.In.Contains(AssetVisibility.Archive) &&
            dto.Filter.Type!.In.Contains(AssetTypeEnum.IMAGE) &&
            !dto.Filter.Type.In.Contains(AssetTypeEnum.VIDEO));
    }

    [Test]
    public async Task GetAssets_ShowVideos_AddsVideoType()
    {
        _settings.SetupGet(s => s.ShowVideos).Returns(true);

        await _pool.GetAssets(1);

        VerifyRandom(dto =>
            dto.Filter!.Type!.In.Contains(AssetTypeEnum.IMAGE) &&
            dto.Filter.Type.In.Contains(AssetTypeEnum.VIDEO));
    }

    [Test]
    public async Task GetAssets_ShowArchived_AddsArchiveVisibility()
    {
        _settings.SetupGet(s => s.ShowArchived).Returns(true);

        await _pool.GetAssets(1);

        VerifyRandom(dto =>
            dto.Filter!.Visibility!.In.Contains(AssetVisibility.Timeline) &&
            dto.Filter.Visibility.In.Contains(AssetVisibility.Archive));
    }

    [Test]
    public async Task GetAssets_Rating_SetsGte()
    {
        _settings.SetupGet(s => s.Rating).Returns(3);

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.Rating!.Gte == 3);
    }

    [Test]
    public async Task GetAssets_ImagesFromDays_SetsTakenAfter()
    {
        _settings.SetupGet(s => s.ImagesFromDays).Returns(10);
        var expected = DateTime.Today.AddDays(-10);

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.TakenAt!.Gt.HasValue && dto.Filter.TakenAt.Gt.Value.Date == expected.Date);
    }

    [Test]
    public async Task GetAssets_ImagesFromDate_WinsOverFromDays()
    {
        var fromDate = new DateTime(2020, 6, 1);
        _settings.SetupGet(s => s.ImagesFromDate).Returns(fromDate);
        _settings.SetupGet(s => s.ImagesFromDays).Returns(10);

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.TakenAt!.Gt.HasValue && dto.Filter.TakenAt.Gt.Value.Date == fromDate.Date);
    }

    [Test]
    public async Task GetAssets_ImagesUntilDate_SetsTakenBefore()
    {
        var until = new DateTime(2024, 1, 1);
        _settings.SetupGet(s => s.ImagesUntilDate).Returns(until);

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.TakenAt!.Lt.HasValue && dto.Filter.TakenAt.Lt.Value.Date == until.Date);
    }

    [Test]
    public async Task GetAssets_ExcludedAlbums_SentAsNone()
    {
        var excludedAlbumId = Guid.NewGuid();
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.AlbumIds!.None.Contains(excludedAlbumId));
        _api.Verify(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssets_EmptyExcludedAlbums_OmitsAlbumFilter()
    {
        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.AlbumIds == null);
    }

    [Test]
    public async Task GetAssets_NullExcludedAlbums_OmitsAlbumFilter()
    {
        _settings.SetupGet(s => s.ExcludedAlbums).Returns((List<Guid>)null!);

        await _pool.GetAssets(1);

        VerifyRandom(dto => dto.Filter!.AlbumIds == null);
    }

    [Test]
    public async Task GetAssets_WithSources_OrsOnlyConfiguredBranches()
    {
        var albumId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.Albums).Returns(new List<Guid> { albumId });
        _settings.SetupGet(s => s.People).Returns(new List<Guid> { personId });
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        await _pool.GetAssets(1);

        VerifyRandom(dto =>
            dto.Filter!.AlbumIds!.None.Contains(excludedAlbumId) &&
            dto.Filter.Or.Count == 3 &&
            dto.Filter.Or.Any(branch => branch.IsFavorite != null && branch.IsFavorite.Eq) &&
            dto.Filter.Or.Any(branch => branch.AlbumIds != null && branch.AlbumIds.Any.Contains(albumId) && branch.AlbumIds.None == null) &&
            dto.Filter.Or.Any(branch => branch.PersonIds != null && branch.PersonIds.Any.Contains(personId)));
    }

    [Test]
    public async Task GetAssets_Tags_MatchFullPathCaseSensitively()
    {
        var match = Guid.NewGuid();
        var wrongCase = Guid.NewGuid();
        var wrongParent = Guid.NewGuid();
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Parent/Child" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>
            {
                new() { Id = match, Value = "Parent/Child" },
                new() { Id = wrongCase, Value = "parent/child" },
                new() { Id = wrongParent, Value = "Other/Child" }
            });

        await _pool.GetAssets(1);

        VerifyRandom(dto =>
            dto.Filter!.Or.Count == 1 &&
            dto.Filter.Or.Single().TagIds!.Any.Contains(match) &&
            !dto.Filter.Or.Single().TagIds.Any.Contains(wrongCase) &&
            !dto.Filter.Or.Single().TagIds.Any.Contains(wrongParent));
    }

    [Test]
    public async Task GetAssets_UnmatchedTagsOnly_DoesNotSearch()
    {
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        var result = await _pool.GetAssets(5);

        Assert.That(result, Is.Empty);
        _api.Verify(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssetCount_UsesTheSameFilter()
    {
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 12 });

        var count = await _pool.GetAssetCount();

        Assert.That(count, Is.EqualTo(12));
        _api.Verify(a => a.SearchAssetStatisticsAsync(It.Is<StatisticsSearchDto>(dto =>
            dto.Filter!.Or.Any(branch => branch.IsFavorite != null && branch.IsFavorite.Eq) &&
            dto.Filter.Visibility!.In.Contains(AssetVisibility.Timeline)
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetAssetCount_UnmatchedTagsOnly_IsZeroWithoutStatisticsCall()
    {
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        var count = await _pool.GetAssetCount();

        Assert.That(count, Is.EqualTo(0));
        _api.Verify(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssetCount_SameFilter_IsServedFromCache()
    {
        using var cache = new ApiCache(TimeSpan.FromHours(1));
        var pool = new AccountSearchPool(cache, _api.Object, _settings.Object);
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 7 });

        Assert.That(await pool.GetAssetCount(), Is.EqualTo(7));
        Assert.That(await pool.GetAssetCount(), Is.EqualTo(7));

        _api.Verify(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetAllAssetIds_PagesWithTheCursor()
    {
        var excludedAlbumId = Guid.NewGuid();
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });
        var page1 = Ids("page1", 3);
        var page2 = Ids("page2", 2);
        var page3 = Ids("page3", 1);
        var requests = new List<MetadataSearchDto>();
        _api.Setup(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string slug, MetadataSearchDto dto, CancellationToken ct) =>
            {
                requests.Add(dto);
                return dto.Cursor switch
                {
                    null => SearchPage(page1, "cursor-1"),
                    "cursor-1" => SearchPage(page2, "cursor-2"),
                    "cursor-2" => SearchPage(page3, null),
                    _ => throw new InvalidOperationException($"Unexpected cursor {dto.Cursor}")
                };
            });

        var ids = await _pool.GetAllAssetIds();

        Assert.That(ids, Is.EqualTo(page1.Concat(page2).Concat(page3)));
        Assert.That(requests, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(requests[0].Cursor, Is.Null);
            Assert.That(requests[1].Cursor, Is.EqualTo("cursor-1"));
            Assert.That(requests[2].Cursor, Is.EqualTo("cursor-2"));
            foreach (var dto in requests)
            {
                Assert.That(dto.Size, Is.EqualTo(1000));
                Assert.That(dto.WithExif, Is.False);
                Assert.That(dto.Filter!.Or.Any(branch => branch.IsFavorite != null && branch.IsFavorite.Eq), Is.True);
                Assert.That(dto.Filter.AlbumIds!.None, Does.Contain(excludedAlbumId));
                Assert.That(dto.Filter.TrashedAt, Is.Not.Null);
                Assert.That(dto.Filter.Visibility!.In, Does.Contain(AssetVisibility.Timeline));
                Assert.That(dto.Filter.Type!.In, Does.Contain(AssetTypeEnum.IMAGE));
            }
        });
        _api.Verify(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAllAssetIds_UnmatchedTagsOnly_IsEmptyWithoutSearch()
    {
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        var ids = await _pool.GetAllAssetIds();

        Assert.That(ids, Is.Empty);
        _api.Verify(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LoadAssetsByIds_KeepsTheInputOrder()
    {
        var ids = Ids("ordered", 8);
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            // Later IDs answer sooner, so completion order differs from input order.
            var delay = TimeSpan.FromMilliseconds((ids.Count - i) * 10);
            _api.Setup(a => a.GetAssetInfoAsync(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    await Task.Delay(delay);
                    return Asset(id);
                });
        }

        var loaded = await _pool.LoadAssetsByIds(ids);

        Assert.That(loaded.Select(a => a.Id), Is.EqualTo(ids));
    }

    [Test]
    public async Task LoadAssetsByIds_SkipsDeletedAndTrashedAssets()
    {
        var kept1 = FixtureHelpers.GuidFor("kept1");
        var deleted = FixtureHelpers.GuidFor("deleted");
        var trashed = FixtureHelpers.GuidFor("trashed");
        var kept2 = FixtureHelpers.GuidFor("kept2");
        SetupAssetInfo(kept1);
        _api.Setup(a => a.GetAssetInfoAsync(deleted, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiError(404));
        _api.Setup(a => a.GetAssetInfoAsync(trashed, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponseDto { Id = trashed, Type = AssetTypeEnum.IMAGE, IsTrashed = true });
        SetupAssetInfo(kept2);

        var loaded = await _pool.LoadAssetsByIds(new List<Guid> { kept1, deleted, trashed, kept2 });

        Assert.That(loaded.Select(a => a.Id), Is.EqualTo(new[] { kept1, kept2 }));
    }

    [Test]
    public void LoadAssetsByIds_ServerError_IsRethrown()
    {
        var kept = FixtureHelpers.GuidFor("kept");
        var failing = FixtureHelpers.GuidFor("failing");
        SetupAssetInfo(kept);
        _api.Setup(a => a.GetAssetInfoAsync(failing, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiError(500));

        var ex = Assert.ThrowsAsync<ApiException>(() => _pool.LoadAssetsByIds(new List<Guid> { kept, failing }));

        Assert.That(ex!.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task GetAssets_RandomMode_NeverListsOrLoadsById()
    {
        await _pool.GetAssets(25, "frame-1");

        VerifyRandom(dto => dto.Size == 25);
        _api.Verify(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _api.Verify(a => a.GetAssetInfoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssets_ExhaustiveMode_ListsAndLoadsByIdWithoutRandomSearch()
    {
        var ids = Ids("exhaustive", 10);
        _api.Setup(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchPage(ids, null));
        foreach (var id in ids)
        {
            SetupAssetInfo(id);
        }

        var dealer = new ShuffleDeckPool(_pool);

        var result = (await dealer.GetAssets(5, "frame-1")).ToList();

        Assert.That(result, Has.Count.EqualTo(5));
        Assert.That(result.Select(a => a.Id), Is.Unique);
        Assert.That(ids, Is.SupersetOf(result.Select(a => a.Id)));
        _api.Verify(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(a => a.GetAssetInfoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        _api.Verify(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static List<Guid> Ids(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => FixtureHelpers.GuidFor($"{prefix}_{i}")).ToList();

    private static AssetResponseDto Asset(Guid id) => new()
    {
        Id = id,
        Type = AssetTypeEnum.IMAGE,
        IsTrashed = false,
        ExifInfo = new ExifResponseDto()
    };

    private void SetupAssetInfo(Guid id) =>
        _api.Setup(a => a.GetAssetInfoAsync(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Asset(id));

    private static ApiException ApiError(int statusCode) =>
        new($"Status {statusCode}", statusCode, string.Empty, new Dictionary<string, IEnumerable<string>>(), null);

    internal static SearchResponseDto SearchPage(IReadOnlyCollection<Guid> ids, string? nextCursor) => new()
    {
        Albums = new SearchAlbumResponseDto
        {
            Count = 0,
            Total = 0,
            Items = new List<AlbumResponseDto>(),
            Facets = new List<SearchFacetResponseDto>()
        },
        Assets = new SearchAssetResponseDto
        {
            Count = ids.Count,
            Total = ids.Count,
            Items = ids.Select(id => new AssetResponseDto { Id = id, Type = AssetTypeEnum.IMAGE }).ToList(),
            Facets = new List<SearchFacetResponseDto>(),
            NextCursor = nextCursor,
            NextPage = null
        }
    };

    private void VerifyRandom(Func<RandomSearchDto, bool> match) =>
        _api.Verify(a => a.SearchRandomAsync(It.Is<RandomSearchDto>(dto => match(dto)), It.IsAny<CancellationToken>()), Times.Once);
}
