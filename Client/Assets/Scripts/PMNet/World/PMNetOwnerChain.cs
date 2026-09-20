namespace PMNet
{
    /// <summary>
    /// 控制器类网络对象（对应 UE 的 `APlayerController`）。
    ///
    /// 它是 Owner 链的**终点**：链走到控制器就必须停下，靠它直接持有的连接完成归属推导。
    ///
    /// 对齐 UE 的 `APlayerController::GetNetConnection()`（`PlayerController.cpp:276`）：
    /// <code>
    /// // A controller without a player has no "owner"
    /// return (Player != NULL) ? NetConnection : NULL;
    /// </code>
    ///
    /// **关键细节：它不回退到基类的 Owner 链。** 没有 Player 就是 null，
    /// 即使这个控制器自己还有 Owner。这条不对称规则很容易在"统一成沿 Owner 往上走"的
    /// 重构里被抹掉，从而让未绑定玩家的控制器错误地被认为是某个连接的拥有者，
    /// 进而让 RPC 归属校验放行本该拒绝的服务端 RPC。
    /// </summary>
    public class PMNetControllerObject : PMNetObject
    {
        /// <summary>
        /// 本控制器是否已经绑定了玩家。
        ///
        /// 对应 UE 的 `Player != NULL`。在本项目里"玩家"就是一条连接，
        /// 所以用 `NetConnection != null` 表达；单独留出这个属性是为了让
        /// UE 源码的判据在代码里一眼可辨。
        /// </summary>
        public bool HasPlayer
        {
            get { return NetConnection != null; }
        }

        /// <summary>对齐 UE：没有 Player 即 null，**不回退基类**。</summary>
        public override PMNetConnection GetNetConnection()
        {
            return HasPlayer ? NetConnection : null;
        }

        /// <summary>对齐 UE `APlayerController::GetNetOwningPlayer()`：直接返回 Player。</summary>
        public override PMNetConnection GetNetOwningPlayer()
        {
            return NetConnection;
        }
    }

    /// <summary>
    /// 被控制的实体类网络对象（对应 UE 的 `APawn`）。
    ///
    /// 对齐 UE 的 `APawn::GetNetConnection()`（`Pawn.cpp:736`）：
    /// <code>
    /// if (GetController()) { return GetController()-&gt;GetNetConnection(); }
    /// return Super::GetNetConnection();
    /// </code>
    ///
    /// 注意这里的**回退**：没有控制器时才走 Owner 链，而控制器那条分支是终局。
    /// 与 `PMNetControllerObject` 恰好相反，两条规则不能合并成一条。
    /// </summary>
    public class PMNetPawnObject : PMNetObject
    {
        /// <summary>当前控制器；null 表示未被控制（对应 `APawn::GetController()`）。</summary>
        public PMNetObject Controller;

        /// <summary>对齐 UE：有控制器走控制器，否则回退 Owner 链。</summary>
        public override PMNetConnection GetNetConnection()
        {
            if (Controller != null)
            {
                return Controller.GetNetConnection();
            }

            return base.GetNetConnection();
        }

        /// <summary>
        /// 对齐 UE `APawn::GetNetOwningPlayer()`（`Pawn.cpp:751`）：
        /// 只有权威角色、且控制器确实是控制器类对象时才取它的 Player，否则回退基类。
        /// </summary>
        public override PMNetConnection GetNetOwningPlayer()
        {
            if (Role == PMNetRole.Authority)
            {
                if (Controller != null)
                {
                    PMNetControllerObject pc = Controller as PMNetControllerObject;
                    if (pc != null)
                    {
                        return pc.NetConnection;
                    }
                }
            }

            return base.GetNetOwningPlayer();
        }
    }
}
