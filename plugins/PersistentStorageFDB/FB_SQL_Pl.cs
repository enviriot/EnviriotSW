﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
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
using FirebirdSql.Data.FirebirdClient;
using LiteDB;

namespace X13.PersistentStorage {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "FBSQL")]
  internal class FB_SQL_Pl : PersistentStorageBase, IPlugModul {
    private const string DB_NAME = "../data/persist.fdb";
    private const string KEEP_FIELD = "Arch.keep";
    private FbConnection _db;
    private readonly Dictionary<Topic, ArchLog> _dict;

    //TODO: Lazy connect

    public FB_SQL_Pl() : base("Firebird") {
      _dict = new Dictionary<Topic, ArchLog>();
    }
    private void CheckConnection() {
    }
    private void ExecuteNonQuery(string command, params object[] args) {
      CheckConnection();
      try {
        lock (_db) {
          using (var cmd = _db.CreateCommand()) {
            cmd.CommandText = command;
            for (int i = 0; i < args.Length; i++) {
              cmd.Parameters.AddWithValue("P" + i.ToString(), args[i]);
            }
            cmd.ExecuteNonQuery();
          }
        }
      }
      catch (Exception ex) {
        Log.Error("FB_SQL.ExecuteNonQuery({0}) - {1}", command, ex);
      }

    }
    private void CloseDB() {
      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        try {
          db.Close();
        }
        catch (Exception ex) {
          Log.Warning("FB_SQL.CloseDB - {0}", ex);
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

      FbConnectionStringBuilder conn = new FbConnectionStringBuilder {
        ServerType = FbServerType.Embedded,
        ClientLibrary = "fbclient.dll",
        UserID = "SYSDBA",
        Password = "masterkey",
        Database = DB_NAME,
      };
      bool exist = File.Exists(DB_NAME);
      if (!exist) {
        FbConnection.CreateDatabase(conn.ConnectionString);
      }

      _db = new FbConnection(conn.ConnectionString);
      _db.Open();

      if (!exist) {
        using (var cmd = _db.CreateCommand()) {
          cmd.CommandText = "CREATE TABLE ARCH_W (\r\n\tID INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL,\r\n\tP VARCHAR(250) NOT NULL,\r\n\tKEEP DOUBLE PRECISION NOT NULL,\r\n\tDT TIMESTAMP,\r\n\tCONSTRAINT ARCH_W_PK PRIMARY KEY (ID),\r\n\tCONSTRAINT ARCH_W_UNIQUE UNIQUE (P)\r\n);";
          cmd.ExecuteNonQuery();
        }
        using (var cmd = _db.CreateCommand()) {
          cmd.CommandText = "CREATE TABLE ARCH (\r\n\tID INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL,\r\n\tP INTEGER NOT NULL,\r\n\tDT TIMESTAMP NOT NULL,\r\n\tV DOUBLE PRECISION NOT NULL,\r\n\tCONSTRAINT ARCH_PK PRIMARY KEY (ID),\r\n\tCONSTRAINT ARCH_ARCH_W_FK FOREIGN KEY (P) REFERENCES ARCH_W(ID) ON DELETE CASCADE\r\n);";
          cmd.ExecuteNonQuery();
        }
        using (var cmd = _db.CreateCommand()) {
          cmd.CommandText = "CREATE INDEX ARCH_DT_IDX ON ARCH (DT);";
          cmd.ExecuteNonQuery();
        }
        /*
        using (var cmd = db.CreateCommand()) {
          cmd.CommandText = "create procedure PurgeArchProc() begin declare v_id int(11); declare v_keep double; select ID, KEEP*24*60*60 into v_id, v_keep from ARCH_W where DT < now() or DT is null order by DT limit 1; if v_id is not null then delete from ARCH where p = v_id AND DT < timestampadd(second, -v_keep, now(3)); update ARCH_W set DT = timestampadd(second, least(2*v_keep, 60*60*(12+12*rand())), now(3)) where ID = v_id; end if; end;";
          cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand()) {
          cmd.CommandText = "set global event_scheduler = on;";
          cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand()) {
          cmd.CommandText = "create event PurgeArch on schedule every 10 second do call PurgeArchProc();";
          cmd.ExecuteNonQuery();
        }*/
      } else {
        using (var cmd = _db.CreateCommand()) {
          cmd.CommandText = "select ID, P, KEEP from ARCH_W order by P";
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
        FbCommand cmd = null;
        long new_id;
        try {
          lock (_db) {
            cmd = _db.CreateCommand();
            cmd.CommandText = "insert into ARCH_W(P, KEEP, DT) values (@path, @keep, @dt) returning ID;";
            cmd.Parameters.AddWithValue("path", t.path);
            cmd.Parameters.AddWithValue("keep", keep);
            cmd.Parameters.AddWithValue("dt", DateTime.Now);
            new_id = (int)cmd.ExecuteScalar();
          }
          ar = new ArchLog(new_id, t.path) { keep = keep, sr = t.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.Once, KEEP_FIELD, OnKeepChanged) };
          _dict.Add(t, ar);
        }
        catch (Exception ex) {
          X13.Log.Warning("FB_SQL.GetOrCreate({0}) - {1}", t.path, ex.Message);
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
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT<@BEGIN order by DT desc fetch first @COUNT rows only";
                cmd.Parameters.AddWithValue("COUNT", -count);
              } else if (count == 0) {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT between @BEGIN and @END order by DT";
                cmd.Parameters.AddWithValue("END", end);
              } else {
                cmd.CommandText = "select P, DT, V from ARCH where P in (" + pi + ") and DT>@BEGIN order by DT  fetch first @COUNT rows only";
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
                  cmd2.CommandText = "select V from ARCH where P=@PATH and DT<@BEGIN order by DT desc fetch first row only";
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
        Log.Error("FB_SQL.AQuery() - {0}", ex);
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
        } else if (p.Art == Perform.E_Art.move) {
          ExecuteNonQuery("update ARCH_W set P = @P1 where ID=@P0;", ar.id, p.src.path);
          _dict[p.src] = new ArchLog(ar.id, p.src.path) { keep = ar.keep, sr = p.src.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.Once, KEEP_FIELD, OnKeepChanged) };
        }
      }
    }
    #endregion Archivist
  }
}
