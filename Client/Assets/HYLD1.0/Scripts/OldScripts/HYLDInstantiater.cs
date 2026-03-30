using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HYLDInstantiater : MonoBehaviour
{
    // Start is called before the first frame update

    public GameObject 生成物;
    public GameObject tcp = null;

    void Start()
    {
        Instantiate(生成物);
        if(tcp!=null&&!TCPSocket.是否创建)
        {
            Instantiate(tcp);
        }
      //  Logging.HYLDDebug.LogError(Time.time);
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
