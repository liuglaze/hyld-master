/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/22 18:36:42
    Description:     静态类
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace Server
{
	public class NetConfigValue :MonoBehaviour
	{
        public const string RegexValue = "^(17[0-9]|13[0-9]|14[5|7]|15[0|1|2|3|4|5|6|7|8|9]|18[0|1|2|3|5|6|7|8|9])\\d{8}$";
        public static string ServiceIP = "";
        public static readonly int ServiceTCPPort = 7778;
        public static readonly float frameTime = 0.016f;
        public static readonly float canPlayerRestoreHealthTime = 2;

        // 旧链退役（契约 §B）：原这里还有 ServiceUDPPort(7777) 与一整组只服务旧链预测/重发/弱网的字段
        // （PredictionHistoryWindowSize / ReconciliationPositionThreshold / EnablePredictionReconciliationPipeline /
        // pingIntervalMs / maxCatchupPerUpdate / maxCatchupPerUpdateWhenBehind / inputBufferSize /
        // targetFrameSafetyFrames / adjustRate / minSpeedFactor / maxSpeedFactor / smoothRate /
        // jitterBufferRatio / maxJitterBufferFrames / severeLeadPauseFrames / pauseAccumulatorRetainFactor /
        // moveMagnitudeThreshold / moveDotThreshold）。它们只被已删除的旧战斗链读取，已随之删除。
        // 保留的是登录短信正则（RegexValue）、大厅 TCP 地址（ServiceIP / ServiceTCPPort）
        // 与仍被保留源表现读取的基础帧时长（frameTime）。
    }
}
