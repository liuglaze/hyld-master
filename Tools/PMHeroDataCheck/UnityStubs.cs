// PMHeroDataCheck 专用的最小 Unity 桩件。
//
// 只提供 HeroData.cs 真正用到的类型（当前只有 GameObject）。
// 刻意不做成「大而全的 Unity 替身」：桩件越大，它与真实 Unity 的偏差就越可能掩盖问题。
// 一旦 HeroData.cs 需要新的 Unity 类型，这里补一个即可 —— 补桩件本身就是一个有意义的信号
// （说明数据模型开始依赖更多引擎能力，那通常不是好事）。

namespace UnityEngine
{
    /// <summary>场景对象的桩件。HeroData 只把它当作「预制体引用」保存与传递，不需要任何行为。</summary>
    public class Object
    {
        public string name { get; set; }

        public static bool operator ==(Object a, Object b)
        {
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b)
        {
            return !ReferenceEquals(a, b);
        }

        public override bool Equals(object other)
        {
            return ReferenceEquals(this, other);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override string ToString()
        {
            return name ?? "<Object>";
        }
    }

    /// <summary>GameObject 的桩件。只作为预制体/实体引用的容器。</summary>
    public class GameObject : Object
    {
        public GameObject()
        {
        }

        public GameObject(string name)
        {
            this.name = name;
        }
    }
}
