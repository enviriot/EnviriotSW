﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JST = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NiL.JS.Extensions;

namespace X13.EsBroker {
  internal class EsSocketTCP : IEsSocket {
    #region static
    private static TcpListener _tcp;
    private static Action<Func<Action<EsMessage>, IEsSocket>> _onConnect;
    private static Func<bool> _verbose;

    public const int portDefault = 10013;
    public static void Start(int port, Func<bool> verbose, Action<Func<Action<EsMessage>, IEsSocket>> onConnect) {
      _verbose = verbose;
      _tcp = new TcpListener(IPAddress.Any, port);
      _onConnect = onConnect;
      _tcp.Start();
      _tcp.BeginAcceptTcpClient(new AsyncCallback(ConnectTCP), null);
    }
    public static EsSocketTCP ConnectCl(TcpClient tcp, Func<bool> verbose, Action<EsMessage> cb) {
      _verbose = verbose;
      return new EsSocketTCP(tcp, cb);
    }
    private static void ConnectTCP(IAsyncResult ar) {
      TcpClient client = null;
      try {
        client = _tcp.EndAcceptTcpClient(ar);
      }
      catch (ObjectDisposedException) {
        return;   // Socket allready closed
      }
      catch (NullReferenceException) {
        return;   // Socket allready destroyed
      }
      catch (SocketException) {
      }
      _tcp.BeginAcceptTcpClient(new AsyncCallback(ConnectTCP), null);
      if (client != null) {
        _onConnect((cb) => new EsSocketTCP(client, cb));
      }
    }
    public static void Stop() {
      _tcp?.Stop();
    }
    private static bool Verbose {
      get {
        return _verbose();
      }
    }

    #endregion static

    private readonly TcpClient _socket;
    private readonly Action<EsMessage> _callback;
    private readonly NetworkStream _stream;
    private readonly byte[] _rcvBuf;
    private byte[] _rcvMsgBuf;
    private int _connected;
    private readonly AsyncCallback _rcvCB;
    private int _rcvState;
    private int _rcvLength;

    private EsSocketTCP(TcpClient tcp, Action<EsMessage> cb) {
      this._socket = tcp;
      this._callback = cb;
      this._stream = _socket.GetStream();
      this._rcvBuf = new byte[1];
      this._rcvMsgBuf = new byte[2048];
      this._connected = 1;
      this._rcvState = -2;
      this._stream.Flush();
      this._rcvCB = new AsyncCallback(RcvProcess);
      this._stream.BeginRead(_rcvBuf, 0, 1, _rcvCB, _stream);

    }
    private void RcvProcess(IAsyncResult ar) {
      bool first = true;
      int len;
      byte b;
      try {
        len = _stream.EndRead(ar);
      }
      catch (IOException) {
        this.Dispose(true);
        return;
      }
      catch (ObjectDisposedException) {
        return;
      }
      if (len > 0) {
        try {
          do {
            if (first) {
              first = false;
              b = _rcvBuf[0];
            } else {
              b = (byte)_stream.ReadByte();
            }
            if (_rcvState < 0) {
              if (_rcvState < -1) {
                if (b == 0) {
                  _rcvState = -1;
                  _rcvLength = 0;
                }
              } else {
                _rcvLength = (_rcvLength << 7) | (b & 0x7F);
                if (b < 0x80) {
                  if (_rcvLength < 3 || _rcvLength > int.MaxValue / 2048) {  // 1 MB
                    _rcvState = -2;                                         // Bad Msg.Len
                  } else {
                    _rcvState = 0;
                    if (_rcvLength >= _rcvMsgBuf.Length) {
                      int l = _rcvMsgBuf.Length;
                      while (l < _rcvLength) {
                        l *= 2;
                      }
                      _rcvMsgBuf = new byte[l];
                    }
                  }
                }
              }
            } else if (_rcvState < _rcvLength) {
              _rcvMsgBuf[_rcvState] = b;
              _rcvState++;

            } else {
              if (b == 0xFF) {   // Paranoic mode On
                string ms = null;
                try {
                  ms = Encoding.UTF8.GetString(_rcvMsgBuf, 0, _rcvState);
                  if (Verbose) {
                    Log.Debug("{0}.Rcv({1})", this.ToString(), ms);
                  }
                  if (JsLib.ParseJson(ms) is JST.Array mj && mj.Count() > 0) {
                    _callback(new EsMessage(this, mj));
                  }
                }
                catch (Exception ex) {
                  Log.Warning("{0}.Rcv({1}) - {2}", this.ToString(), ms ?? BitConverter.ToString(_rcvMsgBuf, 0, _rcvState), ex.Message);
                }
              } else {
                if (Verbose) {
                  Log.Warning("{0}.Rcv - Paranoic", this.ToString());
                }
              }
              _rcvState = -2;
            }
          } while (_stream.DataAvailable);
        }
        catch (ObjectDisposedException) {
          return;
        }
        catch (Exception ex) {
          _rcvState = -2;
          Log.Warning(ex.ToString());
        }
      } else {
        this.Dispose(true);
        return;
      }

      try {
        _stream.BeginRead(_rcvBuf, 0, 1, _rcvCB, _stream);
      }
      catch (IOException) {
        this.Dispose(true);
        return;
      }
      catch (ObjectDisposedException ex) {
        Log.Warning("EsConnection.RcvProcess {0}", ex.Message);
        return;
      }
    }

    #region IEsSocket Members
    public void SendArr(JST.Array arr, bool rep = true) {
      var ms = JsLib.Stringify(arr);
      int len = Encoding.UTF8.GetByteCount(ms);
      int st = 1;
      int tmp = len;
      while (tmp > 0x7F) {
        tmp >>= 7;
        st++;
      }
      var buf = new byte[len + st + 2];
      Encoding.UTF8.GetBytes(ms, 0, ms.Length, buf, st + 1);
      tmp = len;
      buf[0] = 0;
      for (int i = st; i > 0; i--) {
        buf[i] = (byte)((tmp & 0x7F) | (i < st ? 0x80 : 0));
        tmp >>= 7;
      }
      buf[buf.Length - 1] = 0xFF;
      if (this._socket.Connected) {
        try {
          this._stream.Write(buf, 0, buf.Length);
          if (Verbose && rep) {
            Log.Debug("{0}.Send({1})", this.ToString(), ms);
          }
        }catch(Exception ex) {
          Log.Warning("EsSocketTCP.SendArr({0}, {1}) - {2}", ms, rep, ex.Message);
        }
      }
    }
    public IPEndPoint RemoteEndPoint { get { return (_socket == null || !_socket.Connected || _socket.Client == null || !_socket.Client.Connected) ? (new IPEndPoint(IPAddress.Broadcast, 65535)) : (IPEndPoint)_socket.Client.RemoteEndPoint; } }
    #endregion IEsSocket Members
    public override string ToString() {
      if (_socket == null || !_socket.Connected || _socket.Client == null || !_socket.Client.Connected) {
        return "Disconected";
      }
      var rep = (IPEndPoint)_socket.Client.RemoteEndPoint;
      return Convert.ToBase64String(rep.Address.GetAddressBytes().Union(BitConverter.GetBytes((ushort)rep.Port)).ToArray()).TrimEnd('=').Replace('/', '*');
    }

    #region IDisposable Member
    public void Dispose() {
      Dispose(false);
    }
    private void Dispose(bool info) {
      if (Interlocked.Exchange(ref _connected, 0) != 0) {
        _stream.Close();
        _socket.Close();
        if (info) {
          _callback(new EsMessage(this, new JST.Array { 99 }));
        }
      }
    }
    #endregion IDisposable Member
  }
}
