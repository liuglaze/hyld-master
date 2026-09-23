// ============================================================================
//  PMR4UnityCheck / HostDependencies.cs —— 旧链边界替身已退役
// ============================================================================
//
//  本文件曾经只做一件事：为 `PMClientSessionHost` 对旧链的唯一触点
//
//      global::Server.UDPSocketManger.CloseExisting();     // 关掉旧战斗 UDP socket（硬编码 7777）
//
//  提供一个最小边界替身，好让本门禁能在真实 Unity2019 DLL 上编译新宿主的运动接线。
//
//  契约 §B（旧战斗链退役）删除了旧客户端 UDP 链，`PMClientSessionHost` 里的那个触点
//  也一并删除，因此这里**不再需要任何替身**。文件本身保留，是因为
//  `PMR4UnityCheck.csproj` 仍然 `<Compile Include="HostDependencies.cs" />` 显式编译它
//  （csproj 不在本批写入边界内），删掉文件会让门禁工程直接编不过。
//
//  边界纪律不变：门禁只编**真实**新类型源码（PMNet / PMUnity / PMMover / PMPrediction /
//  PMProjectile / PMCombat / PMR3 / Shared），不得用任何新 dummy 掩盖真实 API 的缺失。
// ============================================================================
