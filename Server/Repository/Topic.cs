///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using JST = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;

namespace X13.Repository {
  public sealed class Topic : IComparable<Topic> {
    private static Repo _repo;
    public static Topic root { get; private set; }

    #region Member variables
    private Topic _parent;
    private string _name;
    private string _path;
    private ConcurrentDictionary<string, Topic> _children;
    private List<SubRec> _subRecords;

    private JSValue _state;
    private JSValue _manifest;
    private Perform _mfst_pu;

    #endregion Member variables

    private Topic(Topic parent, string name, bool fill) {
      _name = name;
      _parent = parent;
      _state = JSValue.Undefined;
      disposed = false;
      if(parent == null) {
        _path = "/";
      } else if(parent == root) {
        _path = "/" + name;
      } else {
        _path = parent._path + "/" + name;
      }
      if(fill) {
        _manifest = JSObject.CreateObject();
        _manifest["attr"] = new JST.Number((int)0);
      }
    }

    public Topic parent {
      get { return _parent; }
      internal set { _parent = value; }
    }
    public string name {
      get { return _name; }
    }
    public string path { get { return _path; } }
    public bool disposed { get; private set; }
    public Bill all { get { return new Bill(this, true); } }
    public Bill children { get { return new Bill(this, false); } }

    /// <summary> Get item from tree</summary>
    /// <param name="path">relative or absolute path</param>
    /// <param name="create">true - create, false - check</param>
    /// <returns>item or null</returns>
    public Topic Get(string path, bool create = true, Topic prim = null) {
      return Topic.I.Get(this, path, create, prim, false, true);
    }
    public bool Exist(string path) {
      return Topic.I.Get(this, path, false, null, false, false) != null;
    }
    public bool Exist(string path, out Topic topic) {
      return (topic = Topic.I.Get(this, path, false, null, false, false)) != null;
    }
    public void Move(Topic nParent, string nName, Topic prim = null) {
      if(this._parent == null) {
        return;
      }
      if(nParent == null) {
        nParent = this.parent;
      }
      if(string.IsNullOrEmpty(nName)) {
        nName = this.name;
      }
      Topic tmp;
      if(nParent._children == null) {
        lock(nParent) {
          if(nParent._children == null) {
            nParent._children = new ConcurrentDictionary<string, Topic>();
          }
        }
      }
      if(!nParent._children.TryAdd(nName, this)) {
        throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") FAILED");
      }
      if(!_parent._children.TryRemove(this._name, out tmp)) {
        Log.Warning("{0}.Move({1}, {2}) remove FAILED", this._path, nParent._path, nName);
        return;
      }
      var c = Perform.Create(this, Perform.Art.move, prim);
      c.o = this._path;
      _parent = nParent;
      this._name = nName;
      I.UpdatePath(this);
      _repo.DoCmd(c, false);
    }
    public void Remove(Topic prim = null) {
      this.disposed = true;
      var c = Perform.Create(this, Perform.Art.remove, prim);
      _repo.DoCmd(c, false);
    }
    public SubRec Subscribe(SubRec.SubMask mask, Action<Perform, SubRec> func) {
      return Subscribe(mask, null, func);
    }
    public SubRec Subscribe(SubRec.SubMask mask, string prefix, Action<Perform, SubRec> func) {
      if(func == null) {
        throw new ArgumentNullException(this.path + ".Subscribe(func == NULL, " + mask.ToString() + (prefix == null ? string.Empty : ", " + prefix) + ")");
      }
      SubRec sb;
      bool exist = true;
      if(_subRecords == null) {
        lock(this) {
          if(_subRecords == null) {
            var srs = new List<SubRec>();
            _subRecords = srs;  // PVS V3054. Potentially unsafe double-checked locking.
          }
        }
      }
      lock(_subRecords) {
        sb = _subRecords.FirstOrDefault(z => z.func == func && z.setTopic == this && z.mask == mask && ((z.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || z.prefix == prefix));
        if(sb == null) {
          exist = false;
          sb = new SubRec(this, func, mask, prefix);
          _subRecords.Add(sb);
        }
      }
      if(!exist) {
        var c = Perform.Create(this, Perform.Art.subscribe, this);
        c.o = sb;
        _repo.DoCmd(c, false);
      } else {
        var c = Perform.Create(this, Perform.Art.subAck, this);
        c.o = sb;
        _repo.DoCmd(c, false);
      }
      return sb;
    }

    public JSValue GetState() {
      return _state??JSValue.Null;
    }
    public void SetState(JSValue val, Topic prim = null) {
      var c = Perform.Create(this, val, prim);
      _repo.DoCmd(c, false);
    }

    public JSValue GetField(string fPath) {
      if(_manifest == null) {
        return JSValue.Undefined;
      }
      if(string.IsNullOrEmpty(fPath)) {
        return _manifest;
      }
      var ps = fPath.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      JSValue val = _manifest;
      for(int i = 0; i < ps.Length; i++) {
        if(val.ValueType != JSValueType.Object || val.Value == null) {
          return JSValue.Undefined;
        }
        val = val.GetProperty(ps[i]);
      }
      return val;
    }
    public bool TrySetField(string fPath, JSValue value, Topic prim) {
      if(string.IsNullOrEmpty(fPath)) {
        return false;
      }
      var c = Perform.Create(this, fPath, value, prim);
      _repo.DoCmd(c, false);
      return true;
    }
    public void SetField(string fPath, JSValue value, Topic prim = null) {
      if(!TrySetField(fPath, value, prim)) {
        throw new ArgumentNullException("fPath");
      }
    }

    public bool CheckAttribute(Attribute mask, Attribute value = Attribute.None) {
      if(value == Attribute.None) {
        value = mask;
      }
      JSValue attr;
      if(_manifest == null || _manifest.ValueType != JSValueType.Object || _manifest.Value == null || !(attr = _manifest["attr"]).IsNumber) {
        return false;
      }
      return ((int)attr & (int)mask) == (int)value;
    }
    public void SetAttribute(Attribute value) {
      JSValue attr;
      if(_manifest == null || _manifest.ValueType != JSValueType.Object || _manifest.Value == null || !(attr = _manifest["attr"]).IsNumber) {
        attr = new JST.Number((int)value);
      } else {
        attr = new JST.Number((int)value | (int)attr);
      }
      var c = Perform.Create(this, "attr", attr, null);
      _repo.DoCmd(c, false);
    }
    public void ClearAttribute(Attribute value) {
      JSValue attr;
      if(_manifest == null || _manifest.ValueType != JSValueType.Object || _manifest.Value == null || !(attr = _manifest["attr"]).IsNumber) {
        attr = new JST.Number((int)value);
      } else {
        attr = new JST.Number((int)attr & ~(int)value);
      }
      var c = Perform.Create(this, "attr", attr, null);
      _repo.DoCmd(c, false);
    }

    public int CompareTo(Topic other) {
      if(other == null) {
        return 1;
      }
      return this._path.CompareTo(other._path);
    }
    public override string ToString() {
      return _path;
    }

    #region nested types
    public class Bill : IEnumerable<Topic> {
      public const char delmiter = '/';
      public const string delmiterStr = "/";
      public const string maskAll = "#";
      public const string maskChildren = "+";
      //public const string maskParent = "..";
      public static readonly char[] delmiterObj = new char[] { '.' };
      public static readonly char[] delmiterArr = new char[] { delmiter };
      public static readonly string[] curArr = new string[0];
      public static readonly string[] allArr = new string[] { maskAll };
      public static readonly string[] childrenArr = new string[] { maskChildren };

      private Topic _home;
      private bool _deep;

      public Bill(Topic home, bool deep) {
        _home = home;
        _deep = deep;
      }
      public IEnumerator<Topic> GetEnumerator() {
        if(!_deep) {
          if(_home._children != null) {
            foreach(var t in _home._children.OrderBy(z => z.Key)) {
              yield return t.Value;
            }
          }
          yield break;
        } else {
          var hist = new Stack<Topic>();
          Topic cur;
          hist.Push(_home);
          do {
            cur = hist.Pop();
            yield return cur;
            if(cur._children != null) {
              foreach(var t in cur._children.OrderByDescending(z => z.Key)) {
                hist.Push(t.Value);
              }
            }
          } while(hist.Any());
        }
      }
      //public event Action<SubRec, Perform> changed {
      //  add {
      //    _home.Subscribe(value, _deep ? SubRec.SubMask.All : SubRec.SubMask.Chldren, false);
      //  }
      //  remove {
      //    _home.Unsubscribe(value, _deep ? SubRec.SubMask.All : SubRec.SubMask.Chldren, false);
      //  }
      //}
      System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
        return GetEnumerator();
      }
    }
    internal static class I {
      public static void Init(Repo repo) {
        Topic._repo = repo;
        Topic.root = new Topic(null, "/", false);
      }

