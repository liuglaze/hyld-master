using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class HYLDGameOver : MonoBehaviour
{
    // Start is called before the first frame update
    public Sprite[] failHero;
    public Sprite[] winHero;
    public Sprite[] bg;
    public bool isWin;
    private  Image bgImage;
    private  Image HeroImage;
    private bool isMVP=true;
    void Start()
    {
       
    }

    // Update is called once per frame
    public void GameOver()
    {
        bgImage = gameObject.transform.Find("Bg").GetComponent<Image>();
        HeroImage = gameObject.transform.Find("Player").GetComponent<Image>();
        //把游戏UI关掉，把游戏结束UI开启
        gameObject.SetActive(true);
        //Logging.HYLDDebug.LogError(gameObject.transform.parent.gameObject);
        //Logging.HYLDDebug.LogError(gameObject.transform.parent.Find("GameUI").gameObject);
        gameObject.transform.parent.Find("GameUI").transform.Find("Android").gameObject.SetActive(false);

        //在HYLDStaticValue,增加 失败/胜利的判断
        if (HYLDStaticValue.玩家输了吗) isWin = false;
        else isWin = true;



        if(isWin)//如果胜利
        {
            bgImage.sprite = bg[0];
            
            string name= HYLDStaticValue._myheroName.ToString();
            

            foreach (var sp in winHero)
            {
                if (sp.name == name)
                {
                    HeroImage.sprite  = sp;
                    break;
                }
            }
           
        }
        else//失败
        {
            bgImage.sprite = bg[1];
            string name = HYLDStaticValue._myheroName.ToString();


            foreach (var sp in failHero)
            {
                if (sp.name == name)
                {
                    HeroImage.sprite = sp;
                    break;
                }
            }
        }
        //MVP判断
        if (isMVP) gameObject.transform.Find("MVP").gameObject.SetActive(true);
        else gameObject.transform.Find("MVP").gameObject.SetActive(false);
        
    }
    public void BackToMainScence()
    {
        LoadAsyncScene.isBackStart = true;
        //Destroy(TCPSocket.Instance.gameObject);
        //返回主菜单
        //SceneManager.LoadScene("");
        Manger.ClearSenceManger.LoadScene(SceneConfig.mainScene);
        //SceneManager.LoadScene("HYLDAsyncScence");
    }
}
