using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		public static IMongoDbConnection GetConnection()
		{
			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
		}

		/// <summary>
		/// Checks whether a MongoDB server is reachable so tests can be marked inconclusive instead of failing when no service is available.
		/// </summary>
		public static async Task<bool> IsAvailableAsync()
		{
			try
			{
				using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				var cursor = await GetConnection().Client.ListDatabaseNamesAsync(cancellationSource.Token);
				await cursor.MoveNextAsync(cancellationSource.Token);
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static async Task DropDatabaseAsync()
		{
			await GetConnection().Client.DropDatabaseAsync(GetDatabaseName());
		}
		public static void DropDatabase()
		{
			GetConnection().Client.DropDatabase(GetDatabaseName());
		}
	}
}
