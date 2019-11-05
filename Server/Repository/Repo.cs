///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace X13.Repository {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 1)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Repository")]
  public class Repo : IPlugModul {
    #region internal Members
    internal string cfg;
    private readonly ConcurrentQueue<Perform> _tcQueue;
    private readonly List<Perform> _prOp;
    private readonly List<Action<Perform>> _subscribers;
    private int _busyFlag;
    private int _pfPos;

    internal void DoCmd(Perform cmd, bool intern) {
      if(intern && _prOp.Count > 0 && _pfPos < _prOp.Count) {
        TickStep1(cmd);
        TickStep2(cmd);
      } else {
        _tcQueue.Enqueue(cmd);               // Process in next tick
      }
    }
    internal void SubscribeAll(Action<Perform> func) {
      _subscribers.Add(func);
    }

    private int EnquePerf(Perform cmd) {
      int i;
      for(i = 0; i < _prOp.Count; i++) {
        if(_prOp[i].EqualsGr(cmd)) {
          if(_prOp[i].Art == Perform.ArtEnum.changedState) {
            cmd.old_o = _prOp[i].old_o;
          }
          _prOp.RemoveAt(i);
          if(_pfPos >= i) {
            _pfPos--;
          }
          break;
        }
      }
      i = ~_prOp.BinarySearch(cmd);
      _prOp.Insert(i, cmd);
      return i;
    }

    private void TickStep1(Perform c) {
      SubRec sr;

      switch(c.Art) {
      case Perform.ArtEnum.create:
        Topic.I.SubscribeByCreation(c.src);
        EnquePerf(c);
        break;
      case Perform.ArtEnum.subscribe:
      case Perform.ArtEnum.unsubscribe:
        if((sr = c.o as SubRec) != null) {
          Topic.Bill b = null;
          Perform np;
          if(c.Art == Perform.ArtEnum.subscribe && (sr.mask & SubRec.SubMask.Once) == SubRec.SubMask.Once) {
            EnquePerf(c);
          }
          if((sr.mask & SubRec.SubMask.Chldren) == SubRec.SubMask.Chldren) {
            b = c.src.Children;
          }
          if((sr.mask & SubRec.SubMask.All) == SubRec.SubMask.All) {
            b = c.src.All;
          }
          if(b != null) {
            foreach(Topic tmp in b) {
              if(c.Art == Perform.ArtEnum.subscribe) {
                Topic.I.Subscribe(tmp, sr);
                if((sr.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value
                  || (sr.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || string.IsNullOrEmpty(sr.prefix) || tmp.GetField(sr.prefix).Defined) {
                  np = Perform.Create(tmp, Perform.ArtEnum.subscribe, c.src);
                  np.o = c.o;
                  EnquePerf(np);
                }
              } else {
                Topic.I.RemoveSubscripton(tmp, sr);
              }
            }
          }
          if(c.Art == Perform.ArtEnum.subscribe) {
            np = Perform.Create(c.src, Perform.ArtEnum.subAck, c.src);
            np.o = c.o;
            EnquePerf(np);
          }
        }
        break;
      case Perform.ArtEnum.setField: {
          if(Topic.I.SetField(c)) {
            c.Art = Perform.ArtEnum.changedField;
            EnquePerf(c);
          }
        }
        break;

      case Perform.ArtEnum.changedState:
      case Perform.ArtEnum.setState:
      case Perform.ArtEnum.changedField:
      case Perform.ArtEnum.move:
      case Perform.ArtEnum.subAck:
        EnquePerf(c);
        break;
      case Perform.ArtEnum.remove:
        foreach(Topic tmp in c.src.All) {
          EnquePerf(Perform.Create(tmp, Perform.ArtEnum.remove, c.Prim));
        }
        break;
      }
    }
    private void TickStep2(Perform cmd) {
      if(cmd.Art == Perform.ArtEnum.remove || (cmd.Art == Perform.ArtEnum.setState && !object.ReferenceEquals(cmd.src.GetState(), cmd.o))) {
        cmd.old_o = cmd.src.GetState();
        Topic.I.SetValue(cmd.src, cmd.o as JSValue);
        if(cmd.Art != Perform.ArtEnum.remove) {
          cmd.Art = Perform.ArtEnum.changedState;
        }
      }
      if(cmd.Art == Perform.ArtEnum.changedField) {
        Topic.I.SetField2(cmd.src);
      }
      if(cmd.Art == Perform.ArtEnum.move) {
        Topic.I.SubscribeByMove(cmd.src);
      }
      if(cmd.Art == Perform.ArtEnum.remove) {
        Topic.I.Remove(cmd.src);
      }
    }
    private void CheckCCtor(Perform p) {
      SortedList<string, JSValue> lo = null, ln = null, lc = null;
      JSValue to = null, tn = p.src.GetField("type"), vn;
      if(p.Art == Perform.ArtEnum.changedField) {
        JSValue o = JsLib.GetField(p.old_o as JSValue, "cctor"), n = p.src.GetField("cctor");
        to = JsLib.GetField(p.old_o as JSValue, "type");
        if(!object.ReferenceEquals(o, n)) {
          JsLib.Propertys(ref lo, o);
          JsLib.Propertys(ref ln, n);
        }
      } else if(p.Art == Perform.ArtEnum.create) {
        JsLib.Propertys(ref ln, p.src.GetField("cctor"));
      } else if(p.Art == Perform.ArtEnum.remove) {
        JsLib.Propertys(ref lo, p.src.GetField("cctor"));
      } else {
        return;
      }
      if(!object.ReferenceEquals(to, tn)) {
        if(to!=null && to.ValueType == JSValueType.String && to.Value != null && Topic.Root.Get("$YS/TYPES", false).Exist(to.Value as string, out Topic t1)) {
          JsLib.Propertys(ref lo, JsLib.GetField(t1.GetState(), "cctor"));
        }
        if(tn != null && tn.ValueType == JSValueType.String && tn.Value != null && Topic.Root.Get("$YS/TYPES", false).Exist(tn.Value as string, out Topic t2)) {
          JsLib.Propertys(ref ln, JsLib.GetField(t2.GetState(), "cctor"));
        }
      }
      if(lo != null && ln != null) {
        foreach(var k in lo.Where(z => ln.ContainsKey(z.Key)).Select(z => z.Key).ToArray()) {
          vn = ln[k];
          if(!JSValue.ReferenceEquals(lo[k], vn)) {
            if(lc==null) {
              lc = new SortedList<string, JSValue>();
            }
            lc.Add(k, vn);
          }
          lo.Remove(k);
          ln.Remove(k);
        }
      }

      if(lo != null) {
        ProcessCCtor(lo, p.src, Perform.ArtEnum.remove);
      }
      if(ln != null) {
        ProcessCCtor(ln, p.src, Perform.ArtEnum.create);
      }
      if(lc != null) {
        ProcessCCtor(lc, p.src, Perform.ArtEnum.changedField);
      }
    }

    private void ProcessCCtor(SortedList<string, JSValue> l, Topic t, Perform.ArtEnum a) {
      foreach(var kv in l) {
        RPC.CCtor(kv.Key, t, a);
      }
    }

    #endregion internal Members

    public Repo() {
      _tcQueue = new ConcurrentQueue<Perform>();
      _prOp = new List<Perform>(128);
      _subscribers = new List<Action<Perform>>();
    }

    #region Import/Export
    public static bool Import(string fileName, string path = null) {
      if(string.IsNullOrEmpty(fileName) || !File.Exists(fileName)) {
        return false;
      }
      X13.Log.Info("Import {0}", fileName);
      using(StreamReader reader = File.OpenText(fileName)) {
        Import(reader, path);
      }
      return true;
    }
    public static void Import(TextReader reader, string path) {
      XDocument doc;
      using(var r = new System.Xml.XmlTextReader(reader)) {
        doc = XDocument.Load(r);
      }

      if(string.IsNullOrEmpty(path) && doc.Root.Attribute("path") != null) {
        path = doc.Root.Attribute("path").Value;
      }

      Import(doc.Root, null, path);
    }
    private static void Import(XElement xElement, Topic owner, string path) {
      if(xElement == null || ((xElement.Attribute("n") == null || owner == null) && path == null)) {
        return;
      }
      Topic cur;
      bool setVersion;
      if(xElement.Attribute("ver") != null && Version.TryParse(xElement.Attribute("ver").Value, out Version ver)) {
        if(owner == null ? Topic.Root.Exist(path, out cur) : owner.Exist(xElement.Attribute("n").Value, out cur)) {
          var ov_js = cur.GetField("version");
          string ov_s;
          if(ov_js.ValueType == JSValueType.String && (ov_s = ov_js.Value as string) != null && ov_s.StartsWith("¤VR") && Version.TryParse(ov_s.Substring(3), out Version oldVer) && oldVer >= ver) {
            return; // don't import older version
          }
        }
        setVersion = true;
      } else {
        ver = default;
        setVersion = false;
      }
      JSValue state = null, manifest = null;
      if(xElement.Attribute("m") != null) {
        try {
          manifest = JsLib.ParseJson(xElement.Attribute("m").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).m - {1}", xElement.ToString(), ex.Message);
        }
      }
      if(setVersion) {
        manifest = JsLib.SetField(manifest, "version", "¤VR" + ver.ToString());
      }

      if(xElement.Attribute("s") != null) {
        try {
          state = JsLib.ParseJson(xElement.Attribute("s").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).s - {1}", xElement.ToString(), ex.Message);
        }
      }


      if(owner == null) {
        cur = Topic.I.Get(Topic.Root, path, true, null, false, false);
      } else {
        cur = Topic.I.Get(owner, xElement.Attribute("n").Value, true, null, false, false);
      }
      Topic.I.Fill(cur, state, manifest, null);
      foreach(var xNext in xElement.Elements("i")) {
        Import(xNext, cur, null);
      }
    }

    public static void Export(string filename, Topic t, bool configOnly) {
      if(filename == null || t == null) {
        throw new ArgumentNullException();
      }
      XDocument doc = new XDocument(new XElement("xst", new XAttribute("path", t.Path))){ Declaration = new XDeclaration("1.0", "utf-8", "yes") };
      var s = t.GetState();
      if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
        doc.Root.Add(new XAttribute("s", JsLib.Stringify(s)));
      }
      var m = t.GetField(null);
      doc.Root.Add(new XAttribute("m", JsLib.Stringify(m)));
      foreach(Topic c in t.Children) {
        Export(doc.Root, c, configOnly);
      }
      using(System.Xml.XmlTextWriter writer = new System.Xml.XmlTextWriter(filename, Encoding.UTF8)) {
        writer.Formatting = System.Xml.Formatting.Indented;
        writer.QuoteChar = '\'';
        doc.WriteTo(writer);
        writer.Flush();
      }
    }
    private static void Export(XElement x, Topic t, bool configOnly) {
      if(x == null || t == null) {
        return;
      }
      XElement xCur = new XElement("i", new XAttribute("n", t.Name));
      foreach(Topic c in t.Children) {
        Export(xCur, c, configOnly);
      }
      if(!configOnly || xCur.HasElements || t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
        var s = t.GetState();
        if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
          xCur.Add(new XAttribute("s", JsLib.Stringify(s)));
        }
        var m = t.GetField(null);
        xCur.Add(new XAttribute("m", JsLib.Stringify(m)));

        x.Add(xCur);
      }
    }
    #endregion Import/Export

    #region IPlugModul Members

    public void Init() {
      var dir = Path.GetDirectoryName(cfg);
      if(!Directory.Exists(dir)) {
        Directory.CreateDirectory(dir);
      }

      Topic.I.Init(this);
      _busyFlag = 1;
      if(File.Exists(cfg)) {
        Import(cfg);
      }
      this.Tick();
    }

    public void Start() {
      Repo.Import("../data/base.xst");
    }

    public void Tick() {
      if(Interlocked.CompareExchange(ref _busyFlag, 2, 1) != 1) {
        return;
      }
      //int QC = 0;

      Perform cmd;
      _pfPos = 0;

      // Step1
      while(_tcQueue.TryDequeue(out cmd)) {
        if(cmd == null || cmd.src == null) {
          continue;
        }
        //QC++;
        TickStep1(cmd);
      }

      // Step2
      for(int i = 0; i < _prOp.Count; i++) {
        TickStep2(_prOp[i]);
      }
      // Check constructors
      for(int i = 0; i < _prOp.Count; i++) {
        CheckCCtor(_prOp[i]);
      }

      // Publish
      for(_pfPos = 0; _pfPos < _prOp.Count; _pfPos++) {
        cmd = _prOp[_pfPos];
        if(cmd.Art != Perform.ArtEnum.setState && cmd.Art != Perform.ArtEnum.setField) {
          Topic.I.Publish(cmd);
          for(int i = _subscribers.Count-1; i>=0; i--) {
            var func = _subscribers[i];
            try {
              func.Invoke(cmd);
            }
            catch(Exception ex) {
              Log.Error("{0}.{1}({2}) - {3}", func.Target!=null?func.Target.ToString():func.Method.DeclaringType.Name, func.Method.Name, cmd.ToString(), ex.Message);
            }
          }
        }
      }

      //int PC = _prOp.Count, DB = _db_q.Count;
      _prOp.Clear();

      //if(QC!=0 || PC!=0 || DB!=0) X13.Log.Debug("PLC.Tick QC="+QC.ToString()+", PC="+PC.ToString()+", DB="+ DB.ToString());
      _busyFlag = 1;
    }

    public void Stop() {
      Export("../data/server.xst", Topic.Root, true);
    }

    public bool enabled { get { return true; } set { if(!value) throw new ApplicationException("Repository disabled"); } }
    #endregion IPlugModul Members
  }
}
