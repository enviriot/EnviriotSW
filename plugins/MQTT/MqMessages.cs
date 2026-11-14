///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace X13.MQTT {
  /// <summary>Типы пакетов MQTT; NONE используется как внутренний признак отсутствия типа.</summary>
  internal enum MessageType : byte {
    NONE        = 0,
    CONNECT 	= 1,
    CONNACK 	= 2,
    PUBLISH 	= 3,
    PUBACK 	    = 4,
    PUBREC 	    = 5,
    PUBREL 	    = 6,
    PUBCOMP 	= 7,
    SUBSCRIBE 	= 8,
    SUBACK 	    = 9,
    UNSUBSCRIBE = 10,
    UNSUBACK 	= 11,
    PINGREQ 	= 12,
    PINGRESP 	= 13,
    DISCONNECT 	= 14,
  }

  internal abstract class MqMessage {
    protected static UTF8Encoding enc = new UTF8Encoding();
    static MqMessage() {
    }
    public static MqMessage Parse(byte header, uint len, MemoryStream stream) {
      MqMessage msg=null;
      switch((MessageType)((header & 0xf0) >> 4)) {
      case MessageType.CONNECT:
        msg=new MqConnect(header, len, stream);
        break;
      case MessageType.CONNACK:
        msg=new MqConnack(header, len, stream);
        break;
      case MessageType.DISCONNECT:
        msg=new MqDisconnect(header, len, stream);
        break;
      case MessageType.PINGREQ:
        msg=new MqPingReq(header, len, stream);
        break;
      case MessageType.PINGRESP:
        msg=new MqPingResp(header, len, stream);
        break;
      case MessageType.PUBLISH:
        msg=new MqPublish(header, len, stream);
        break;
      case MessageType.SUBSCRIBE:
        msg=new MqSubscribe(header, len, stream);
        break;
      case MessageType.SUBACK:
        msg=new MqSuback(header, len, stream);
        break;
      case MessageType.UNSUBSCRIBE:
        msg=new MqUnsubscribe(header, len, stream);
        break;
      case MessageType.UNSUBACK:
        msg=new MqUnsuback(header, len, stream);
        break;
        case MessageType.PUBACK:
        case MessageType.PUBCOMP:
        case MessageType.PUBREC:
        case MessageType.PUBREL:
          msg=new MqMsgAck(header, len, stream);
          break;
      }
      return msg;
    }

    protected uint variableHeaderLength = 0;

    public readonly MessageType MsgType;
    public bool Duplicate;
    public QoS QualityOfService;
    public bool Retained;
    public ushort MessageID;
    public MessageType Reason { get; protected set; }

    protected MqMessage(MessageType msgType) {
      this.MsgType = msgType;
      this.Duplicate=false;
      this.QualityOfService=QoS.AtMostOnce;
      this.Retained=false;
      this.MessageID=0;
    }
    /// <summary>Разбирает флаги фиксированного заголовка и сохраняет длину оставшейся части пакета.</summary>
    /// <param name="header">Первый байт фиксированного заголовка.</param>
    /// <param name="len">Remaining Length: длина переменного заголовка и полезной нагрузки вместе.</param>
    /// <param name="str">Поток тела пакета; базовый конструктор его не читает.</param>
    protected MqMessage(byte header, uint len, Stream str) { //-V3117
      this.MsgType = (MessageType)((header & 0xf0) >> 4);
      this.Duplicate = (header & 0x08) != 0;
      this.QualityOfService = (QoS)((header & 0x06)>>1);
      this.Retained = (header & 0x01) != 0;
      variableHeaderLength = len;
    }
    /// <summary>Записывает фиксированный заголовок; тело пакета добавляет конкретный тип сообщения.</summary>
    /// <param name="str">Выходной поток.</param>
    public virtual void Serialise(Stream str) {
      // Первый байт содержит тип пакета и флаги.
      byte header = (byte)(((byte)this.MsgType << 4) | (this.Duplicate?8:0) | ((byte)this.QualityOfService << 1) | (this.Retained?1:0));
      str.WriteByte(header);
      // Remaining Length кодируется переменным числом байтов.
      WriteToStreamPacked(str, variableHeaderLength);
    }
    public override string ToString() {
      return MsgType.ToString();
    }
    protected static void WriteToStream(Stream str, ushort val) {
      str.WriteByte((byte)(val >> 8));
      str.WriteByte((byte)(val & 0xFF));
    }
    private static void WriteToStreamPacked(Stream str, uint length) {
      byte digit = 0;
      do {
        digit = (byte)(length % 128);
        length /= 128;
        if(length > 0) {
          digit |= 0x80;
        }
        str.WriteByte(digit);
      }
      while(length > 0);

    }
    protected static void WriteToStream(Stream str, string val) {
      UTF8Encoding enc = new UTF8Encoding();
      byte[] bs = enc.GetBytes(val);
      WriteToStream(str, (ushort)bs.Length);
      str.Write(bs, 0, bs.Length);
    }
    protected static ushort ReadUshortFromStream(Stream str) {
      // Сетевой порядок: старший байт перед младшим.
      byte[] data = new byte[2];
      ReadCompleteBuffer(str, data);
      return (ushort)((data[0] << 8) + data[1]);
    }
    protected static string ReadStringFromStream(Stream str) {
      ushort len = ReadUshortFromStream(str);
      byte[] data = new byte[len];
      ReadCompleteBuffer(str, data);
      return enc.GetString(data, 0, data.Length);
    }
    protected static byte[] ReadCompleteBuffer(Stream str, byte[] buffer) {
      int read = 0;
      while(read < buffer.Length) {
        int res = str.Read(buffer, read, buffer.Length - read);
        if(res <1) {
          throw new Exception("End of stream reached whilst filling buffer");
        }
        read += res;
      }
      return buffer;
    }
  }

  internal class MqConnect : MqMessage {
    /// <summary>Обозначение уровня протокола 3, соответствующего MQTT 3.1.</summary>
    private const byte VERSION = 3;
    /// <summary>Имя и уровень протокола в заголовке CONNECT.</summary>
    private static readonly byte[] protocolDesc3 = new byte[] { 0, 6, (byte)'M', (byte)'Q', (byte)'I', (byte)'s', (byte)'d', (byte)'p', 3};
    private static readonly byte[] protocolDesc4 = new byte[] { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4 };

    public ushort keepAlive { get; set; }
    public string clientId { get; internal set; }
    public string willTopic { get; set; }
    public byte[] willPayload { get; set; }
    public bool willRetained { get; set; }
    public QoS willQos { get; set; }
    public bool cleanSession { get; set; }
    public string userName { get; set; }
    public string userPassword { get; set; }
    public byte version { get; set; }

    public MqConnect()
      : base(MessageType.CONNECT) {
    }
    public MqConnect(byte header, uint len, Stream str)
      : base(header, len, str) {
      byte b;
      version = 0;
      int i = 0;
      while(true) {
        b = (byte)str.ReadByte();
        if((version==0 || version==3) && b==protocolDesc3[i]){
          if(i > 0) {
            version = 3;
          }
          i++;
          if(i>=protocolDesc3.Length){
            break;
          }
        } else if((version==0 || version==4) && b==protocolDesc4[i]){
          version=4;
          i++;
          if(i>=protocolDesc4.Length){
            break;
          }
        } else {
          throw new ArgumentException("Connect.ProtocolDescription Error ["+i.ToString()+"]=0x"+b.ToString("X2"));
        }
      }
      byte connectFlags=(byte)str.ReadByte();
      this.cleanSession=(connectFlags & 0x02)!=0;
      this.willRetained=(connectFlags & 0x20)!=0;
      this.willQos=(QoS)((connectFlags>>3) & 0x03);
      this.keepAlive=ReadUshortFromStream(str);
      this.clientId=ReadStringFromStream(str);
      if((connectFlags & 0x04)!=0) {
        willTopic=ReadStringFromStream(str);
        int wLen=ReadUshortFromStream(str);
        willPayload=new byte[wLen];
        ReadCompleteBuffer(str, willPayload);
      }
      if((connectFlags & 0x80)!=0) {
        userName=ReadStringFromStream(str);
      }
      if((connectFlags & 0x40)!=0) {
        userPassword=ReadStringFromStream(str);
      }
      if(userPassword==null) {
        userPassword=string.Empty;
      }
    }
    public override void Serialise(Stream str) {
      byte _connectFlags=(byte)((cleanSession ? 0x02 : 0)); // Флаг чистой сессии.

      base.variableHeaderLength = (uint)(
        version==4? protocolDesc4.Length:protocolDesc3.Length         // Длина описания протокола.
        +3                          // Флаги подключения и интервал keep-alive.
        +enc.GetByteCount(clientId) // Размер идентификатора клиента в UTF-8.
        +2                          // Поле длины идентификатора клиента.
      );

      if(!string.IsNullOrEmpty(willTopic)) {
        _connectFlags=(byte)(4 | (willRetained ? 0x20 : 0) | (((byte)willQos) << 3));
        base.variableHeaderLength += (uint)(enc.GetByteCount(willTopic) +  (willPayload==null?0:willPayload.Length)+ 4);
      }
      if(!string.IsNullOrEmpty(userName)) {
        _connectFlags|=0x80;
        base.variableHeaderLength += (uint)(enc.GetByteCount(userName)+2);
      }
      if(!string.IsNullOrEmpty(userPassword)) {
        _connectFlags|=0x40;
        base.variableHeaderLength += (uint)(enc.GetByteCount(userPassword)+2);
      }

      base.Serialise(str);
      str.Write(version == 4 ? protocolDesc4 : protocolDesc3, 0, (version == 4 ? protocolDesc4 : protocolDesc3).Length);
      str.WriteByte(_connectFlags);
      // Интервал keep-alive.
      WriteToStream(str, this.keepAlive);
      // Полезная нагрузка начинается с идентификатора клиента.
      WriteToStream(str, clientId);

      if(!string.IsNullOrEmpty(willTopic)) {
        WriteToStream(str, willTopic);
        if(willPayload!=null) {
          WriteToStream(str, (ushort)willPayload.Length);
          str.Write(willPayload, 0, willPayload.Length);
        } else {
          WriteToStream(str, (ushort)0);
        }
      }
      if(!string.IsNullOrEmpty(userName)) {
        WriteToStream(str, userName);
      }
      if(!string.IsNullOrEmpty(userPassword)) {
        WriteToStream(str, userPassword);
      }
    }
    public override string ToString() {
      return string.Format("{0} - {1}", MessageType.CONNECT, clientId);
    }
  }

  internal class MqConnack : MqMessage {
    /// <summary>Код результата подключения в CONNACK.</summary>
    public enum MqttConnectionResponse : byte {
      Accepted                    = 0,
      UnacceptableProtocolVersion = 1,
      IdentifierRejected          = 2,
      ServerUnavailable           = 3,
      BadUsernameOrPassword       = 4,
      NotAuthorized               = 5
    }
    public MqttConnectionResponse Response { get; private set; }
    public MqConnack(MqttConnectionResponse response)
      : base(MessageType.CONNACK) {
      Response = response;
    }
    public MqConnack(byte header, uint len, Stream str)
      : base(header, len, str) {
      byte[] buffer = new byte[2];
      ReadCompleteBuffer(str, buffer);
      Response = (MqttConnectionResponse)buffer[1];
    }

    public override void Serialise(Stream str) {
      base.variableHeaderLength=2;
      base.Serialise(str);
      str.WriteByte(0);
      str.WriteByte((byte)Response);
    }
    public override string ToString() {
      return string.Format("{0}={1}", MessageType.CONNACK, this.Response);
    }
  }

  internal class MqSubscribe : MqMessage {
    private List<KeyValuePair<string, QoS>> _list=new List<KeyValuePair<string,QoS>>();
    /// <summary>Создаёт пакет подписки на несколько топиков.</summary>
    public MqSubscribe()
      : base(MessageType.SUBSCRIBE) {
      base.QualityOfService = QoS.AtLeastOnce;
    }
    public MqSubscribe(byte header, uint len, Stream str)
      : base(header, len, str) {
      uint payloadLen = base.variableHeaderLength;
      base.MessageID=ReadUshortFromStream(str);
      payloadLen-=2;
      string topic;
      QoS sQoS;
      while(payloadLen>0) {
        topic=ReadStringFromStream(str);
        sQoS=(QoS)str.ReadByte();
        _list.Add(new KeyValuePair<string,QoS>(topic, sQoS));
        payloadLen-=(uint)(3+enc.GetByteCount(topic));
      }
    }
    public void Add(string topic, QoS sQoS) {
        _list.Add(new KeyValuePair<string,QoS>(topic, sQoS));
    }
    public List<KeyValuePair<string, QoS>> list { get { return _list; } }
    public override void Serialise(Stream str) {
      // Длина переменного заголовка и полезной нагрузки.
      base.variableHeaderLength=2;
      foreach(var s in _list) {
        base.variableHeaderLength+=(uint)(3+enc.GetByteCount(s.Key));
      }
      // Заголовок и идентификатор пакета.
      base.Serialise(str);
      WriteToStream(str, base.MessageID);
      // Фильтры подписки и запрошенные уровни QoS.
      foreach(var s in _list) {
        WriteToStream(str, s.Key);
        str.WriteByte((byte)s.Value);
      }
    }
    public override string ToString() {
      StringBuilder sb=new StringBuilder();
      sb.AppendFormat("{0} [", MessageType.SUBSCRIBE);
      foreach(var s in _list) {
        sb.AppendFormat("({0} - {1}), ", s.Key, s.Value);
      }
      sb.Append("]");
      return sb.ToString();
    }
  }

  internal class MqSuback : MqMessage {

    private List<QoS> grantedQos=new List<QoS>();
    public MqSuback()
      : base(MessageType.SUBACK) {
    }
    public MqSuback(byte header, uint len, Stream str)
      : base(header, len, str) {
      uint payloadLen = base.variableHeaderLength;
      base.Reason=MessageType.SUBSCRIBE;
      base.MessageID=ReadUshortFromStream(str);
      payloadLen-=2;
      while(payloadLen>0) {
        grantedQos.Add((QoS)(str.ReadByte()));
        payloadLen--;
      }
    }
    public void Add(QoS gQoS) {
      grantedQos.Add(gQoS);
    }
    public override void Serialise(Stream str) {
      // Длина идентификатора пакета и кодов результата.
      base.variableHeaderLength=(uint)(2+grantedQos.Count);

      // Заголовок и идентификатор пакета.
      base.Serialise(str);
      WriteToStream(str, base.MessageID);
      // Коды результата подписки.
      for(int i=0; i<grantedQos.Count; i++) {
        str.WriteByte((byte)grantedQos[i]);
      }
    }
    public override string ToString() {
      StringBuilder sb=new StringBuilder();
      sb.AppendFormat("{0} [", MessageType.SUBACK);
      foreach(QoS q in grantedQos) {
        sb.AppendFormat("{0}, ", q);
      }
      sb.Append("]");
      return sb.ToString();
    }
  }

  internal class MqPublish : MqMessage {
    private string _path;
    private string _payload;

    //public MqPublish(Topic topic)
    //  : base(MessageType.PUBLISH) {
    //  DataSource=topic;
    //  this.Retained=DataSource.saved;
    //}
    public MqPublish(string path, string payload):base(MessageType.PUBLISH) {
      _path = path;
      _payload = payload;
    }
    public MqPublish(byte header, uint len, Stream str)
      : base(header, len, str) {
      uint payloadLen = base.variableHeaderLength;

      Path = ReadStringFromStream(str);
      payloadLen -= (uint)(enc.GetByteCount(Path) + 2);

      if(base.QualityOfService != QoS.AtMostOnce) {
        base.MessageID = ReadUshortFromStream(str);
        payloadLen -= 2;
      }

      byte[] tmp= new byte[payloadLen];
      ReadCompleteBuffer(str, tmp);
      Payload=Encoding.UTF8.GetString(tmp);
    }
    //public Topic DataSource { get; private set; }
    public string Path { get { return (_path); } set { _path=value; } }
    public string Payload { 
      get { 
        return _payload; 
      } 
      set { 
        _payload=value; 
      } 
    }

    public override void Serialise(Stream str) {
      byte[] pathBuf = enc.GetBytes(Path);
      string pys=this.Payload;
      if(pys==null) {
        pys=string.Empty;
      }
      byte[] payloadBuf=Encoding.UTF8.GetBytes(pys);
      base.variableHeaderLength =(uint)(
        2 + pathBuf.Length    // Топик и поле его длины.
          +(base.QualityOfService  == QoS.AtMostOnce ? 0 : 2)  // Идентификатор пакета при QoS > 0.
          +((payloadBuf==null)?0:payloadBuf.Length));                     // Полезная нагрузка.
      base.Serialise(str);

      WriteToStream(str, (ushort)pathBuf.Length);
      str.Write(pathBuf, 0, pathBuf.Length);

      if(base.QualityOfService != QoS.AtMostOnce) {
        WriteToStream(str, base.MessageID);
      }
      if(payloadBuf!=null && payloadBuf.Length>0) {
        str.Write(payloadBuf, 0, payloadBuf.Length);
      }
      //if(!Path.StartsWith("/etc")) {
      //  Log.Debug(">{0}{1}={2}", this.Retained?"*":".", Path, pys??"null");
      //}
    }
    public override string ToString() {
      return string.Format("{0} {1} {2}[{3}] {4},{5:X4}", MessageType.PUBLISH, this.Retained?"+":"-", Path, Payload, this.QualityOfService, this.MessageID);
    }
  }

  internal class MqMsgAck : MqMessage {
    public MqMsgAck(MessageType type, ushort msgId)
      : base(type) {
      base.MessageID=msgId;
      base.variableHeaderLength=2;
    }
    public MqMsgAck(byte header, uint len, Stream str)
      : base(header, len, str) {
      base.MessageID=ReadUshortFromStream(str);
      switch(base.MsgType) {
      case MessageType.PUBACK:
        base.Reason=MessageType.PUBLISH;
        break;
      case MessageType.PUBREC:
        base.Reason=MessageType.PUBLISH;
        break;
      case MessageType.PUBREL:
        base.Reason=MessageType.PUBREC;
        break;
      case MessageType.PUBCOMP:
        base.Reason=MessageType.PUBREL;
        break;
      default:
        throw new ArgumentOutOfRangeException();
      }
    }
    public override void Serialise(Stream str) {
      base.Serialise(str);
      WriteToStream(str, base.MessageID);
    }
    public override string ToString() {
      return string.Format("{0} {1:X4}", base.MsgType, base.MessageID);
    }
  }

  internal class MqUnsubscribe : MqMessage {
    private List<string> _subscriptions=new List<string>();
    public MqUnsubscribe()
      : base(MessageType.UNSUBSCRIBE) {
      base.QualityOfService = QoS.AtLeastOnce;
    }

    public MqUnsubscribe(byte header, uint len, Stream str)
      : base(header, len, str) {
      uint payloadLen = base.variableHeaderLength;
      base.MessageID=ReadUshortFromStream(str);
      payloadLen-=2;
      string topic;
      while(payloadLen>0) {
        topic=ReadStringFromStream(str);
        _subscriptions.Add(topic);
        payloadLen-=(uint)(2+enc.GetByteCount(topic));
      }
    }
    public void Add(string path) {
      _subscriptions.Add(path);
    }
    public List<string> list { get { return _subscriptions; } }
    public override void Serialise(Stream str) {
      // Длина идентификатора пакета и списка фильтров.
      base.variableHeaderLength=2;
      foreach(string s in _subscriptions) {
        base.variableHeaderLength+=(uint)(2+enc.GetByteCount(s));
      }
      // Заголовок и идентификатор пакета.
      base.Serialise(str);
      WriteToStream(str, base.MessageID);
      // Фильтры отменяемых подписок.
      foreach(string s in _subscriptions) {
        WriteToStream(str, s);
      }
    }
    public override string ToString() {
      StringBuilder sb=new StringBuilder();
      sb.AppendFormat("{0} [", MessageType.UNSUBSCRIBE);
      foreach(var s in _subscriptions) {
        sb.AppendFormat("{0}, ", s);
      }
      sb.Append("]");
      return sb.ToString();
    }
  }

  internal class MqUnsuback : MqMessage {
    public MqUnsuback()
      : base(MessageType.UNSUBACK) {
    }
    public MqUnsuback(byte header, uint len, Stream str)
      : base(header, len, str) {
      base.MessageID=ReadUshortFromStream(str);
      base.Reason=MessageType.UNSUBSCRIBE;
    }
    public override void Serialise(Stream str) {
      // Длина идентификатора пакета.
      base.variableHeaderLength=2;
      // Заголовок и идентификатор пакета.
      base.Serialise(str);
      WriteToStream(str, base.MessageID);
    }
  }

  internal class MqPingReq : MqMessage {
    public MqPingReq()
      : base(MessageType.PINGREQ) {
    }
    public MqPingReq(byte header, uint len, Stream str)
      : base(header, len, str) {
    }
    public override void Serialise(Stream str) {
      base.variableHeaderLength=0;
      base.Serialise(str);
    }
  }

  internal class MqPingResp : MqMessage {
    public MqPingResp()
      : base(MessageType.PINGRESP) {
    }
    public MqPingResp(byte header, uint len, Stream str)
      : base(header, len, str) {
    }
    public override void Serialise(Stream str) {
      base.variableHeaderLength=0;
      base.Serialise(str);
    }
  }

  internal class MqDisconnect : MqMessage {
    public MqDisconnect()
      : base(MessageType.DISCONNECT) {
    }
    public MqDisconnect(byte header, uint len, Stream str)
      : base(header, len, str) {
    }
    public override void Serialise(Stream str) {
      base.variableHeaderLength=0;
      base.Serialise(str);
    }
  }

}
