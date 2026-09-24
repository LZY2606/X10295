using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace CacheTower.Tests.Utils;

public static class RedisHelper
{
	public static string Endpoint => Environment.GetEnvironmentVariable("REDIS_ENDPOINT") ?? "localhost:6379";

	private static readonly ConcurrentQueue<string> Errors = new();

	private static ConnectionMultiplexer Connection { get; set; }

	/// <summary>
	/// Whether a Redis server is reachable at <see cref="Endpoint"/>. Uses a short TCP probe so tests
	/// can be explicitly skipped (rather than failed) when no server is available.
	/// </summary>
	public static bool IsAvailable => ExternalServiceProbe.IsTcpEndpointAvailable(Endpoint);

	public static ConnectionMultiplexer GetConnection()
	{
		if (!IsAvailable)
		{
			Assert.Inconclusive($"Redis is not available at {Endpoint}; skipping Redis integration test.");
		}

		if (Connection == null)
		{
			var config = new ConfigurationOptions
			{
				AllowAdmin = true
			};
			config.EndPoints.Add(Endpoint);
			Connection = ConnectionMultiplexer.Connect(config);
			Connection.ErrorMessage += (sender, args) =>
			{
				Errors.Enqueue(args.Message);
			};
			Connection.InternalError += (sender, args) =>
			{
				if (args.Exception is not null)
				{
					Errors.Enqueue(args.Exception.Message);
				}
			};
		}
		return Connection;
	}

	/// <summary>
	/// Flushes Redis and resets the state of error logging
	/// </summary>
	public static void ResetState()
	{
		GetConnection().GetServer(Endpoint).FlushDatabase();
		GetConnection().GetSubscriber().UnsubscribeAll();
		
		//.NET Framework doesn't support `Clear()` on Errors so we do it manually
		while (!Errors.IsEmpty)
		{
			Errors.TryDequeue(out _);
		}
	}

	public static void DebugInfo(IConnectionMultiplexer connection)
	{
		Debug.WriteLine("== Redis Connection Status ==");
		Debug.WriteLine(connection.GetStatus());

		Debug.WriteLine("== Errors (Redis and Internal) ==");
		while (!Errors.IsEmpty)
		{
			if (Errors.TryDequeue(out var message))
			{
				Debug.WriteLine(message);
			}
		}
	}
}