      public static void Fill(Topic t, JSValue state, JSValue manifest, Topic prim) {
        t._manifest = manifest??JSObject.CreateObject();
        if(!t._manifest["attr"].IsNumber) {
          t._manifest["attr"] = new JST.Number(0);
        }

        var c = Perform.Create(t, Perform.Art.create, prim);
        _repo.DoCmd(c, false);

        if(state != null) {
          SetValue(t, state);
        }
      }

      public static Topic Get(Topic home, string path, bool create, Topic prim, bool inter, bool fill) {
        if(string.IsNullOrEmpty(path)) {
          return home;
        }
        Topic next;
        if(path[0] == Bill.delmiter) {
          if(path.StartsWith(home._path)) {
            path = path.Substring(home._path.Length);
          } else {
            home = Topic.root;
          }
        }
        var pt = path.Split(Bill.delmiterArr, StringSplitOptions.RemoveEmptyEntries);
        for(int i = 0; i < pt.Length; i++) {
          if(pt[i] == Bill.maskAll || pt[i] == Bill.maskChildren) {
            throw new ArgumentException(string.Format("{0}[{1}] dont allow wildcard", home._path, path));
          }
          //if(pt[i] == Bill.maskParent) {
          //  home = home.parent;
          //  if(home == null) {
          //    throw new ArgumentException(string.Format("{0}[{1}] BAD path: excessive nesting", home._path, path));
          //  }
          //  continue;
          //}
          next = null;
          if(home._children == null) {
            lock(home) {
              if(home._children == null) {
                home._children = new ConcurrentDictionary<string, Topic>();
              }
            }
          } else if(home._children.TryGetValue(pt[i], out next) && next.disposed) {
            next = null;
          }
          if(next == null) {
            if(create) {
              if(home._children.TryGetValue(pt[i], out next)) {
                home = next;
              } else {
                next = new Topic(home, pt[i], fill);
                home._children[pt[i]] = next;
                if(fill) {  // else the Perform(create) will be added in Fill()
                  var c = Perform.Create(next, Perform.Art.create, prim);
                  _repo.DoCmd(c, inter);
                }
              }
            } else {
              return null;
            }
          }
          home = next;
        }
        return home;
      }
      public static void SetValue(Topic t, JSValue val) {
        t._state = val;
      }
      public static bool SetField(Perform cmd){
        Topic t = cmd.src;
        bool r;
        JSValue oc;
        if(t._mfst_pu == null) {
          r = true;
          oc = t._manifest??JSValue.Null;
          t._mfst_pu = cmd;
        } else {
          r = false;
          oc = t._mfst_pu.f_v;
          if(cmd.prim != t._mfst_pu.prim) {
            t._mfst_pu.prim = null; // inform all subscribers
          }
        }
        t._mfst_pu.f_v = JsLib.SetField(oc, cmd.o as string, cmd.f_v);
        return r;
      }
      public static void SetField2(Topic t) {
        var p = System.Threading.Interlocked.Exchange(ref t._mfst_pu, null);
        p.old_o = System.Threading.Interlocked.Exchange(ref t._manifest, p.f_v);
      }

