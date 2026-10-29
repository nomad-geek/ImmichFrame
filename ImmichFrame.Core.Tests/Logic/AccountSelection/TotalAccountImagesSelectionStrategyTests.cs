using Moq;
using NUnit.Framework;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.AccountSelection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichFrame.Core.Tests.Logic.AccountSelection
{
    [TestFixture]
    public class TotalAccountImagesSelectionStrategyTests
    {
        private Mock<IAssetAccountTracker> _mockTracker;

        [SetUp]
        public void Setup()
        {
            _mockTracker = new Mock<IAssetAccountTracker>();
            _mockTracker
                .Setup(t => t.RecordAssetLocation(It.IsAny<IAccountImmichFrameLogic>(), It.IsAny<Guid>()))
                .ReturnsAsync(true);
        }

        private TotalAccountImagesSelectionStrategy CreateStrategy(params IAccountImmichFrameLogic[] accounts)
            => new(NullLogger<TotalAccountImagesSelectionStrategy>.Instance, _mockTracker.Object, accounts.ToList());

        private static Mock<IAccountImmichFrameLogic> CreateAccount(long totalAssets, IEnumerable<AssetResponseDto> assets)
        {
            var mock = new Mock<IAccountImmichFrameLogic>();
            mock.Setup(a => a.GetTotalAssets()).ReturnsAsync(totalAssets);
            mock.Setup(a => a.GetAssets(It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync((int count, string _) => assets.Take(count));
            return mock;
        }

        private static AssetResponseDto CreateAsset() => new()
        {
            Id = Guid.NewGuid(),
            OriginalPath = "/path/asset.jpg",
            Type = AssetTypeEnum.IMAGE,
        };

        [Test]
        public async Task GetAssets_NoAccounts_ReturnsEmpty()
        {
            var strategy = CreateStrategy();

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_AllAccountsEmpty_ReturnsEmptyWithoutNaN()
        {
            var account1 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var account2 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_UnevenAccounts_SmallerAccountIsAskedForItsShare()
        {
            var largeAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var smallAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var largeAccount = CreateAccount(1000, largeAssets);
            var smallAccount = CreateAccount(100, smallAssets);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            var result = (await strategy.GetAssets()).ToList();

            // The account with the most assets is asked for a full batch, smaller ones for their share of it.
            largeAccount.Verify(a => a.GetAssets(TotalAccountImagesSelectionStrategy.BatchSize, It.IsAny<string>()), Times.Once);
            smallAccount.Verify(a => a.GetAssets(3, It.IsAny<string>()), Times.Once);

            var fromLarge = result.Count(t => ReferenceEquals(t.Item1, largeAccount.Object));
            var fromSmall = result.Count(t => ReferenceEquals(t.Item1, smallAccount.Object));
            Assert.Multiple(() =>
            {
                Assert.That(fromLarge, Is.EqualTo(10));
                Assert.That(fromSmall, Is.EqualTo(3));
            });
        }

        [Test]
        public async Task GetAssets_Proportions_AsksEachAccountForItsShareWithClientId()
        {
            var largeAssets = Enumerable.Range(0, 25).Select(_ => CreateAsset()).ToList();
            var smallAssets = Enumerable.Range(0, 25).Select(_ => CreateAsset()).ToList();
            var largeAccount = CreateAccount(1000, largeAssets);
            var smallAccount = CreateAccount(400, smallAssets);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            var result = (await strategy.GetAssets("frame-1")).ToList();

            // Proportions 1.0 and 0.4 of a batch of 25: 25 and ceil(25 * 0.4) = 10.
            largeAccount.Verify(a => a.GetAssets(25, "frame-1"), Times.Once);
            smallAccount.Verify(a => a.GetAssets(10, "frame-1"), Times.Once);
            largeAccount.Verify(a => a.GetAssets(It.IsAny<string>()), Times.Never);
            smallAccount.Verify(a => a.GetAssets(It.IsAny<string>()), Times.Never);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(35));
                Assert.That(result.Where(t => ReferenceEquals(t.Item1, largeAccount.Object)).Select(t => t.Item2),
                    Is.EquivalentTo(largeAssets.Take(25)));
                Assert.That(result.Where(t => ReferenceEquals(t.Item1, smallAccount.Object)).Select(t => t.Item2),
                    Is.EquivalentTo(smallAssets.Take(10)));
            });
        }

        [Test]
        public async Task GetAssets_KeepsEveryAssetAnAccountReturns()
        {
            // An account returning more than it was asked for is not trimmed: every returned asset is kept.
            var largeAssets = Enumerable.Range(0, 25).Select(_ => CreateAsset()).ToList();
            var smallAssets = Enumerable.Range(0, 25).Select(_ => CreateAsset()).ToList();
            var largeAccount = CreateAccount(1000, largeAssets);
            var smallAccount = new Mock<IAccountImmichFrameLogic>();
            smallAccount.Setup(a => a.GetTotalAssets()).ReturnsAsync(400L);
            smallAccount.Setup(a => a.GetAssets(It.IsAny<int>(), It.IsAny<string>())).ReturnsAsync(smallAssets);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            var result = (await strategy.GetAssets("frame-1")).ToList();

            smallAccount.Verify(a => a.GetAssets(10, "frame-1"), Times.Once);
            Assert.That(result.Select(t => t.Item2), Is.EquivalentTo(largeAssets.Concat(smallAssets)));
        }

        [Test]
        public async Task GetAssets_RecordsEveryReturnedAssetWithTracker()
        {
            var assets = Enumerable.Range(0, 5).Select(_ => CreateAsset()).ToList();
            var account = CreateAccount(5, assets);
            var strategy = CreateStrategy(account.Object);

            var result = (await strategy.GetAssets()).ToList();

            Assert.That(result, Has.Count.EqualTo(5));
            _mockTracker.Verify(
                t => t.RecordAssetLocation(account.Object, It.IsAny<Guid>()),
                Times.Exactly(5));
        }

        [Test]
        public async Task GetNextAsset_ReturnsAssetAndRecordsLocation()
        {
            var asset = CreateAsset();
            var account = CreateAccount(1, new[] { asset });
            account.Setup(a => a.GetNextAsset(It.IsAny<string>())).ReturnsAsync(asset);
            var strategy = CreateStrategy(account.Object);

            var result = await strategy.GetNextAsset();

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Item2.Id, Is.EqualTo(asset.Id));
            _mockTracker.Verify(t => t.RecordAssetLocation(account.Object, asset.Id), Times.Once);
        }
    }
}
