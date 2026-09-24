using System;
using System.Threading.Tasks;
using CacheTower.Providers.Database.MongoDB;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoFramework;
using MongoFramework.Infrastructure.Indexing;
using NSubstitute;

namespace CacheTower.Tests.Providers.Database;

[TestClass]
public class MongoDbCacheLayerTests : BaseCacheLayerTests
{
	[TestInitialize]
	public async Task Setup()
	{
		if (!await MongoDbHelper.IsAvailableAsync())
		{
			Assert.Inconclusive("MongoDB is not available at " + MongoDbHelper.ConnectionString);
		}
		await MongoDbHelper.DropDatabaseAsync();
	}

	[TestCleanup]
	public async Task Cleanup()
	{
		if (await MongoDbHelper.IsAvailableAsync())
		{
			await MongoDbHelper.DropDatabaseAsync();
		}
	}

	[TestMethod]
	public async Task GetSetCache()
	{
		await AssertGetSetCacheAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()));
	}

	[TestMethod]
	public async Task IsCacheAvailable()
	{
		EntityIndexWriter.ClearCache();

		await AssertCacheAvailabilityAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()), true);

		var connectionMock = Substitute.For<IMongoDbConnection>();
		connectionMock.GetDatabase().Returns(x => throw new Exception());
		EntityIndexWriter.ClearCache();

		await AssertCacheAvailabilityAsync(new MongoDbCacheLayer(connectionMock), false);
	}

	[TestMethod]
	public async Task EvictFromCache()
	{
		await AssertCacheEvictionAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()));
	}

	[TestMethod]
	public async Task FlushFromCache()
	{
		await AssertCacheFlushAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()));
	}

	[TestMethod]
	public async Task CacheCleanup()
	{
		await AssertCacheCleanupAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()));
	}

	[TestMethod]
	public async Task CachingComplexTypes()
	{
		await AssertComplexTypeCachingAsync(new MongoDbCacheLayer(MongoDbHelper.GetConnection()));
	}
}
