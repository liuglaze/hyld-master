using System.Collections;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using System.Threading;

public  class IScenseState 
{
    private string mString;
    protected ScenseStateController mScenseStateController;
    public IScenseState(string name,ScenseStateController scenseStateController)
    {
        mString = name;
        mScenseStateController = scenseStateController;
    }
    public string ScenseName
    {
        get { return mString; }
    }
    public virtual void Update() { }
    public virtual void End() { }
    public virtual void Start() { }
}

public class ScenseStateController
{
    private IScenseState mISceseState;
    private AsyncOperation mAO;
    private bool mIsLoadingScese;
    public void setCurrentState(IScenseState state,bool isNeedloadScence=true)
    {
        if(state!=null)
        {
            state.End();
        }
        mISceseState = state;
        if(isNeedloadScence)
        {
            mAO = SceneManager.LoadSceneAsync(state.ScenseName);
            mIsLoadingScese = true;
        }
        else
        {
            mISceseState.Start();
            mIsLoadingScese = false;
        }
    }
    public void Update()
    {
        if(mAO != null && mAO.isDone == false)
        {
            return;
        }
        if(mAO!=null&&mAO.isDone==true&&mIsLoadingScese)
        {
            mISceseState.Start();
            mIsLoadingScese = false;
        }
        if(mISceseState!=null)
        {
            mISceseState.Update();
        }
    }
}

public class InitState : IScenseState
{
    private Image logo;
    private float time;
    public InitState(ScenseStateController scenseStateController) : base("HYLDInit", scenseStateController)
    {
    }
    public override void Start()
    {
        base.Start();
        logo = GameObject.Find("logo").GetComponent<Image>();
        logo.color = Color.black;
        time = Time.time;
    }
    public override void Update()
    {
        base.Update();
        logo.color = Color.Lerp(logo.color, Color.white, 4 * Time.deltaTime);
        if(Time.time-time>2)
        {
            mScenseStateController.setCurrentState(new StartMenuState(mScenseStateController));
        }
       
    }
}
public class StartMenuState : IScenseState
{
    
    public StartMenuState(ScenseStateController scenseStateController) : base("HuangYeLuanDouStart", scenseStateController)
    {
    }
    public override void Start()
    {
        base.Start();
        GameObject.FindGameObjectWithTag("UI").gameObject.transform.Find("UI").gameObject.transform.Find("Hall").gameObject.transform.Find("StartGameDanji").GetComponent<Button>().onClick.AddListener(OnStartButtonDownDanJi);
    }
    private void OnStartButtonDownDanJi()
    {

        mScenseStateController.setCurrentState(new GameState(mScenseStateController));
    }
    
}
public class GameState : IScenseState
{
    public GameState(ScenseStateController scenseStateController) : base("HYLDGame", scenseStateController)
    {

    }
}

public class TryGameState : IScenseState
{

    public TryGameState(ScenseStateController scenseStateController) : base("HYLDTryGame", scenseStateController)
    {
    }
    public override void Start()
    {
        base.Start();
        GameObject.FindGameObjectWithTag("UI").gameObject.transform.Find("Button").GetComponent<Button>().onClick.AddListener(OnStartButtonDownDanJi);
    }
    private void OnStartButtonDownDanJi()
    {
        mScenseStateController.setCurrentState(new StartMenuState(mScenseStateController));
    }

}