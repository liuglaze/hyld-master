namespace Server
{
	/// <summary>
	/// 服务端子弹数据结构（纯数据，不涉及 Unity GameObject）。
	/// </summary>
	public class ServerBullet
	{
		public int AttackId;
		public int OwnerBattleId;
		public int OwnerTeamId;
		public ServerVector3 Position;
		public ServerVector3 Direction;
		public float Speed;
		public float MaxDistance;
		public float TraveledDistance;
		public int Damage;
		public int ClientFrameId;   // V2 延迟补偿用，V1 记录但不消费
	}
}
