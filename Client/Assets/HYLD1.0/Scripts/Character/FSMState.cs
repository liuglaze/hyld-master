using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum FSMStateID
{
    None,
    Idle,
    Chase,
    Attack
}
public enum FSMTransition
{
    NullTransition,
    SeeEnemy,
    NoEnemy,
    CanAttack
}
public abstract class IFSMState
{
    protected Dictionary<FSMTransition, FSMStateID> mMap = new Dictionary<FSMTransition, FSMStateID>();
    protected FSMStateID mStateID;
    protected ICharacter mCharacter;
    protected AIFSMSystem mFSMSystem;

    public IFSMState(AIFSMSystem aIFSMSystem,ICharacter character)
    {
        mFSMSystem = aIFSMSystem;
        mCharacter = character;
    }
    public FSMStateID StateID
    { get { return mStateID; } }

    public void AddTransition(FSMTransition trans,FSMStateID id)
    {
        if(trans==FSMTransition.NullTransition)
        {
            Logging.HYLDDebug.LogError("State Error!!  trans错误");
        }
        else if(id==FSMStateID.None)
        {
            Logging.HYLDDebug.LogError("State Error!! id错误");
        }
        else if(mMap.ContainsKey(trans))
        {
            Logging.HYLDDebug.LogError("State Error!! 当前值已经存在");
        }
        else
        mMap.Add(trans, id);
    }
    public void DeleteTransition(FSMTransition trans)
    {
        if(mMap.ContainsKey(trans))
        {
            mMap.Remove(trans);
        }
        else
        {
            Logging.HYLDDebug.LogError("State Error!! 当前key不存在");
        }
    }

    public FSMStateID GetOutPutState(FSMTransition trans)
    {
        if(mMap.ContainsKey(trans))
        {
            return mMap[trans];
        }
        else
        {
            Logging.HYLDDebug.LogError("State Error!! 当前key不存在");
            return FSMStateID.None;
        }
    }

    public virtual void DoBeforeEntering() { }
    public virtual void DoBeforeLeveing() { }

    public abstract void Reason(List<ICharacter> targets);
    public abstract void Act(List<ICharacter> targets);
}

public class AIFSMSystem
{
    private List<IFSMState> mState = new List<IFSMState>();

    private IFSMState mCurrentFSMState;

    public IFSMState currentState { get { return mCurrentFSMState; } }
    public void AddState(params IFSMState[] state)
    {
        foreach(IFSMState iFSMState in state)
        {
            mState.Add(iFSMState);
        }
    }

    public void AddState(IFSMState state)
    {
        if(state==null)
        {
            Logging.HYLDDebug.LogError("FSMSystem Error！ 要添加的状态为空");
        }
        if(mState.Count==0)
        {
            mState.Add(state);
            mCurrentFSMState = state;
            return;
        }
        foreach(IFSMState s in mState)
        {
            if(s.StateID==state.StateID)
            {
                Logging.HYLDDebug.LogError($"FSMSystem Error！ 要添加的状态{s.StateID}为已经添加过了");
                return;
            }

        }
        mState.Add(state);
    }
    public void DeleteState(FSMStateID stateid)
    {
        if(stateid==FSMStateID.None)
        {
            Logging.HYLDDebug.LogError("FSMSystem Error！ 要删除的状态为空");
            return;
        }
        foreach (IFSMState s in mState)
        {
            if (s.StateID == stateid)
            {
                mState.Remove(s);
                return;
            }

        }
        Logging.HYLDDebug.LogError("FSMSystem Error！ 要删除的状态为不在状态列表");

    }

    public void PerformTransition(FSMTransition trans)
    {
        if(trans==FSMTransition.NullTransition)
        {
            Logging.HYLDDebug.LogError("FSMSystem Error！ 要转换的状态为空");
            return;
        }
        FSMStateID nextstateID = mCurrentFSMState.GetOutPutState(trans);
        if (nextstateID == FSMStateID.None)
        {
            Logging.HYLDDebug.LogError("FSMSystem Error！ 要转换的id为空");
            return;
        }
        foreach(IFSMState s in mState)
        {
            if(s.StateID==nextstateID)
            {
                mCurrentFSMState.DoBeforeLeveing();
                mCurrentFSMState = s;
                mCurrentFSMState.DoBeforeEntering();
                return;
            }
        }
        Logging.HYLDDebug.LogError("FSMSystem Error！ 要转换的id为空");
    }
}

public class IdleState : IFSMState
{
    public IdleState(AIFSMSystem aIFSMSystem, ICharacter character) : base(aIFSMSystem,character)
    {
        mStateID = FSMStateID.Idle;
    }
    public override void Act(List<ICharacter> targets)
    {
        mCharacter.PlayAnim("stand");
    }

    public override void Reason(List<ICharacter> targets)
    {
        if(targets!=null&& targets.Count > 0 )
        {
            mFSMSystem.PerformTransition(FSMTransition.SeeEnemy);
        }
    }
}
public class ChaseState : IFSMState
{
    public ChaseState(AIFSMSystem aIFSMSystem, ICharacter character) : base(aIFSMSystem,character) 
    {
        mStateID = FSMStateID.Chase;
    }
    public override void Act(List<ICharacter> targets)
    {
        if(targets!=null&&targets.Count>0)
        {
            mCharacter.SetTargetPos(targets[0].Position);
        }
    }

    public override void Reason(List<ICharacter> targets)
    {
        if(targets==null&&targets.Count==0)
        {
            mFSMSystem.PerformTransition(FSMTransition.NoEnemy);return;
        }
        float dis = Vector3.Distance(targets[0].Position, mCharacter.Position);
        if(dis<mCharacter.AttackDis)
        {
            mFSMSystem.PerformTransition(FSMTransition.CanAttack);
        }
    }
}
public class AttackState : IFSMState
{
    private float mAttackTime = 1;
    private float mAttackTimer = 1;
    public AttackState(AIFSMSystem aIFSMSystem, ICharacter character) : base(aIFSMSystem,character)
    {
        mStateID = FSMStateID.Attack;
        mAttackTimer = mAttackTime;
    }
    public override void Act(List<ICharacter> targets)
    {
        if(targets==null&&targets.Count==0)
        {
            
            return;
        }
        mAttackTimer +=Time.deltaTime;
        if(mAttackTime<mAttackTimer)
        {
            mCharacter.Attack(targets[0]);
            mAttackTimer = 0;
        }
    }

    public override void Reason(List<ICharacter> targets)
    {
        if (targets == null && targets.Count == 0)
        {
            mFSMSystem.PerformTransition(FSMTransition.NoEnemy);
            return;
        }
        float dis = Vector3.Distance(targets[0].Position, mCharacter.Position);
        if(mCharacter.AttackDis<dis)
        {
            mFSMSystem.PerformTransition(FSMTransition.SeeEnemy);
        }
    }
}

