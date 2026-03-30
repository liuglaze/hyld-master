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
        public static readonly int ServiceUDPPort = 7777;
        public static readonly float frameTime = 0.016f;
        public static readonly float canPlayerRestoreHealthTime = 2;
        public static int PredictionHistoryWindowSize = 20;
        public static float ReconciliationPositionThreshold = 0.6f;
        public static bool EnablePredictionReconciliationPipeline = true;
        // ── 动态追帧参数 ──
        public static readonly float pingIntervalMs = 200f;
        public static readonly int maxCatchupPerUpdate = 3;
        public static readonly int inputBufferSize = 4;
        public static readonly float adjustRate = 0.04f;
        public static readonly float minSpeedFactor = 0.88f;
        public static readonly float maxSpeedFactor = 1.10f;
        public static readonly float smoothRate = 4.0f;
        public static readonly float jitterBufferRatio = 0.25f;
        public static readonly int maxJitterBufferFrames = 6;
        public static readonly int severeLeadPauseFrames = 8;
        public static readonly float pauseAccumulatorRetainFactor = 0.35f;
    }
}