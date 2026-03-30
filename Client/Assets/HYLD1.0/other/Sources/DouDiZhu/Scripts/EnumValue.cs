
public enum OldRequestCode
{
    None,
    User,
    Game,
    Room,
    Hell,
    HYLDGame,
};
public enum OldActionCode
{
    None,
    SendSMS,
    VfCode,
    Logins,
    GameHall,
    StartMacthingNormal,
    StopMacthingNormal=6,
    DealCard=7,//#刚刚开始发给玩家的手牌
    PlayerBehavior=8,//###1为叫 2不叫  客户端处理顺序后发送，服务器仅做广播
    PlayerPushCard=9,//####3456789TJQKA2SG  按大小格式排序 若为0表示不出牌， 客户端处理顺序后发送，服务器仅做广播
    LeftRoom=10,
    //#################################################HYLD
    StartBSZBMacthing=11,//宝石争霸
    PlayerLogicMove=12,
    
    //####################################################
}
