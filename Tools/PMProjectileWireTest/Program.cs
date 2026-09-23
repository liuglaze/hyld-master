using System;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

internal static class Program
{
    private static int passed;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        passed++;
    }
    private sealed class Filter : IPMProjectileHitFilter
    {
        public bool Accept(PMProjectileKey key, PMProjectileTargetSample target, PMVector3 impact) { return true; }
    }
    private static void Main()
    {
        var history = new PMProjectileHistory(1);
        var target = new PMProjectileTargetSample {
            Epoch=1, NetId=20, StreamVersion=1,
            ServerFrame=new PMFrameId(PMFrameDomain.AuthorityServer, 1),
            OutputFrame=new PMFrameId(PMFrameDomain.Input, 1),
            TotalSimTimeMs=0, WorldTimeMs=0, Position=new PMVector3(0,1,1),
            RadiusM=0.4f, HalfHeightM=0.9f, Alive=true
        };
        Check(history.Record(target), "record authoritative target");
        var coordinator = new PMProjectileCoordinator(1,history,new Filter(),null);
        var key = new PMProjectileKey(1,10,1,PMProjectileOrigin.ClientPredicted);
        var intent = new PMProjectileSpawnIntent {
            Key=key, ActivationId=1, Position=new PMVector3(0,1,0), Direction=new PMVector3(0,0,1),
            Yaw=0, PredictionMs=100
        };
        byte[] bytes; string error;
        Check(PMProjectileCodec.TryEncodeSpawnIntent(intent,out bytes,out error), "encode spawn: "+error);
        Check(bytes.Length<=PMProjectileCodec.MaxGeneratedRpcBytes,"spawn fits generated RPC");
        PMProjectileSpawnIntent received;
        Check(PMProjectileCodec.TryDecodeSpawnIntent(bytes,out received,out error),"decode spawn: "+error);
        var request = new PMProjectileSpawnRequest { State=new PMProjectileState {
            Key=received.Key, ActivationId=received.ActivationId, SpawnPosition=received.Position,
            Velocity=received.Direction, Yaw=received.Yaw }, PredictionMs=received.PredictionMs };
        coordinator.ResolveActivation(10,1,PMActivationResult.Confirmed,0);
        coordinator.RequestSpawn(request,10,900,1,new PMVector3(0,1,0),new PMProjectileSpec {SpeedMps=10},null,0);
        Check(coordinator.IsSpawned(key),"confirmed player projectile spawned");
        coordinator.CatchUpMotion(key,100,0);
        PMProjectileState state; PMProjectileSpec spec;
        Check(coordinator.TryObserveFrozen(key,out spec,out state),"observe projectile");
        Check(state.AuthorityNetId==900 && Math.Abs(state.Position.Z-1)<0.001f,"authority promotion and real catchup");
        Check(PMProjectileCodec.TryEncodeSnapshot(new PMProjectileSnapshot {State=state,Spec=spec},out bytes,out error),"snapshot encode: "+error);
        PMProjectileSnapshot snapshot;
        Check(PMProjectileCodec.TryDecodeSnapshot(bytes,out snapshot,out error),"snapshot decode: "+error);
        Check(snapshot.State.AuthorityNetId==900 && snapshot.State.Key==key,"snapshot identity");
        var hits = new PMProjectileHitBatch { Key=key, PreviousPosition=new PMVector3(0,1,0),
            HitPosition=new PMVector3(0,1,1), RewindMs=0,
            Targets=new [] {new PMProjectileHitCandidate { TargetNetId=20,TargetStreamVersion=1,
                TargetServerFrame=target.ServerFrame,ImpactPoint=new PMVector3(0,1,1) }} };
        Check(PMProjectileCodec.TryEncodeHitBatch(hits,out bytes,out error),"hit encode: "+error);
        PMProjectileHitBatch decodedHits;
        Check(PMProjectileCodec.TryDecodeHitBatch(bytes,out decodedHits,out error),"hit decode: "+error);
        coordinator.ReportHits(decodedHits,99,0);
        PMProjectileSettlement[] settlements;
        Check(coordinator.DrainSettlements(32,out settlements)==0,"wrong owner cannot settle");
        coordinator.ReportHits(decodedHits,10,0);
        Check(coordinator.DrainSettlements(32,out settlements)==1,"one accepted settlement");
        Check(settlements[0].Hits.Length==1 && settlements[0].Hits[0].TargetNetId==20,"correct target");
        coordinator.ReportHits(decodedHits,10,0);
        Check(coordinator.DrainSettlements(32,out settlements)==0,"duplicate hit cannot settle twice");
        var decision = new PMProjectileDecision { Key=key,ActivationId=1,Result=PMActivationResult.Confirmed };
        Check(PMProjectileCodec.TryEncodeDecision(decision,out bytes,out error),"decision encode: "+error);
        PMProjectileDecision decodedDecision;
        Check(PMProjectileCodec.TryDecodeDecision(bytes,out decodedDecision,out error),"decision decode: "+error);
        Check(decodedDecision.Result==PMActivationResult.Confirmed,"decision roundtrip");
        Console.WriteLine("PMProjectileWireTest PASS "+passed+" FAIL 0 (in-process bytes, not network/Unity)");
    }
}
