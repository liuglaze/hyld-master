using System;
using System.Collections.Generic;
using System.Text;
using SocketProto;
using System.Reflection;
namespace Server.Controller
{
    class ControllerManger
    {
        private Server _server;
        private Dictionary<RequestCode,BaseControllers> _controllerDic = new Dictionary<RequestCode, BaseControllers>();
        private Dictionary<string, BaseControllers> controllerNameDic = new Dictionary<string, BaseControllers>();
        public ControllerManger(Server server)
        {
            _server = server;
            UserController userController = new UserController();
            FriendController friendController = new FriendController();
            FriendRoomController friendRoomController = new FriendRoomController();
            PingPongController pingPongController = new PingPongController();
            MatchingController matchingController = new MatchingController();
            ClearSenceController clearSenceController = new ClearSenceController();
            _controllerDic.Add(friendRoomController.GetRequestCode,friendRoomController);
            _controllerDic.Add(userController.GetRequestCode, userController);
            _controllerDic.Add(friendController.GetRequestCode, friendController);
            _controllerDic.Add(pingPongController.GetRequestCode, pingPongController);
            _controllerDic.Add(matchingController.GetRequestCode, matchingController);
            _controllerDic.Add(clearSenceController.GetRequestCode, clearSenceController);
            controllerNameDic.Add(nameof(UserController), userController);
            controllerNameDic.Add(nameof(FriendController), friendController);
            controllerNameDic.Add(nameof(FriendRoomController), friendRoomController);
            controllerNameDic.Add(nameof(PingPongController), pingPongController);
            controllerNameDic.Add(nameof(MatchingController), matchingController);
            controllerNameDic.Add(nameof(ClearSenceController), clearSenceController);
        }
        public BaseControllers GetControllerByName(string name)
        {
            if (controllerNameDic.TryGetValue(name, out BaseControllers controller))
            {
                return controller;
            }
            return null;
        }
        public void CloseClient(Client client,int id)
        {
            foreach (BaseControllers controllers in _controllerDic.Values)
            {
                controllers.CloseClient(client, id);
            }
        }

        public void HandleRequest(MainPack pack,Client client)
        {
            if (_controllerDic.TryGetValue(pack.Requestcode, out BaseControllers controller))
            {
                //根据Requestcode找到对应的Controller
                string methodname = pack.Actioncode.ToString();
                //根据Actioncode找到controller里的对应同名方法
                MethodInfo method = controller.GetType().GetMethod(methodname);
                Logging.Debug.Log($"Handle  {pack}\n Controller  {controller} \n method : {methodname}");
                if (method == null)
                {
                    Logging.Debug.Log("没有找到指定事件处理" + pack.Actioncode.ToString());
                    return;
                }
                //调用对应的actioncode方法
                //我们controller所有默认都需要这三个参数，因为不知道会不会用到，所以都放在一起，反射调用时就不需要区分了
                object[] obj = new object[] { _server, client, pack };
                object o = method.Invoke(controller, obj);
                if (o == null) { return; }
                client.Send(o as MainPack);
            }
            else 
            {
                Logging.Debug.Log("未找到对应的事件控制 :"+pack);
            }

        }

    }
}
