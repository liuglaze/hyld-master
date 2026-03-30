/*
 ****************
 * Author:        赵元恺
 * CreatTime:       2020/7/25
 * Description: 妙具和星辉和属性菜单的面板点开关闭
 ****************
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
public class HYLDHeropropertyUI : MonoBehaviour
{
    // Start is called before the first frame update
    public void openMiaoJuCaiDan()
    {
        this.gameObject.transform.Find("MiaoJuCaiDan").gameObject.SetActive(true);
    }
    public void closeMiaoJuCaiDan()
    {
        this.gameObject.transform.Find("MiaoJuCaiDan").gameObject.SetActive(false);
    }

    public void openXingHuiCaiDan()
    {
        this.gameObject.transform.Find("XingHuiCaiDan").gameObject.SetActive(true);
    }
    public void closeXingHuiCaiDan()
    {
        this.gameObject.transform.Find("XingHuiCaiDan").gameObject.SetActive(false);
    }

    public void openYingXiongShuXingCaoDan()
    {
        this.gameObject.transform.Find("YingXiongShuXingCaoDan").gameObject.SetActive(true);
    }
    public void closeYingXiongShuXingCaoDan()
    {
        this.gameObject.transform.Find("YingXiongShuXingCaoDan").gameObject.SetActive(false);
    }
    public void backStart()
    {
        HYLDStaticValue.isloading = true;
        HYLDStaticValue.ModenName = "HYLDBaoShiZhengBa";
        SceneManager.LoadScene("HuangYeLuanDouStart");
        HYLDStaticValue.Players.Clear();
    }
}
