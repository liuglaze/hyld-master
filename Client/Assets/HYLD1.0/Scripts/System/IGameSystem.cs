using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class IGameSystem 
{
    public virtual void Init() { }
    public virtual void Update() { }
    public virtual void Release() { }

}
