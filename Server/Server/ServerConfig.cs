
public class ServerConfig
{
	public const int TCPservePort = 7778;
	public const int UDPservePort = 7777;

	public const int frameTime = 16;

	public static int MaxRoom3_3Number = 6;
	public static int MaxTeam3_3Number = 2;
    public static string DOMConectStr =
      "database=hyld;data source=localhost;user=root;password=123456;pooling=false;" +
      "CharSet=utf8mb4;port=3306;allowPublicKeyRetrieval=true;SslMode=None";
}
