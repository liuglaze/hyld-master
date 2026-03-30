using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class Pool : MonoBehaviour, ZYKPoolInterface
{
    public abstract void OnDesteryObject();

    public abstract void OnInstantiateObject();
}
