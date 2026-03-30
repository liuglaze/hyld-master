using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public class IPManager
{
	public static string GetIP(ADDRESSFAM Addfam)
	{
		if (Addfam == ADDRESSFAM.IPv6 && !Socket.OSSupportsIPv6)
		{
			return null;
		}

		string output = "";

		foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
		{
			NetworkInterfaceType _type1 = NetworkInterfaceType.Wireless80211;
			NetworkInterfaceType _type2 = NetworkInterfaceType.Ethernet;

			if ((item.NetworkInterfaceType == _type1 || item.NetworkInterfaceType == _type2) && item.OperationalStatus == OperationalStatus.Up)
			{
				foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
				{
					if (Addfam == ADDRESSFAM.IPv4)
					{
						if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
						{
							output = ip.Address.ToString();
						}
					}
					else if (Addfam == ADDRESSFAM.IPv6)
					{
						if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
						{
							output = ip.Address.ToString();
						}
					}
				}
			}
		}
		return output;
	}
}

public enum ADDRESSFAM
{
	IPv4, IPv6
}
