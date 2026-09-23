/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/29 11:44:50
    Description:     加载进度面板（旧清场景网络协议已退役，纯本地进度 UI）
*****************************************************/

namespace MVC
{
	/// <summary>
	/// 挂在 ClearScene 场景里的加载进度面板。
	/// 旧「清场景」协议（客户端清场完成 / 全员清场完成两个消息）随旧战斗链退役后，
	/// 本类不再注册任何 BaseRequest，也不再处理远端 Ready；进度推进与场景放行由
	/// Manger.ClearSenceManger 纯本地完成。
	/// 保留类名与脚本 GUID（12ce5b5195389c74caa57eaa1c9c8f7f）、继续挂在 HYLDAsyncScence 上，
	/// 避免产生 Missing Script。
	/// </summary>
	public class UISliderPanel : UIbasePanel
	{
	}
}
