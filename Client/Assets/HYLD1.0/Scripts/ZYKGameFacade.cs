using System.Collections;
using System.Collections.Generic;

//外观模式 中介者
public class ZYKGameFacade :LZJSingleModen<ZYKGameFacade>
{
    private bool misGameOver = false;

    public bool isGameOver { get { return misGameOver; } }

    private ZYKGameFacade() { }

    private GameEventSystem mGameEventSystem;
    private CharacterSystem mCharacterSystem;
    private ArchievementSystem mArchievementSystem;
    
    

    public void Update()
    {
        mGameEventSystem.Update();
        mCharacterSystem.Update();
        mArchievementSystem.Update();
    }
    public void Init()
    {
        mGameEventSystem = new GameEventSystem();
        mCharacterSystem = new CharacterSystem();
        mArchievementSystem = new ArchievementSystem();


        mGameEventSystem.Init();
        mCharacterSystem.Init();
        mArchievementSystem.Init();
    }

    public void Release()
    {
        mGameEventSystem.Release();
        mCharacterSystem.Release();
        mArchievementSystem.Release();
    }
}
