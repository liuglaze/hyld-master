using System;

namespace Server
{
	/// <summary>
	/// 服务端简单三维向量，避免依赖 Unity。
	/// </summary>
	public struct ServerVector3
	{
		public float X, Y, Z;
		public ServerVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
		public static ServerVector3 operator +(ServerVector3 a, ServerVector3 b) => new ServerVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static ServerVector3 operator *(ServerVector3 v, float s) => new ServerVector3(v.X * s, v.Y * s, v.Z * s);
		public static float Distance(ServerVector3 a, ServerVector3 b)
		{
			float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
			return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		// P3'-3: 这里原本还有 Magnitude() 与 Normalized()。
		//
		// 它们随着仿真数学的收口而失去所有调用点（唯一的用户是子弹方向归一化，
		// 现已由 PMNet.Shared.PMBattleSim.TryGetAimDirection / SpreadDirection 承担）。
		// 刻意删除而不是保留：留在那里的第二份归一化实现（含它自己的 1e-6 阈值）
		// 正是「公式会缓慢漂移」的土壤 —— 单点化就应该是真的单点。
		//
		// Distance 仍在使用（MoveAck 的位置误差判定），保留。
		// + 与 * 仍在使用（子弹推进），保留。
	}
}
