using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		/// <summary>
		/// Whether a MongoDB server is reachable at <see cref="ConnectionString"/>. Uses a short TCP probe so tests
		/// can be explicitly skipped (rather than failed) when no server is available.
		/// </summary>
		public static bool IsAvailable
		{
			get
			{
				var server = new MongoUrl(ConnectionString).Server;
				return ExternalServiceProbe.IsTcpEndpointAvailable(server.Host, server.Port);
			}
		}

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		public static IMongoDbConnection GetConnection()
		{
			if (!IsAvailable)
			{
				Assert.Inconclusive($"MongoDB is not available at {ConnectionString}; skipping MongoDB integration test.");
			}

			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
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
