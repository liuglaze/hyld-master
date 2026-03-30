/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/11/7 2：38 
 * Description:  角色类
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public abstract class ICharacter
{
    protected CharacterBaseAttribute mAttr;
    protected GameObject mGameObject;
    protected AudioSource mAudio;
    protected IWeapon mWeapon;
    protected Animation mAnimation;
    protected NavMeshAgent mNavMeshAgent;
    protected AIFSMSystem mAIFSMSystem;
    public ICharacter() {
        MakeMFS();
    }
    public CharacterBaseAttribute Attribute{
        set { mAttr = value; }
        get { return mAttr; }
    }
    public GameObject gameObject
    {
        set
        {
            mGameObject = value;
            mNavMeshAgent = mGameObject.GetComponent<NavMeshAgent>();
            mAudio = mGameObject.GetComponent<AudioSource>();
            mAnimation = mGameObject.GetComponentInChildren<Animation>();
        }
        get
        {
            return mGameObject;
        }
    }
    public IWeapon Weapon
    {
        set 
        {
            mWeapon = value;
            mWeapon.owner = this;
            GameObject child = UnityTool.FindChild(mGameObject, "weapon-point");
            UnityTool.Attach(child, mWeapon.gameObject);
        }
    }
    public float AttackDis
    {
        get
        {
            return mWeapon.AttackDis;
        }
    }
    public Vector3 Position
    {
        get
        {
            if (mGameObject == null)
            {
                Logging.HYLDDebug.LogError("mGameObect is NULL"); return Vector3.zero;
            }
            return mGameObject.transform.position;
        }
    }
    public void Attack(ICharacter target)
    {
        //TODO
        mGameObject.transform.LookAt(target.Position);
        PlayAnim("attack");
        mWeapon.Fire();
    }
    
    public void PlayAnim(string animName)
    {
        mAnimation.CrossFade(animName);
    }
    public void SetTargetPos(Vector3 tartPosition)
    {
        PlayAnim("move");
        mNavMeshAgent.SetDestination(tartPosition);
    }
    public void Update(List<ICharacter> characters)
    {
        mWeapon.Update();
        mAIFSMSystem.currentState.Act(characters);
        mAIFSMSystem.currentState.Reason(characters);
        
    }
    private void MakeMFS()
    {
        mAIFSMSystem = new AIFSMSystem();
        IdleState idle = new IdleState(mAIFSMSystem, this);
        idle.AddTransition(FSMTransition.SeeEnemy, FSMStateID.Chase);


        ChaseState chase = new ChaseState(mAIFSMSystem, this);
        chase.AddTransition(FSMTransition.CanAttack, FSMStateID.Attack);
        chase.AddTransition(FSMTransition.NoEnemy, FSMStateID.Idle);


        AttackState attack = new AttackState(mAIFSMSystem, this);
        attack.AddTransition(FSMTransition.NoEnemy, FSMStateID.Idle);
        attack.AddTransition(FSMTransition.SeeEnemy, FSMStateID.Chase);

        mAIFSMSystem.AddState(idle, chase, attack);

    }
}
public class CharacterBaseAttribute
{
    protected string mName;//名字
    protected int mMaxHP;//血量
    protected float mMoveSpeed;//移速
    protected string mIconSprite;
    protected string mPrefabName;

    
    public CharacterBaseAttribute(string name, int maxHP, float moveSpeed, string iconSprite, string prefabName)
    {
        mName = name;
        mMaxHP = maxHP;
        mMoveSpeed = moveSpeed;
        mIconSprite = iconSprite;
        mPrefabName = prefabName;

    }

    public string Name { get { return mName; } }
    public int MaxHP { get { return mMaxHP; } }
    public float MoveSpeed { get { return mMoveSpeed; } }
    public string IconSprite { get { return mIconSprite; } }
    public string PrefabName { get { return mPrefabName; } }

}

public class HYLDCharacter : ICharacter
{
    //TODO
}