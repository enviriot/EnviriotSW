///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
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
    internal static string configPath;
    #region internal Members
    private ConcurrentQueue<Perform> _tcQueue;
    private List<Perform> _prOp;
    private volatile Action<Perform>[] _subscribers;
    private int _busyFlag;
    private int _pfPos;
    private DateTime? _saveConfigT;
    private bool _loaded;

    internal void DoCmd(Perform cmd, bool intern) {
      if(intern && _prOp.Count > 0 && _pfPos < _prOp.Count) {
        TickStep1(cmd);
        TickStep2(cmd);
      } else {
        _tcQueue.Enqueue(cmd);               // Process in next tick
      }
    }
    /// <summary>Registers a repository-wide callback and hands back the way to stop it.</summary>
    /// <remarks>Returned rather than void, which is what this was: a plugin could start receiving
    /// every Perform in the tree and had no way to stop receiving them. PersistentStorage paid for
    /// that concretely - its Stop() disposes the AutoResetEvent that its own callback then went on
    /// to Set() on the next repository change.
    /// <para>The list is replaced rather than mutated, so the publish loop below reads a snapshot
    /// that cannot change under it. Writes happen three times at startup and three times at
    /// shutdown; the read runs on every published Perform.</para></remarks>
    internal IDisposable SubscribeAll(Action<Perform> func) {
      if(func == null) {
        throw new ArgumentNullException("func");
      }
      var old = _subscribers;
      var next = new Action<Perform>[old.Length + 1];
      Array.Copy(old, next, old.Length);
      next[old.Length] = func;
      _subscribers = next;
      return new AllSubRec(this, func);
    }
    private void UnsubscribeAll(Action<Perform> func) {
      var old = _subscribers;
      int idx = Array.IndexOf(old, func);
      if(idx < 0) {
        return;
      }
      var next = new Action<Perform>[old.Length - 1];
      Array.Copy(old, 0, next, 0, idx);
      Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
      _subscribers = next;
    }

    /// <summary>What SubscribeAll hands back. Disposing twice is a no-op, as is disposing late.</summary>
    private sealed class AllSubRec : IDisposable {
      private Repo _owner;
      private readonly Action<Perform> _func;

      public AllSubRec(Repo owner, Action<Perform> func) {
        _owner = owner;
        _func = func;
      }
      public void Dispose() {
        Repo owner = _owner;
        _owner = null;
        if(owner != null) {
          owner.UnsubscribeAll(_func);
        }
      }
    }

    private int EnquePerf(Perform cmd) {
      int i;
      for(i = 0; i < _prOp.Count; i++) {
        if(_prOp[i].EqualsGr(cmd)) {
          if(_prOp[i].Art == Perform.E_Art.changedState) {
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
      case Perform.E_Art.create:
        Topic.I.SubscribeByCreation(c.src);
        EnquePerf(c);
        break;
      case Perform.E_Art.subscribe:
      case Perform.E_Art.unsubscribe:
        if((sr = c.o as SubRec) != null) {
          Topic.Bill b = null;
          Perform np;
          if(c.Art == Perform.E_Art.subscribe && (sr.mask & SubRec.SubMask.Once) == SubRec.SubMask.Once) {
            EnquePerf(c);
          }
          // unsorted: the fan-out subscribes every descendant, order is irrelevant here
          if((sr.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children) {
            b = new Topic.Bill(c.src, false, false);
          }
          if((sr.mask & SubRec.SubMask.All) == SubRec.SubMask.All) {
            b = new Topic.Bill(c.src, true, false);
          }
          if(b != null) {
            foreach(Topic tmp in b) {
              if(c.Art == Perform.E_Art.subscribe) {
                Topic.I.Subscribe(tmp, sr);
                if((sr.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value
                  || (sr.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || string.IsNullOrEmpty(sr.prefix) || tmp.GetField(sr.prefix).Defined) {
                  np = Perform.Create(tmp, Perform.E_Art.subscribe, c.src);
                  np.o = c.o;
                  EnquePerf(np);
                }
              } else {
                Topic.I.RemoveSubscripton(tmp, sr);
              }
            }
          }
          if(c.Art == Perform.E_Art.subscribe) {
            np = Perform.Create(c.src, Perform.E_Art.subAck, c.src);
            np.o = c.o;
            EnquePerf(np);
          }
        }
        break;
      case Perform.E_Art.setField: {
          if(Topic.I.SetField(c)) {
            c.Art = Perform.E_Art.changedField;
            EnquePerf(c);
          }
        }
        break;

      case Perform.E_Art.changedState:
      case Perform.E_Art.setState:
      case Perform.E_Art.changedField:
      case Perform.E_Art.move:
      case Perform.E_Art.subAck:
        EnquePerf(c);
        break;
      case Perform.E_Art.remove:
        foreach(Topic tmp in new Topic.Bill(c.src, true, false)) {  // unsorted: every descendant gets a remove Perform anyway
          EnquePerf(Perform.Create(tmp, Perform.E_Art.remove, c.Prim));
        }
        break;
      }
    }
    private void TickStep2(Perform cmd) {
      if(cmd.Art == Perform.E_Art.remove || (cmd.Art == Perform.E_Art.setState && !object.ReferenceEquals(cmd.src.GetState(), cmd.o))) {
        cmd.old_o = cmd.src.GetState();
        Topic.I.SetValue(cmd.src, cmd.o as JSValue);
        if(cmd.Art != Perform.E_Art.remove) {
          cmd.Art = Perform.E_Art.changedState;
        }
      }
      if(cmd.Art == Perform.E_Art.changedField) {
        Topic.I.SetField2(cmd.src);
      }
      if(cmd.Art == Perform.E_Art.move) {
        Topic.I.SubscribeByMove(cmd.src);
      }
      if(cmd.Art == Perform.E_Art.remove) {
        Topic.I.Remove(cmd.src);
      }
    }
    private void CheckCCtor(Perform p) {
      SortedList<string, JSValue> lo = null, ln = null, lc = null;
      JSValue to = null, tn = p.src.GetField("type"), vn;
      if(p.Art == Perform.E_Art.changedField) {
        JSValue o = (p.old_o as JSValue).Field("cctor"), n = p.src.GetField("cctor");
        to = (p.old_o as JSValue).Field("type");
        if(!object.ReferenceEquals(o, n)) {
          JsLib.Propertys(ref lo, o);
          JsLib.Propertys(ref ln, n);
        }
      } else if(p.Art == Perform.E_Art.create) {
        JsLib.Propertys(ref ln, p.src.GetField("cctor"));
      } else if(p.Art == Perform.E_Art.remove) {
        JsLib.Propertys(ref lo, p.src.GetField("cctor"));
      } else {
        return;
      }
      if(!object.ReferenceEquals(to, tn)) {
        // $YS/TYPES is seeded by PersistentStorage (priority 2) and does not exist yet during
        // Repo.Init(), nor at all when that plugin is disabled
        Topic types = Topic.root.Get("$YS/TYPES", false), tt;
        if(types != null) {
          if(to.Is<string>() && to.Value != null && types.Exist(to.Value as string, out tt)) {
            JsLib.Propertys(ref lo, tt.GetState().Field("cctor"));
          }
          if(tn.Is<string>() && tn.Value != null && types.Exist(tn.Value as string, out tt)) {
            JsLib.Propertys(ref ln, tt.GetState().Field("cctor"));
          }
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
        ProcessCCtor(lo, p.src, Perform.E_Art.remove);
      }
      if(ln != null) {
        ProcessCCtor(ln, p.src, Perform.E_Art.create);
      }
      if(lc != null) {
        ProcessCCtor(lc, p.src, Perform.E_Art.changedField);
      }
    }

    private void ProcessCCtor(SortedList<string, JSValue> l, Topic t, Perform.E_Art a) {
      foreach(var kv in l) {
        RPC.CCtor(kv.Key, t, a);
      }
    }

    private void PublishSaveConfig(Perform p) {
      if(p.Art==Perform.E_Art.changedField || p.Art==Perform.E_Art.changedState || p.Art==Perform.E_Art.remove) {
        if(p.src.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
          _saveConfigT = DateTime.Now.AddSeconds(5);
        }
      }
    }

    #endregion internal Members

    public Repo() {
      _tcQueue = new ConcurrentQueue<Perform>();
      _prOp = new List<Perform>(128);
      _subscribers = new Action<Perform>[0];
      _saveConfigT = null;
    }

    #region Import/Export
    public static bool Import(string fileName, string path = null) {
      if(string.IsNullOrEmpty(fileName) || !File.Exists(fileName)) {
        return false;
      }
      X13.Log.Info("Import {0}", Path.GetFullPath(fileName));
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
      Version ver;
      Topic cur = null;
      bool setVersion;
      if(xElement.Attribute("ver") != null && Version.TryParse(xElement.Attribute("ver").Value, out ver)) {
        if(owner == null ? Topic.root.Exist(path, out cur) : owner.Exist(xElement.Attribute("n").Value, out cur)) {
          Version oldVer;
          var ov_js = cur.GetField("version");
          string ov_s;
          if(ov_js.Is<string>() && (ov_s = ov_js.Value as string) != null && ov_s.StartsWith("¤VR") && Version.TryParse(ov_s.Substring(3), out oldVer) && oldVer >= ver) {
            return; // don't import older version
          }
        }
        setVersion = true;
      } else {
        ver = default(Version);
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
        cur = Topic.I.Get(Topic.root, path, true, null, false, false);
      } else {
        cur = Topic.I.Get(owner, xElement.Attribute("n").Value, true, null, false, false);
      }
      Topic.I.Fill(cur, state, manifest, null);
      foreach(var xNext in xElement.Elements("i")) {
        Import(xNext, cur, null);
      }
    }

    public static void Export(string filename, Topic t, bool configOnly) {
      if(filename == null) {
        throw new ArgumentNullException("filename");
      }
      using(FileStream stream = File.Create(filename)) {
        Export(stream, t, configOnly);
      }
    }
    public static void Export(Stream stream, Topic t, bool configOnly) {
      if(stream == null) {
        throw new ArgumentNullException("stream");
       }
      XDocument doc = BuildExportDocument(t, configOnly);
      System.Xml.XmlTextWriter writer = new System.Xml.XmlTextWriter(stream, Encoding.UTF8);
      writer.Formatting = System.Xml.Formatting.Indented;
      writer.QuoteChar = '\'';
      doc.WriteTo(writer);
      writer.Flush();
    }
    private static XDocument BuildExportDocument(Topic t, bool configOnly) {
      if(t == null) {
        throw new ArgumentNullException("topic");
      }
      XDocument doc = new XDocument(new XElement("xst", new XAttribute("path", t.path)));
      doc.Declaration = new XDeclaration("1.0", "utf-8", "yes");
      var s = t.GetState();
      if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
        doc.Root.Add(new XAttribute("s", JsLib.Stringify(s)));
      }
      var m = t.GetField(null);
      doc.Root.Add(new XAttribute("m", JsLib.Stringify(m)));
      foreach(Topic c in t.children) {
        Export(doc.Root, c, configOnly);
      }
      return doc;
    }
    private static void Export(XElement x, Topic t, bool configOnly) {
      if(x == null || t == null) {
        return;
      }
      XElement xCur = new XElement("i", new XAttribute("n", t.name));
      foreach(Topic c in t.children) {
        Export(xCur, c, configOnly);
      }
      if(!configOnly || xCur.HasElements || t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
        var s = t.GetState();
        if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
          var state_json = JsLib.Stringify(s);
          if(state_json!=null) {
            xCur.Add(new XAttribute("s", state_json));
          }
        }

        var m = t.GetField(null);
        var manifest_json = JsLib.Stringify(m);
        if(manifest_json!=null){
          xCur.Add(new XAttribute("m", manifest_json));
        }

        x.Add(xCur);
      }
    }
    #endregion Import/Export

    #region IPlugModul Members

    public void Init() {
      Topic.I.Init(this);
      _busyFlag = 1;
      if(File.Exists(configPath)) {
        Import(configPath);
      }
      this.Tick();
      this.Tick();
      _loaded = true;   // last line on purpose: see Stop
    }

    public void Start() {
      //this.Tick();
      //this.Tick();
      SubscribeAll(PublishSaveConfig);
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
        if(cmd.Art != Perform.E_Art.setState && cmd.Art != Perform.E_Art.setField) {
          Topic.I.Publish(cmd);
          // One read of the field, then iterate that: a callback may unsubscribe - its own
          // registration or another's - and the loop must not be walking the list it changed.
          var subs = _subscribers;
          for(int i = subs.Length-1; i>=0; i--) {
            var func = subs[i];
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

      if(_saveConfigT!=null && _saveConfigT<DateTime.Now) {
        _saveConfigT=null;
        Export(configPath, Topic.root, true);
      }

      //if(QC!=0 || PC!=0 || DB!=0) X13.Log.Debug("PLC.Tick QC="+QC.ToString()+", PC="+PC.ToString()+", DB="+ DB.ToString());
      _busyFlag = 1;
    }

    /// <summary>Writes the configuration back out - but never a tree that was not fully read in.</summary>
    /// <remarks>Import throws on a truncated or malformed server.xst, which is exactly what a
    /// power cut during the previous Export leaves behind. Startup then fails and the server is
    /// torn down - and this method, running as part of that teardown, would export whatever made
    /// it into the tree before the parser gave up, straight over the file that has the rest of it.
    /// A configuration one could still repair by hand becomes an empty one. Reproduced, not
    /// imagined: a deliberately truncated config came back as a 92-byte empty export.
    /// <para>Only Init sets the flag, and only as its last statement, so "loaded" means the whole
    /// of it - Topic.I.Init, the import and both ticks.</para></remarks>
    public void Stop() {
      if(!_loaded) {
        Log.Warning("Repository did not finish loading; {0} is left as it is", configPath);
        return;
      }
      Export(configPath, Topic.root, true);
    }

    /// <summary>Where the repository's own settings would live. Nothing is created until read.</summary>
    /// <remarks>Deliberately not touched by <see cref="enabled"/> below, unlike every other
    /// plugin: Topic.root does not exist until Init() runs Topic.I.Init(this), and enabled is
    /// asked first, so a topic-backed answer here would dereference null on the way up.</remarks>
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }
    private const string OWNER_PATH = "/$YS/Repository";
    private Topic _owner;

    /// <summary>Always on - the one plugin that does not read this from its Owner topic.</summary>
    /// <remarks>Every other plugin may be switched off from the tree; switching this one off would
    /// make InitPlugins skip the component that owns the tree, leaving the rest of the server with
    /// nothing to run against. A constant says that better than the ApplicationException the
    /// setter used to throw, which nothing could reach anyway.</remarks>
    public bool enabled { get { return true; } }
    #endregion IPlugModul Members
  }
}
