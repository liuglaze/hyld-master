
public class ServerConfig
{
	public const int TCPservePort = 7778;

	public static int MaxRoom3_3Number = 6;
	public static int MaxTeam3_3Number = 2;

	// 说明：原先这里有一条 MySQL 连接串 DOMConectStr。
	// 账号数据已改为进程内内存库（Server/DAO/UserStore.cs），不再需要任何数据库，故移除。
}
