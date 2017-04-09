///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.MQTT {
  internal class MqClient : IDisposable {
    private MQTTPl _pl;
    private string _host, _uName, _uPass;
    private int _port;
    private MqStreamer _stream;
    private int _keepAliveMS;
    private bool _waitPingResp;
    private Timer _tOut;

    public readonly List<MqSite> Sites;
    public readonly string Signature;
    public Status status { get; private set; }

    public MqClient(MQTTPl pl, string host, int port, string uName, string uPass) {
      _keepAliveMS = 9950;    // 10 sec
      _tOut = new Timer(new TimerCallback(TimeOut));
      _pl = pl;
      _host = host;
      _port = port;
      _uName = uName;
      _uPass = uPass;
      Signature = "MQTT://" + (_uName == null ? string.Empty : (_uName + "@")) + _host + (_port == 1883 ? string.Empty : (":" + _port.ToString()));
      Sites = new List<MqSite>();
      status = Status.Disconnected;
      Connect();
    }
    public void Subscribe(MqSite ms) {
      if(status == Status.Connected) {
        var msg = new MqSubscribe();
        msg.Add(ms.remotePath, QoS.AtMostOnce);
        Send(msg);
      }
      Sites.Add(ms);
    }
    public void Unsubscribe(MqSite mqSite) {
      if(Sites.Remove(mqSite) && status == Status.Connected) {
        var msg = new MqUnsubscribe();
        msg.Add(mqSite.remotePath);
        Send(msg);
      }
    }
    public void Dispose() {
      _tOut.Change(Timeout.Infinite, Timeout.Infinite);
      status = Status.Disconnected;
      var s = Interlocked.Exchange(ref _stream, null);
      if(s != null && s.isOpen) {
        s.Send(new MqDisconnect());
        Thread.Sleep(0);
        s.Close();
      }
    }

    private void Connect() {
      status = Status.Connecting;
      TcpClient _tcp = new TcpClient();
      _tcp.SendTimeout = 900;
      _tcp.ReceiveTimeout = 10;
      _tcp.BeginConnect(_host, _port, new AsyncCallback(ConnectCB), _tcp);
    }
    private void ConnectCB(IAsyncResult rez) {
      var _tcp = rez.AsyncState as TcpClient;
      try {
        _tcp.EndConnect(rez);
        _stream = new MqStreamer(_tcp, Received, SendIdle);
        var id = string.Format("{0}_{1:X4}", Environment.MachineName, System.Diagnostics.Process.GetCurrentProcess().Id);
        var ConnInfo = new MqConnect();
        ConnInfo.keepAlive = (ushort)(_keepAliveMS + 50 / 1000);
        ConnInfo.cleanSession = true;
        ConnInfo.clientId = id;
        if(_uName != null) {
          ConnInfo.userName = _uName;
          if(_uPass != null) {
            ConnInfo.userPassword = _uPass;
          }
        }
        this.Send(ConnInfo);
        _tOut.Change(3000, _keepAliveMS);       // better often than never
      }
      catch(Exception ex) {
        var se = ex as SocketException;
        if(se != null && (se.SocketErrorCode == SocketError.ConnectionRefused || se.SocketErrorCode == SocketError.TryAgain || se.SocketErrorCode == SocketError.TimedOut)) {
          status = Status.Disconnected;
          if(_keepAliveMS < 900000) {
            _keepAliveMS = (new Random()).Next(_keepAliveMS * 3, _keepAliveMS * 6);
          }
          _tOut.Change(_keepAliveMS, Timeout.Infinite);
        } else {
          status = Status.NotAccepted;
          _tOut.Change(Timeout.Infinite, Timeout.Infinite);
        }
        Log.Error("{0} Connection FAILED - {1}", this.Signature, ex.Message);

      }
    }
    private void Received(MqMessage msg) {
      if(_pl.verbose) {
        Log.Debug("R {0} > {1}", this.Signature, msg);
      }
      switch(msg.MsgType) {
      case MessageType.CONNACK: {
          MqConnack cm = msg as MqConnack;
          if(cm.Response == MqConnack.MqttConnectionResponse.Accepted) {
            status = Status.Connected;
            _keepAliveMS = 9950;
            _tOut.Change(_keepAliveMS*2, _keepAliveMS);
            Log.Info("Connected to {0}", Signature);
            if(Sites.Any()) {
              var sMsg = new MqSubscribe();
              foreach(var site in Sites) {
                sMsg.Add(site.remotePath, QoS.AtMostOnce);
              }
              Send(sMsg);
            }
          } else {
            status = Status.NotAccepted;
            _tOut.Change(Timeout.Infinite, Timeout.Infinite);
          }
        }
        break;
      case MessageType.DISCONNECT:
        status = Status.Disconnected;
        _tOut.Change(3000, _keepAliveMS);
        break;
      case MessageType.PINGRESP:
        _waitPingResp = false;
        break;
      case MessageType.PUBLISH:{
          MqPublish pm=msg as MqPublish;
          if(msg.MessageID!=0) {
            if(msg.QualityOfService==QoS.AtLeastOnce) {
              this.Send(new MqMsgAck(MessageType.PUBACK, msg.MessageID));
            } else if(msg.QualityOfService==QoS.ExactlyOnce) {
              this.Send(new MqMsgAck(MessageType.PUBREC, msg.MessageID));
            }
          }
          ProccessPublishMsg(pm);
        }
        break;
      case MessageType.PUBACK:
        break;
      case MessageType.PUBREC:
        if(msg.MessageID!=0) {
          this.Send(new MqMsgAck(MessageType.PUBREL, msg.MessageID));
        }
        break;
      case MessageType.PUBREL:
        if(msg.MessageID!=0) {
          this.Send(new MqMsgAck(MessageType.PUBCOMP, msg.MessageID));
        }
        break;
      }
      if(_waitPingResp) {
        _tOut.Change(_keepAliveMS, _keepAliveMS);
      }
    }

    private void ProccessPublishMsg(MqPublish pm) {
      string path=pm.Path;
      if(string.IsNullOrEmpty(path)){
        path="/";
      } else if(path[0]!='/'){
        path="/"+path;
      }
      foreach(var s in Sites.Where(z => path.StartsWith(z.remotePrefix))) {
        s.Publish(path, pm.Payload);
      }
    }
    private void SendIdle() {
    }
    public void Send(MqMessage msg) {
      _stream.Send(msg);
      if(_pl.verbose) {
        Log.Debug("S {0} < {1}", this.Signature, msg);
      }
    }
    private void TimeOut(object o) {
      if(status == Status.NotAccepted) {
        _tOut.Change(Timeout.Infinite, Timeout.Infinite);
      } else if(_stream == null) {
        Connect();
      } else if(status == Status.Connected && !_waitPingResp) {
        _waitPingResp = true;
        Send(new MqPingReq());
      } else {
        if(status == Status.Connected) {
          Log.Warning("{0} - PingResponse timeout", Signature);
        } else if(status == Status.Connecting) {
          Log.Warning("{0} - ConnAck timeout", Signature);
        }
        Dispose();
        _tOut.Change(1500, _keepAliveMS);
      }
    }

    /*
    private void OwnerChanged(Topic sender, TopicChanged param) {
      if(!_connected || sender.parent==null || sender.path.StartsWith("/local") || sender.path.StartsWith("/var/now") || sender.path.StartsWith("/var/log") || param.Visited(_mq, false) || param.Visited(_owner, false)) {
        return;
      }
      switch(param.Art) {
      case TopicChanged.ChangeArt.Add: {
          MqPublish pm=new MqPublish(sender);
          if(sender.valueType!=null && sender.valueType!=typeof(string) && !sender.valueType.IsEnum && !sender.valueType.IsPrimitive) {
            pm.Payload=(new Newtonsoft.Json.Linq.JObject(new Newtonsoft.Json.Linq.JProperty("+", WOUM.ExConverter.Type2Name(sender.valueType)))).ToString();
          }
          this.Send(pm);
        }
        break;
      case TopicChanged.ChangeArt.Value: {
          MqPublish pm=new MqPublish(sender);
          this.Send(pm);
        }
        break;
      case TopicChanged.ChangeArt.Remove: {
          MqPublish pm=new MqPublish(sender);
          pm.Payload=string.Empty;
          this.Send(pm);
        }
        break;
      }
    }*/

    public enum Status {
      Disconnected,
      Connecting,
      Connected,
      NotAccepted
    }
  }
}
