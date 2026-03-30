using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ZYKGameLoop : MonoSingleton<ZYKGameLoop>
{
    private ScenseStateController mScenseStateController=null;

    public ScenseStateController ZYKScenseStateController
    {
        get
        {
            return mScenseStateController;
        }
    }
    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }
    private void Start()
    {
        mScenseStateController = new ScenseStateController();
        mScenseStateController.setCurrentState(new InitState(mScenseStateController),false);
    }
    private void Update()
    {
        mScenseStateController.Update();
    }

    public void SetScenseStateController(IScenseState scense)
    {
        mScenseStateController.setCurrentState(scense);
    }
}
