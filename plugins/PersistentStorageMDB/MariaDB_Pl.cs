///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JSL = NiL.JS.BaseLibrary;
using JSC = NiL.JS.Core;
using NiL.JS.Extensions;
using X13.Repository;
using MySqlConnector;
using System.Security.Cryptography;
using System.Xml.Serialization;

namespace X13.PersistentStorage {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "MariaDB")]
  internal class MariaDB_Pl : PersistentStorageBase, IPlugModul {
    private const string DB_NAME = "Enviriot";
    private bool _showError;
    private DateTime _lastConnect;
    private string _connStr;
    private MySqlConnection _db;
    private readonly Dictionary<string, long> _idCache;

    public MariaDB_Pl() : base("MariaDB") {
      _idCache = new Dictionary<string, long>();
      _showError = true;
      _lastConnect = DateTime.MinValue;
      JsExtLib.AQuery = this.AQuery;
    }
    private void CheckConnection() {
      if(_db == null && (_showError || (DateTime.Now - _lastConnect).TotalSeconds > 7)) {
        try {
          if(_db == null) {
            _lastConnect = DateTime.Now;
            var db = new MySqlConnection(_connStr);
            db.Open();
            _db = db;
            _showError = true;
            X13.Log.Debug("DB connection opened");
          }
        }
        catch(Exception ex) {
          X13.Log.Error("DB.CheckConnection - {0}", ex.Message);
          _showError = false;
        }
      }
    }
    private void ExecuteNonQuery(string command, params object[] args) {
      CheckConnection();
      try {
        lock(_db) {
          using(MySqlCommand cmd = new MySqlCommand(command, _db)) {
            for(int i = 0; i< args.Length; i++) {
              cmd.Parameters.AddWithValue("P"+i.ToString(), args[i]);
            }
            cmd.ExecuteNonQuery();
          }
        }
      }
      catch(Exception ex) {
        Log.Error("PS.ExecuteNonQuery({0}) - {1}", command, ex);
        CloseDB();
      }

    }
    private void CloseDB() {
      var db = Interlocked.Exchange(ref _db, null);
      if(db != null) {
        try {
          db.Close();
        }
        catch(Exception ex) {
          Log.Warning("PS.CloseDB - {0}", ex);
        }
      }
    }

    #region Persisten Storage Members
    protected override void ThreadM() {
      LoadOrCreateDB();
      _tick.Set();

      do {
        if(_tick.WaitOne(15)) {
          while(_q.TryDequeue(out Perform p)) {
            try {
              Save(p);
            }
            catch(Exception ex) {
              Log.Warning("PersistentStorage(" + (p == null ? "null" : p.ToString()) + ") - " + ex.ToString());
            }
          }
        }/* else {
          try {
            OptimizeArch();
          }
          catch(Exception ex) {
            Log.Warning("OptimizeArch() - " + ex.ToString());
          }
        }*/
      } while(!_terminate);
      CloseDB();
    }
    private void LoadOrCreateDB() {
      string sUrl = "tcp://user:pa$w0rd@localhost/";
      var tUrl = _owner.Get("url", true, _owner);
      if(tUrl.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(tUrl.GetState().As<string>())) {
        tUrl.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
        tUrl.SetState(sUrl);
      } else {
        sUrl = tUrl.GetState().As<string>();
      }
      var uri = new Uri(sUrl);
      var builder = new MySqlConnectionStringBuilder { Server = uri.DnsSafeHost };
      if(!string.IsNullOrEmpty(uri.UserInfo)) {
        var items = uri.UserInfo.Split(new[] { ':' });
        if(items.Length > 1) {
          builder.UserID = items[0];
          builder.Password = items[1];
        } else {
          builder.UserID = uri.UserInfo;
        }
      }
      if(uri.Port > 0) {
        builder.Port = (uint)uri.Port;
      }
      _connStr = builder.ToString();
      CheckConnection();
      bool exist;
      using(var cmd = _db.CreateCommand()) {
        cmd.CommandText = "select count(*) from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = @name;";
        cmd.Parameters.AddWithValue("name", DB_NAME);
        exist = 1 == (long)cmd.ExecuteScalar();
      }
      if(!exist) {
        using(var cmd = _db.CreateCommand()) {
          cmd.CommandText = "create database " + DB_NAME + ";";
          cmd.ExecuteNonQuery();
        }
      }
      _db.Close();
      builder.Database = DB_NAME;
      _db = new MySqlConnection(builder.ConnectionString);
      _db.Open();
      if(!exist) {
        using(var batch = _db.CreateBatch()) {
          batch.BatchCommands.Add(new MySqlBatchCommand("create table PS(ID int auto_increment primary key, P text not null, M text, S text);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create table LOGS(ID int auto_increment primary key, DT datetime(3) not null, L tinyint not null, M text not null, key `LOGS_DT_IDX` (`DT`) using btree);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create table ARCH(ID int not null auto_increment, P int not null, DT datetime(3) not null, V double not null, primary key (ID), key ARCH_FK (p), key ARCH_DT_IDX (DT) using btree, constraint ARCH_FK foreign key (P) references PS(ID) on delete cascade)"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create table ARCH_W(P int primary key, DT1 datetime(3), DT2 datetime(3), constraint ARCH_W_FK foreign key (P) references PS(ID) on delete cascade);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create trigger ARCH_DATA after update on PS for each row begin if (new.S != old.S and json_value(new.M, '$.Arch.enable') and (json_type(new.S) = 'INTEGER' or json_type(new.S) = 'DOUBLE')) then insert into ARCH (P, DT, V) values (new.ID, now(), json_value(new.S, '$')); end if; end"));
          batch.ExecuteNonQuery();
        }
      } else {
        using(var cmd = new MySqlCommand("select * from PS order by P", _db)) {
          using(var r = cmd.ExecuteReader()) {
            while(r.Read()) {
              long id = r.GetInt64(0);
              string path = r.GetString(1);
              var manifest = JsLib.ParseJson(r.GetString(2));
              _idCache[path] = id;

              var t = Topic.I.Get(Topic.root, path, true, _owner, false, false);
              // check version
              {
                var jTmp = t.GetField("version");
                string sTmp;

                if(jTmp.ValueType == JSC.JSValueType.String && (sTmp = jTmp.Value as string) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out Version vRepo)) {
                  jTmp = manifest["version"];
                  if(jTmp.ValueType != JSC.JSValueType.String || (sTmp = jTmp.Value as string) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out Version vDB) || vRepo > vDB) {
                    continue; // skip load, old version
                  }
                }
              }
              // check attribute
              JSC.JSValue attr;
              bool saved;
              if(manifest == null || manifest.ValueType != JSC.JSValueType.Object || manifest.Value == null || !(attr = manifest["attr"]).IsNumber) {
                saved = false;
              } else {
                saved = ((int)attr & (int)Topic.Attribute.Saved) == (int)Topic.Attribute.DB;
              }

              var state = saved ? JsLib.ParseJson(r.GetString(3)) : null;
              Topic.I.Fill(t, state, manifest, _owner);
            }
          }
        }
      }
      Log.History = History;
      Log.Write += Log_Write;

    }
    private void Save(Perform p) {
      Topic t = p.src;
      long id;
      if(!_idCache.TryGetValue(t.path, out id)) {
        if(p.Art == Perform.E_Art.remove) {
          return;
        }
        CheckConnection();
        try {
          lock(_db) {
            using(var cmd = new MySqlCommand("SELECT id FROM PS WHERE p=@path", _db)) {
              cmd.Parameters.AddWithValue("path", t.path);
              var oid = cmd.ExecuteScalar();
              if(oid is long nid) {
                id = nid;
              } else {
                id = 0;
              }
            }
          }
        }
        catch(Exception ex) {
          Log.Error("PS.Save1() - {0}", ex);
          CloseDB();
        }
      }

      if(p.Art == Perform.E_Art.remove) {
        _idCache.Remove(t.path);
        ExecuteNonQuery("delete from PS where ID=@P0", id);
      } else if(p.Art == Perform.E_Art.move) {
        ExecuteNonQuery("update PS set P=@P1 where ID=@P0", id, t.path);
      } else {   //create, changedField, changedState
        bool saveM = p.Art == Perform.E_Art.create || p.Art == Perform.E_Art.changedField;
        bool saveS = (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB) || t.GetField("Arch.enable").As<bool>())
          && (p.Art == Perform.E_Art.create || p.Art == Perform.E_Art.changedState);

        if(id == 0) {
          CheckConnection();
          try {
            lock(_db) {
              using(MySqlCommand cmd = _db.CreateCommand()) {
                if(saveS) {
                  cmd.CommandText = "insert into PS (P, M, S) values (@path, @manifest, @state);";
                  cmd.Parameters.AddWithValue("state", JsLib.Stringify(t.GetState()));
                } else {
                  cmd.CommandText = "insert into PS (P, M) values (@path, @manifest);";
                }
                cmd.Parameters.AddWithValue("path", t.path);
                cmd.Parameters.AddWithValue("manifest", JsLib.Stringify(t.GetField(null)));
                cmd.ExecuteNonQuery();
                _idCache[t.path] = cmd.LastInsertedId;
              }
            }
          }
          catch(Exception ex) {
            Log.Error("PS.Save4() - {0}", ex);
            CloseDB();
          }
        } else {
          if(saveM && saveS) {
            ExecuteNonQuery("update PS set M = @P1, S= @P2 where ID=@P0", id, JsLib.Stringify(t.GetField(null)), JsLib.Stringify(t.GetState()));
          } else if(saveS) {
            ExecuteNonQuery("update PS set S= @P1 where ID=@P0;", id, JsLib.Stringify(t.GetState()));
          } else if(saveM){
            ExecuteNonQuery("update PS set M = @P1 where ID=@P0", id, JsLib.Stringify(t.GetField(null)));
          }
        }
      }
    }
    #endregion Persisten Storage Members

    #region History
    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      if(ll != LogLevel.Debug) {
        CheckConnection();
        try {
          lock(_db) {
            using(var cmd = new MySqlCommand("insert into LOGS(DT, L, M) values(@dt, @level, @message);", _db)) {
              cmd.Parameters.AddWithValue("dt", dt);
              cmd.Parameters.AddWithValue("level", (int)ll);
              cmd.Parameters.AddWithValue("message", msg);
              cmd.ExecuteNonQuery();
            }
          }

        }
        catch(Exception ex) {
          Log.Error("PS.Log_Write() - {0}", ex);
          CloseDB();
        }
      }
    }
    private IEnumerable<Log.LogRecord> History(DateTime dt, int cnt) {
      var lst = new List<Log.LogRecord>();
      CheckConnection();
      try {
        lock(_db) {
          using(var cmd = new MySqlCommand("select DT, L, M from LOGS where DT < @dt order by DT desc LIMIT @count;", _db)) {
            cmd.Parameters.AddWithValue("dt", dt);
            cmd.Parameters.AddWithValue("count", cnt);
            using(var reader = cmd.ExecuteReader()) {
              while(reader.Read()) {
                lst.Add(new Log.LogRecord() { dt = reader.GetDateTime(0), ll = (LogLevel)reader.GetInt32(1), format = reader.GetString(2) });
              }
            }
          }
        }
      }
      catch(Exception ex) {
        Log.Error("PS.History() - {0}", ex);
        CloseDB();
      }

      return lst;
    }
    #endregion History

    #region Archivist

    private JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end) {
      //var sw = System.Diagnostics.Stopwatch.StartNew();
      var rez = new JSL.Array();
      var p_ids = topics.Select(z => _idCache[z]).ToArray();
      CheckConnection();
      try {
        lock(_db) {
          using(var cmd = _db.CreateCommand()) {
            for(int i = 0; i < topics.Length; i++) {
              var pn = "P" + i.ToString();
              cmd.Parameters.AddWithValue(pn, p_ids[i]);
            }
            var pi = string.Join(" ,", Enumerable.Range(0, topics.Length).Select(p => "P" + p.ToString()));

            cmd.Parameters.AddWithValue("BEGIN", begin);

            if(end <= begin || count == 0) {  // end == MinValue
              if(count < 0) {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT<@BEGIN order by DT desc limit @COUNT";
                cmd.Parameters.AddWithValue("COUNT", -count);
              } else if(count == 0) {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT between @BEGIN and @END order by DT";
                cmd.Parameters.AddWithValue("END", end.ToUniversalTime());
              } else {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT>@BEGIN order by DT limit @COUNT";
                cmd.Parameters.AddWithValue("COUNT", count);
              }
              using(var reader = cmd.ExecuteReader()) {
                if(reader.HasRows) {
                  JSL.Array lo = null;
                  while(reader.Read()) {
                    var p_id = reader.GetInt64(0);
                    var dt = reader.GetDateTime(1);
                    var v = new JSL.Number(reader.GetDouble(2));
                    int i;
                    for(i = 0; p_id != p_ids[i]; i++)
                      ;
                    i++;
                    if(lo != null && lo[i].ValueType == JSC.JSValueType.Object && (dt - ((JSL.Date)lo[0].Value).ToDateTime()).TotalSeconds < 15) {  // Null.ValueType==Object
                      lo[i] = v;
                    } else {
                      lo = new JSL.Array(topics.Length + 1) {
                        [0] = JSC.JSValue.Marshal(dt)
                      };
                      for(var j = 1; j <= topics.Length; j++) {
                        lo[j] = (i == j) ? v : JSC.JSValue.Null;
                      }
                      rez.Add(lo);
                    }
                  }
                }
              }
            } else {
              var step = (end - begin).TotalSeconds / Math.Abs(count);

              DateTime cursor = begin.AddSeconds(step);
              var f_cnt = new int[topics.Length];
              var f_val = new double[topics.Length];
              var l_val = new double[topics.Length];
              var l_delta = new double[topics.Length];
              var t_cnt = 0;
              double t_sum = 0;
              int i;

              for(i = 0; i < topics.Length; i++) {
                f_val[i] = 0;
                f_cnt[i] = 0;
                l_delta[i] = -step;
                using(var cmd2 = _db.CreateCommand()) {
                  cmd2.CommandText = "select V from ARCH where P=@PATH and DT<@BEGIN order by DT desc limit 1";
                  cmd2.Parameters.AddWithValue("@PATH", p_ids[i]);
                  cmd2.Parameters.AddWithValue("@BEGIN", begin);
                  var r = cmd2.ExecuteScalar();
                  if(r is double v) {
                    l_val[i] = v;
                  } else {
                    l_val[i] = double.NaN;
                  }
                }
              }
              cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT between @BEGIN AND @END order by DT";
              cmd.Parameters.AddWithValue("END", end.ToUniversalTime());
              using(var reader = cmd.ExecuteReader()) {
                if(reader.HasRows) {
                  while(reader.Read()) {
                    var p = reader.GetString(0);
                    var t_cur = reader.GetDateTime(1).ToLocalTime();
                    var v = new JSL.Number(reader.GetDouble(2));
                    if(t_cur >= cursor) {
                      AddRecord();
                      do {
                        cursor = cursor.AddSeconds(step);
                      } while(t_cur >= cursor);
                    }
                    for(i = 0; i < topics.Length; i++) {
                      if(p == topics[i]) {
                        if(!double.IsNaN(v)) {
                          var td = (t_cur - cursor).TotalSeconds;
                          if(!double.IsNaN(l_val[i])) {
                            f_val[i] += l_val[i] * (td - l_delta[i]) / step;
                            l_delta[i] = td;
                          }
                          f_cnt[i]++;
                          l_val[i] = v;
                          t_cnt++;
                          t_sum += td;
                        }
                        break;
                      }
                    }
                  }
                }
              }
              AddRecord();
              void AddRecord() {
                JSL.Array lo = new JSL.Array(topics.Length + 1) {
                  [0] = JSC.JSValue.Marshal(cursor.AddSeconds(t_cnt == 1 ? t_sum : (-step / 2)).ToLocalTime()),
                };
                t_cnt = 0;
                t_sum = 0;
                for(i = 0; i < topics.Length; i++) {
                  lo[i + 1] = f_cnt[i] > 0 ? new JSL.Number(f_val[i] + l_val[i] * (-l_delta[i]) / step) : (double.IsNaN(l_val[i]) ? JSC.JSValue.Null : l_val[i]);
                  f_val[i] = 0;
                  f_cnt[i] = 0;
                  l_delta[i] = -step;
                }
                rez.Add(lo);
              }
            }
          }
        }
      }
      catch(Exception ex) {
        Log.Error("PS.AQuery() - {0}", ex);
        CloseDB();
      }
      //sw.Stop();
      //Log.Debug("AQuery([{0}], {1:yyMMdd'T'HHmmss}, {2}, {3:yyMMdd'T'HHmmss}) {4:0.0} mS", string.Join(", ", topics), begin, count, end, sw.Elapsed.TotalMilliseconds);
      return rez;
    }
    #endregion Archivist
  }
}
