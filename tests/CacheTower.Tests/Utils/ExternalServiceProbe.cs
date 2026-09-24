using System.Net.Sockets;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Probes whether an external TCP service is reachable, used to explicitly skip integration
/// tests (via Assert.Inconclusive) when the required service is not available on the machine.
/// </summary>
public static class ExternalServiceProbe
{
	public static bool IsTcpEndpointAvailable(string host, int port, int timeoutMilliseconds = 500)
	{
		try
		{
			using var client = new TcpClient();
			var connectTask = client.ConnectAsync(host, port);
			return connectTask.Wait(timeoutMilliseconds) && client.Connected;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsTcpEndpointAvailable(string hostAndPort)
	{
		var separatorIndex = hostAndPort.LastIndexOf(':');
		if (separatorIndex < 0)
		{
			return IsTcpEndpointAvailable(hostAndPort, 6379);
		}

		var host = hostAndPort.Substring(0, separatorIndex);
		if (!int.TryParse(hostAndPort.Substring(separatorIndex + 1), out var port))
		{
			return false;
		}

		return IsTcpEndpointAvailable(host, port);
	}
}
