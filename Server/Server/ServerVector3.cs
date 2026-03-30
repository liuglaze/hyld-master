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
		public float Magnitude() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
		public ServerVector3 Normalized()
		{
			float m = Magnitude();
			return m > 1e-6f ? new ServerVector3(X / m, Y / m, Z / m) : new ServerVector3(0, 0, 0);
		}
	}
}