      public static void UpdatePath(Topic t) {
        t._path = t.parent == root ? "/" + t._name : t.parent._path + "/" + t._name;
        if(t._children != null) {
          foreach(var ch in t._children) {
            UpdatePath(ch.Value);
          }
        }
      }
      public static void Remove(Topic t) {
        t.disposed = true;
        if(t._parent != null) {
          Topic tmp;
          t._parent._children.TryRemove(t._name, out tmp);
        }
      }
      public static void Publish(Perform cmd) {
        SubRec sb;
        Topic t = cmd.src;

        if((cmd.art == Perform.Art.subscribe || cmd.art == Perform.Art.subAck) && (sb = cmd.o as SubRec) != null) {
          try {
            sb.func(cmd, sb);
          }
          catch(Exception ex) {
            Log.Warning("{0}.{1}({2}) - {3}", sb.func.Method.DeclaringType.Name, sb.func.Method.Name, cmd.ToString(), ex.ToString());
          }
        } else {
          if(t._subRecords != null) {
            for(int i = t._subRecords.Count-1; i >= 0 ; i--) {
              sb = t._subRecords[i];
              if(((sb.mask & SubRec.SubMask.OnceOrAll) != SubRec.SubMask.None || ((sb.mask & SubRec.SubMask.Chldren) == SubRec.SubMask.Chldren && sb.setTopic == t.parent))
                  && (cmd.art != Perform.Art.changedState || (sb.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value)
                  && (cmd.art != Perform.Art.changedField || ((sb.mask & SubRec.SubMask.Field) == SubRec.SubMask.Field && !object.ReferenceEquals(JsLib.GetField(cmd.old_o as JSValue, sb.prefix ?? string.Empty), JsLib.GetField(t._manifest, sb.prefix ?? string.Empty))))
                  ) {
                try {
                  //Log.Debug("$ {0} <= {1}", sb.ToString(), cmd.ToString());
                  sb.func(cmd, sb);
                }
                catch(Exception ex) {
                  Log.Warning("{0}.{1}({2}) - {3}", sb.func.Method.DeclaringType.Name, sb.func.Method.Name, cmd.ToString(), ex.ToString());
                }
              }
            }
          }
        }

      }
      public static void SubscribeByCreation(Topic t_c) {
        Topic p;
        if((p = t_c.parent) != null) {
          if(p._subRecords != null) {
            lock(p._subRecords) {
              foreach(var st in p._subRecords.Where(z => z.setTopic == p && (z.mask & SubRec.SubMask.Chldren) == SubRec.SubMask.Chldren)) {
                Subscribe(t_c, st);
              }
            }
          }
          while(p != null) {
            if(p._subRecords != null) {
              lock(p._subRecords) {
                foreach(var st in p._subRecords.Where(z => (z.mask & SubRec.SubMask.All) == SubRec.SubMask.All)) {
                  Subscribe(t_c, st);
                }
              }
            }
            p = p.parent;
          }
        }
      }
      public static void SubscribeByMove(Topic t) {
        if(t._subRecords != null) {
          t._subRecords.RemoveAll(z => ((z.mask & SubRec.SubMask.Chldren) == SubRec.SubMask.Chldren && z.setTopic != t) || (z.mask & SubRec.SubMask.All) == SubRec.SubMask.All);
        }
        SubscribeByCreation(t);
        if(t._children != null) {
          foreach(var c in t._children) {
            SubscribeByMove(c.Value);
          }
        }
      }

      public static void Subscribe(Topic t, SubRec sr) {

        if(t._subRecords == null) {
          lock(t) {
            if(t._subRecords == null) {
              t._subRecords = new List<SubRec>();
            }
          }
        }
        lock(t._subRecords) {
          if(!t._subRecords.Any(z => z.func == sr.func && z.setTopic == sr.setTopic && z.mask == sr.mask && ((z.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || z.prefix == sr.prefix))) {
            t._subRecords.Add(sr);
          }
        }
      }
      public static bool Unsubscribe(Topic t, SubRec sr) {
        if(RemoveSubscripton(t, sr)) {
          var c = Perform.Create(t, Perform.Art.unsubscribe, null);
          c.o = sr;
          _repo.DoCmd(c, false);
          return true;
        }
        return false;
      }
      public static bool RemoveSubscripton(Topic t, SubRec sr) {
        if(t._subRecords == null) {
          return false;
        }
        bool fl;
        lock(t._subRecords) {
          fl = t._subRecords.Remove(sr);
        }
        return fl;
      }
    }
    [Flags]
    public enum Attribute {
      None = 0,
      Required = 1,
      Readonly = 2,
      DB = 4,
      Config = 8,
      Saved = Attribute.DB | Attribute.Config,
      Internal = 64,

    }
    #endregion nested types
  }
}
