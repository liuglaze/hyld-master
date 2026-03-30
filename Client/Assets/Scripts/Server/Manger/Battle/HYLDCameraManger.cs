/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/18 21:25:33 
 * Description: 
 * * * * * * * * * * * * * * * * 
*/
/*
****************
 * Author:        赵元恺
 * CreatTime:  2020/7/4 22:41 
 * Description: 越过服务器运行单人模式,引入Hero类，hero自带子弹类型，血量，名字等参数
 **************** 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using System;

public class HYLDCameraManger : MonoBehaviour
{
    private float tempx = 0;
    private float tempy = 0;
    [HideInInspector]
    public string moden = "HYLDBaoShiZhengBa";
    //public bool isTest = false;
    public bool initFinish { get; private set; }
    public void InitData()
    {
        initFinish = false;
        moden = HYLDStaticValue.ModenName;
        StartCoroutine(InitCamera());
    }
    IEnumerator InitCamera()
    {
        yield return new WaitUntil(() => {
            // Logging.HYLDDebug.LogError("WaitInitData()~~~等待中");
            return HYLDStaticValue.playerSelfIDInServer != -1;//roleManage.initFinish && obstacleManage.initFinish && bulletManage.initFinish;
        });
        tempx = Mathf.Min(6, transform.position.x - HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon.x);
        tempy = Mathf.Min(12, transform.position.y - HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon.y);
        initFinish = true;
    }
    Vector3 startPos;
    Vector3 endPos;

    /// <summary>相机平滑时间（秒）。越小越跟手，越大越平滑。</summary>
    private const float SmoothTime = 0.08f;
    private Vector3 _velocity = Vector3.zero;

    // ★ OnLogicUpdate 保留接口但不再承担 endPos 更新职责
    public void OnLogicUpdate()
    {
        // endPos 更新已移至 LateUpdate，保证每个渲染帧都读取角色最新渲染位置
    }

    private void LateUpdate()
    {
        if (!initFinish) return;
        if (HYLDStaticValue.isloading) return;

        GameObject selfBody = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body;
        if (selfBody == null) return;

        // ★ 在 LateUpdate 里直接读取角色当前渲染位置（Update 中 MoveTowards 已执行完毕）
        endPos = selfBody.transform.GetChild(0).position;
        endPos.x += tempx;
        endPos.y += tempy;
        endPos.z = transform.position.z;

        Vector3 pos = transform.position;
        pos.x = Mathf.SmoothDamp(pos.x, endPos.x, ref _velocity.x, SmoothTime);
        pos.y = Mathf.SmoothDamp(pos.y, endPos.y, ref _velocity.y, SmoothTime);
        transform.position = pos;
    }
}