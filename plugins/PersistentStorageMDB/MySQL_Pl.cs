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

namespace X13.PersistentStorage {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "MySQL")]
  internal class MySQL_Pl : PersistentStorageBase, IPlugModul {
    private const string DB_NAME = "Enviriot";
    private const string KEEP_FIELD = "Arch.keep";
    private bool _showError;
    private DateTime _lastConnect;
    private string _connStr;
    private MySqlConnection _db;
    private readonly Dictionary<Topic, ArchLog> _dict;

    //TODO: Lazy connect

    public MySQL_Pl() : base("MySQL") {
      _dict = new Dictionary<Topic, ArchLog>();
      _showError = true;
      _lastConnect = DateTime.MinValue;
    }
    private void CheckConnection() {
      if (_db == null && (_showError || (DateTime.Now - _lastConnect).TotalSeconds > 7)) {
        try {
          if (_db == null) {
            _lastConnect = DateTime.Now;
            var db = new MySqlConnection(_connStr);
            db.Open();
            _db = db;
            _showError = true;
            X13.Log.Debug("MySQL connection opened");
          }
        }
        catch (Exception ex) {
          X13.Log.Error("MySQL.CheckConnection - {0}", ex.Message);
          _showError = false;
        }
      }
    }
    private void ExecuteNonQuery(string command, params object[] args) {
      CheckConnection();
      try {
        lock (_db) {
          using (MySqlCommand cmd = new MySqlCommand(command, _db)) {
            for (int i = 0; i < args.Length; i++) {
              cmd.Parameters.AddWithValue("P" + i.ToString(), args[i]);
            }
            cmd.ExecuteNonQuery();
          }
        }
      }
      catch (Exception ex) {
        Log.Error("MySQL.ExecuteNonQuery({0}) - {1}", command, ex);
        if (_db != null && (_db.State == System.Data.ConnectionState.Closed || _db.State == System.Data.ConnectionState.Broken)) {
          CloseDB();
        }
      }

    }
    private void CloseDB() {
      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        try {
          db.Close();
        }
        catch (Exception ex) {
          Log.Warning("MySQL.CloseDB - {0}", ex);
        }
      }
    }

    private class ArchLog {
      public ArchLog(long id, string path) {
        this.id = id;
        this.path = path;
      }
      public readonly long id;
      public readonly string path;
      public double keep;
      public SubRec sr;
    }
    #region Archivist
    protected override void OpenOrCreateArch() {
      string sUrl = "tcp://user:pa$w0rd@localhost/";
      var tUrl = _owner.Get("url", true, _owner);
      if (tUrl.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(tUrl.GetState().As<string>())) {
        tUrl.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
        tUrl.SetState(sUrl);
      } else {
        sUrl = tUrl.GetState().As<string>();
      }
      var uri = new Uri(sUrl);
      var builder = new MySqlConnectionStringBuilder { Server = uri.DnsSafeHost };
      if (!string.IsNullOrEmpty(uri.UserInfo)) {
        var items = uri.UserInfo.Split(new[] { ':' });
        if (items.Length > 1) {
          builder.UserID = items[0];
          builder.Password = items[1];
        } else {
          builder.UserID = uri.UserInfo;
        }
      }
      if (uri.Port > 0) {
        builder.Port = (uint)uri.Port;
      }
      var db = new MySqlConnection(builder.ConnectionString);
      while (true) {
        try {
          db.Open();
          break;
        }
        catch (MySqlException ex) {
          if (ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost) {
            if (_showError || (DateTime.Now - _lastConnect).TotalSeconds > 300) {
              X13.Log.Error("MySQL.OpenOrCreate - {0}", ex.Message);
              _lastConnect = DateTime.Now;
            }
            _showError = false;
            Thread.Sleep(30000);
          } else {
            throw;
          }
        }
      }

      bool exist;
      using (var cmd = db.CreateCommand()) {
        cmd.CommandText = "select count(*) from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = @name;";
        cmd.Parameters.AddWithValue("name", DB_NAME);
        exist = 1 == (long)cmd.ExecuteScalar();
      }
      if (!exist) {
        using (var cmd = db.CreateCommand()) {
          cmd.CommandText = "create database " + DB_NAME + " default charset=utf8mb4 collate=utf8mb4_general_ci;";
          cmd.ExecuteNonQuery();
        }
        Log.Info("MySQL. Database \"{0}\" on {1} created", DB_NAME, builder.Server);
      }
      db.Close();
      builder.Database = DB_NAME;
      _connStr = builder.ToString();
      CheckConnection();
      if (!exist) {
        using (var batch = _db.CreateBatch()) {
          batch.BatchCommands.Add(new MySqlBatchCommand("create table ARCH_W(ID int auto_increment primary key, P text not null, KEEP double not null, DT1 datetime(3));"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create table ARCH(ID int auto_increment primary key, P int not null, DT datetime(3) not null, V double not null, key ARCH_FK (p), key ARCH_DT_IDX (DT) using btree, constraint ARCH_FK foreign key (P) references ARCH_W(ID) on delete cascade);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create procedure PurgeArchProc() begin declare v_id int(11); declare v_keep double; select ID, KEEP*24*60*60 into v_id, v_keep from ARCH_W where DT1 < now() or DT1 is null order by DT1 limit 1; if v_id is not null then delete from ARCH where p = v_id AND DT < timestampadd(second, -v_keep, now(3)); update ARCH_W set DT1 = timestampadd(second, least(2*v_keep, 60*60*(12+12*rand())), now(3)) where ID = v_id; end if; end;"));
          batch.BatchCommands.Add(new MySqlBatchCommand("set global event_scheduler = on;"));
          batch.BatchCommands.Add(new MySqlBatchCommand("create event PurgeArch on schedule every 10 second do call PurgeArchProc();"));
          batch.ExecuteNonQuery();
        }
      } else {
        using (var cmd = new MySqlCommand("select ID, P, KEEP from ARCH_W order by P", _db)) {
          using (var r = cmd.ExecuteReader()) {
            while (r.Read()) {
              try {
                var ar = new ArchLog(r.GetInt64(0), r.GetString(1)) { keep = r.GetDouble(2) };
                var t = Topic.I.Get(Topic.root, ar.path, true, _owner, false, false);
                _dict.Add(t, ar);
              }
              catch (Exception ex) {
                Log.Warning("OpenOrCreateArch().Load - {0}", ex.Message);
              }
            }
          }

        }
      }
    }
    protected override void SaveArch(Topic t) {
      ExecuteNonQuery("insert into ARCH(P, DT, V) values(@P0, @P1, @P2)", GetOrCreate(t).id, DateTime.Now, t.GetState().As<double>());
    }
    private ArchLog GetOrCreate(Topic t) {
      if (!_dict.TryGetValue(t, out var ar)) {
        var keepJ = t.GetField(KEEP_FIELD);
        var keep = ((keepJ.ValueType == JSC.JSValueType.Double && !double.IsNaN(keepJ.As<double>())) || keepJ.ValueType == JSC.JSValueType.Integer) ? keepJ.As<double>() : 7.0;
        MySqlCommand cmd = null;
        try {
          lock (_db) {
            cmd = _db.CreateCommand();
            cmd.CommandText = "insert into ARCH_W(P, KEEP, DT1) values (@path, @keep, @dt1);";
            cmd.Parameters.AddWithValue("path", t.path);
            cmd.Parameters.AddWithValue("keep", keep);
            cmd.Parameters.AddWithValue("dt1", DateTime.Now);
            cmd.ExecuteNonQuery();
          }
          ar = new ArchLog(cmd.LastInsertedId, t.path) { keep = keep, sr = t.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.Once, KEEP_FIELD, OnKeepChanged) };
          _dict.Add(t, ar);
        }
        catch (Exception ex) {
          if (_db != null && (_db.State == System.Data.ConnectionState.Closed || _db.State == System.Data.ConnectionState.Broken)) {
            CloseDB();
          }
          X13.Log.Warning("MySQL.GetOrCreate({0}) - {1}", t.path, ex.Message);
          return null;
        }
        finally {
          cmd?.Dispose();
        }
      }
      return ar;
    }
    protected override void IdleTaskArch() {
    }
    protected override void CloseArch() {
      CloseDB();
    }
    protected override JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end) {
      //var sw = System.Diagnostics.Stopwatch.StartNew();
      var rez = new JSL.Array();
      CheckConnection();
      var p_ids = topics.Select(z => GetOrCreate(Topic.root.Get(z, false))).ToArray();
      try {
        lock (_db) {
          using (var cmd = _db.CreateCommand()) {
            for (int i = 0; i < topics.Length; i++) {
              var pn = "P" + i.ToString();
              cmd.Parameters.AddWithValue(pn, p_ids[i].id);
            }
            var pi = string.Join(" ,", Enumerable.Range(0, topics.Length).Select(p => "@P" + p.ToString()));

            cmd.Parameters.AddWithValue("BEGIN", begin);

            if (end <= begin || count == 0) {  // end == MinValue
              if (count < 0) {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT<@BEGIN order by DT desc limit @COUNT";
                cmd.Parameters.AddWithValue("COUNT", -count);
              } else if (count == 0) {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT between @BEGIN and @END order by DT";
                cmd.Parameters.AddWithValue("END", end);
              } else {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT>@BEGIN order by DT limit @COUNT";
                cmd.Parameters.AddWithValue("COUNT", count);
              }
              using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                  JSL.Array lo = null;
                  while (reader.Read()) {
                    var p_id = reader.GetInt64(0);
                    var dt = reader.GetDateTime(1);
                    var v = new JSL.Number(reader.GetDouble(2));
                    int i;
                    for (i = 0; p_id != p_ids[i].id; i++)
                      ;
                    i++;
                    if (lo != null && lo[i].ValueType == JSC.JSValueType.Object && (dt - ((JSL.Date)lo[0].Value).ToDateTime()).TotalSeconds < 15) {  // Null.ValueType==Object
                      lo[i] = v;
                    } else {
                      lo = new JSL.Array(topics.Length + 1) {
                        [0] = JSC.JSValue.Marshal(dt)
                      };
                      for (var j = 1; j <= topics.Length; j++) {
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

              for (i = 0; i < topics.Length; i++) {
                f_val[i] = 0;
                f_cnt[i] = 0;
                l_delta[i] = -step;
                using (var cmd2 = _db.CreateCommand()) {
                  cmd2.CommandText = "select V from ARCH where P=@PATH and DT<@BEGIN order by DT desc limit 1";
                  cmd2.Parameters.AddWithValue("@PATH", p_ids[i].id);
                  cmd2.Parameters.AddWithValue("@BEGIN", begin);
                  var r = cmd2.ExecuteScalar();
                  if (r is double v) {
                    l_val[i] = v;
                  } else {
                    l_val[i] = double.NaN;
                  }
                }
              }
              cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT between @BEGIN AND @END order by DT";
              cmd.Parameters.AddWithValue("END", end);
              using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                  while (reader.Read()) {
                    var p_id = reader.GetInt64(0);
                    var t_cur = reader.GetDateTime(1);
                    var v = new JSL.Number(reader.GetDouble(2));
                    if (t_cur >= cursor) {
                      AddRecord();
                      do {
                        cursor = cursor.AddSeconds(step);
                      } while (t_cur >= cursor);
                    }
                    for (i = 0; i < topics.Length; i++) {
                      if (p_id == p_ids[i].id) {
                        if (!double.IsNaN(v)) {
                          var td = (t_cur - cursor).TotalSeconds;
                          if (!double.IsNaN(l_val[i])) {
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
                for (i = 0; i < topics.Length; i++) {
                  lo[i + 1] = f_cnt[i] > 1 ? new JSL.Number(f_val[i] + l_val[i] * (-l_delta[i]) / step) : (double.IsNaN(l_val[i]) ? JSC.JSValue.Null : l_val[i]);
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
      catch (Exception ex) {
        Log.Error("MySQL.AQuery() - {0}", ex);
        if (_db != null && (_db.State == System.Data.ConnectionState.Closed || _db.State == System.Data.ConnectionState.Broken)) {
          CloseDB();
        }
      }
      //sw.Stop();
      //Log.Debug("AQuery([{0}], {1:yyMMdd'T'HHmmss}, {2}, {3:yyMMdd'T'HHmmss}) {4:0.0} mS", string.Join(", ", topics), begin, count, end, sw.Elapsed.TotalMilliseconds);
      return rez;
    }
    private void OnKeepChanged(Perform p, SubRec sr) {
      if (_dict.TryGetValue(p.src, out var ar)) {
        if (p.Art == Perform.E_Art.changedField) {
          var keepJ = p.src.GetField(KEEP_FIELD);
          var keep = ((keepJ.ValueType == JSC.JSValueType.Double && !double.IsNaN(keepJ.As<double>())) || keepJ.ValueType == JSC.JSValueType.Integer) ? keepJ.As<double>() : 7.0;
          if (Math.Abs(keep - ar.keep) > double.Epsilon) {
            ExecuteNonQuery("update ARCH_W set KEEP = @P1 where ID=@P0;", ar.id, keep);
            ar.keep = keep;
          }
        } else if(p.Art == Perform.E_Art.move) {
          ExecuteNonQuery("update ARCH_W set P = @P1 where ID=@P0;", ar.id, p.src.path);
          _dict[p.src] = new ArchLog(ar.id, p.src.path) { keep = ar.keep, sr = p.src.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.Once, KEEP_FIELD, OnKeepChanged) };
        }
      }
    }
    #endregion Archivist
  }
}
