///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Repository {
  public sealed class Topic : IComparable<Topic> {
    private object _sync;
    private static Repo _repo;
    public static Topic root { get; private set; }

    public static void Subscribe(Action<Perform> func) {
      if (_repo != null) {
        _repo.SubscribeAll(func);
      } else {
        Log.Error("Topic.Subscribe({0}.{1}) - _repo == null", func.Target != null ? func.Target.ToString() : func.Method.DeclaringType.Name, func.Method.Name);
        throw new NullReferenceException("Topic.Subscribe() - _repo == null");
      }
    }

    #region Member variables
    private Topic _parent;
    private string _name;
    private string _path;
    private volatile ConcurrentDictionary<string, Topic> _children;
    private volatile List<SubRec> _subRecords;

    private JSC.JSValue _state;
    private JSC.JSValue _manifest;
    private Perform _mfst_pu;

    #endregion Member variables

    private Topic(Topic parent, string name, bool fill) {
      _sync = new object();
      _name = name;
      _parent = parent;
      _state = JSC.JSValue.Undefined;
      disposed = false;
      if (parent == null) {
        _path = "/";
      } else if (parent == root) {
        _path = "/" + name;
      } else {
        _path = parent._path + "/" + name;
      }
      if (fill) {
        _manifest = JSC.JSObject.CreateObject();
        _manifest["attr"] = new JSL.Number((int)0);
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
    public bool HasChildren() {
      return _children != null && _children.Any(z => !z.Value.disposed);
    }
    public string GetStateType() {
      return JsValueTypeName(_state);
    }

    // Extracted from GetStateType so callers with a bare JSValue (not backed by a Topic - e.g.
    // a nested field inside another topic's state) can infer the same semantic type name.
    public static string JsValueTypeName(JSValue value) {
      if (value == null) return null;
      switch (value.ValueType) {
      case JSValueType.Object:
        if (value.Value == null) return "Null";
        // IsByteArray covers both representations: the JSValue itself and its .Value
        if (X13.ByteArray.IsByteArray(value, out _)) return "ByteArray";
        return "Object";
      case JSValueType.String: {
          string text = value.AsString(null);
          if (text != null && text.StartsWith("¤VR")) return "Version";
          return "String";
        }
      case JSValueType.Boolean: return "Boolean";
      case JSValueType.Double:
      case JSValueType.Integer: return "Double";
      case JSValueType.Date: return "Time";
      default: return null;

      }
    }

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
      if (this._parent == null) {
        return;
      }
      if (nParent == null) {
        nParent = this.parent;
      }
      if (string.IsNullOrEmpty(nName)) {
        nName = this.name;
      }
      Topic tmp;
      if (nParent._children == null) {
        lock (nParent) {
          if (nParent._children == null) {
            nParent._children = new ConcurrentDictionary<string, Topic>();
          }
        }
      }
      if (!nParent._children.TryAdd(nName, this)) {
        throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") FAILED");
      }
      if (!_parent._children.TryRemove(this._name, out tmp)) {
        // undo the add, otherwise the topic stays reachable under both parents; the
        // conditional remove makes sure a concurrent writer's entry is left alone
        ((ICollection<KeyValuePair<string, Topic>>)nParent._children).Remove(new KeyValuePair<string, Topic>(nName, this));
        Log.Warning("{0}.Move({1}, {2}) remove FAILED", this._path, nParent._path, nName);
        return;
      }
      var c = Perform.Create(this, Perform.E_Art.move, prim);
      c.o = this._path;
      _parent = nParent;
      this._name = nName;
      I.UpdatePath(this);
      _repo.DoCmd(c, false);
    }
    public void Remove(Topic prim = null) {
      this.disposed = true;
      var c = Perform.Create(this, Perform.E_Art.remove, prim);
      _repo.DoCmd(c, false);
    }
    public SubRec Subscribe(SubRec.SubMask mask, Action<Perform, SubRec> func) {
      return Subscribe(mask, null, func);
    }
    public SubRec Subscribe(SubRec.SubMask mask, string prefix, Action<Perform, SubRec> func) {
      if (func == null) {
        throw new ArgumentNullException(this.path + ".Subscribe(func == NULL, " + mask.ToString() + (prefix == null ? string.Empty : ", " + prefix) + ")");
      }
      SubRec sb;
      bool exist = true;
      if (_subRecords == null) {
        lock (this._sync) {  // must be the same lock object as Topic.I.Subscribe uses
          if (_subRecords == null) {
            var srs = new List<SubRec>();
            _subRecords = srs;  // _subRecords is volatile, so the publish is ordered
          }
        }
      }
      lock (_subRecords) {
        sb = _subRecords.FirstOrDefault(z => z.func == func && z.setTopic == this && z.mask == mask && ((z.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || z.prefix == prefix));
        if (sb == null) {
          exist = false;
          sb = new SubRec(this, func, mask, prefix);
          _subRecords.Add(sb);
        }
      }
      if (!exist) {
        var c = Perform.Create(this, Perform.E_Art.subscribe, this);
        c.o = sb;
        _repo.DoCmd(c, false);
      } else {
        var c = Perform.Create(this, Perform.E_Art.subAck, this);
        c.o = sb;
        _repo.DoCmd(c, false);
      }
      return sb;
    }

    public JSValue GetState() {
      return _state ?? JSValue.Null;
    }
    public void SetState(JSValue val, Topic prim = null) {
      _repo.DoCmd(Perform.Create(this, val, prim), false);
    }

    public JSValue GetField(string fPath) {
      if (_manifest == null) {
        return JSValue.Undefined;
      }
      if (string.IsNullOrEmpty(fPath)) {
        return _manifest;
      }
      var ps = fPath.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      JSValue val = _manifest;
      for (int i = 0; i < ps.Length; i++) {
        if (!val.IsObject()) return JSValue.Undefined;
        val = val.GetProperty(ps[i]);
      }
      return val;
    }
    public bool TrySetField(string fPath, JSValue value, Topic prim) {
      if (string.IsNullOrEmpty(fPath)) return false;
      _repo.DoCmd(Perform.Create(this, fPath, value, prim), false);
      return true;
    }
    public void SetField(string fPath, JSValue value, Topic prim = null) {
      if (!TrySetField(fPath, value, prim)) throw new ArgumentNullException("fPath");
    }

    /// <summary>Reads the manifest's "attr" field; false when the manifest holds no usable value.</summary>
    private bool TryGetAttr(out int attr) {
      JSValue a;
      if (!_manifest.IsObject() || !(a = _manifest["attr"]).IsNumber) {
        attr = 0;
        return false;
      }
      attr = (int)a;
      return true;
    }
    public bool CheckAttribute(Attribute mask, Attribute value = Attribute.None) {
      if (value == Attribute.None) {
        value = mask;
      }
      int attr;
      if (!TryGetAttr(out attr)) return false;
      return (attr & (int)mask) == (int)value;
    }
    public void SetAttribute(Attribute value) {
      int old;
      JSL.Number attr;
      if (!TryGetAttr(out old)) {
        attr = new JSL.Number((int)value);
      } else {
        // DB and Config are mutually exclusive; test the bit, not the whole value - real
        // callers pass combined flags like Required|Readonly|Config
        if ((value & Attribute.Saved) != Attribute.None) {
          old &= ~((int)Attribute.Saved);
        }
        attr = new JSL.Number((int)value | old);
      }
      var c = Perform.Create(this, "attr", attr, null);
      _repo.DoCmd(c, false);
    }
    public void ClearAttribute(Attribute value) {
      int old;
      JSL.Number attr;
      if (!TryGetAttr(out old)) {
        attr = new JSL.Number((int)value);
      } else {
        attr = new JSL.Number(old & ~(int)value);
      }
      var c = Perform.Create(this, "attr", attr, null);
      _repo.DoCmd(c, false);
    }

    public int CompareTo(Topic other) {
      if (other == null) {
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
      private bool _sorted;

      public Bill(Topic home, bool deep) : this(home, deep, true) {
      }
      /// <param name="sorted">false skips the per-node name ordering; only for callers that
      /// do not care about order, e.g. Repo's remove cascade and subscribe fan-out.</param>
      public Bill(Topic home, bool deep, bool sorted) {
        _home = home;
        _deep = deep;
        _sorted = sorted;
      }
      public IEnumerator<Topic> GetEnumerator() {
        if (!_deep) {
          if (_home._children != null) {
            foreach (var t in _sorted ? _home._children.OrderBy(z => z.Key) : (IEnumerable<KeyValuePair<string, Topic>>)_home._children) {
              if (!t.Value.disposed) {  // Remove() marks disposed at once, the unlink happens a tick later
                yield return t.Value;
              }
            }
          }
          yield break;
        } else {
          var hist = new Stack<Topic>();
          Topic cur;
          hist.Push(_home);
          do {
            cur = hist.Pop();
            // _home is yielded even when disposed: Repo's remove cascade walks src.all and
            // needs the just-removed topic itself to get its unlink Perform
            yield return cur;
            if (cur._children != null) {
              foreach (var t in _sorted ? cur._children.OrderByDescending(z => z.Key) : (IEnumerable<KeyValuePair<string, Topic>>)cur._children) {
                if (!t.Value.disposed) {  // a separately removed child cascades from its own Perform
                  hist.Push(t.Value);
                }
              }
            }
          } while (hist.Any());
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
    public static class I {
      public static void Init(Repo repo) {
        Topic._repo = repo;
        Topic.root = new Topic(null, "/", false);
        Topic.root._manifest = JSObject.CreateObject();
        Topic.root._manifest["attr"] = new JSL.Number((int)(Attribute.Required | Attribute.Internal));
      }

      public static void Fill(Topic t, JSValue state, JSValue manifest, Topic prim) {
        t._manifest = (manifest == null || manifest.IsNull) ? JSObject.CreateObject() : manifest;
        if (!t._manifest["attr"].IsNumber) {
          t._manifest = JsLib.SetField(t._manifest, "attr", new JSL.Number(0));
        }

        var c = Perform.Create(t, Perform.E_Art.create, prim);
        _repo.DoCmd(c, false);

        if (state != null) {
          SetValue(t, state);
        }
      }

      public static Topic Get(Topic home, string path, bool create, Topic prim, bool inter, bool fill) {
        if (path == Bill.delmiterStr) {
          return root;
        }
        if (string.IsNullOrEmpty(path)) {
          return home;
        }
        Topic next;
        if (path[0] == Bill.delmiter) {
          // the prefix must end on a real segment boundary, otherwise "/dev/light10/state"
          // would resolve against home "/dev/light1" and address "0/state" under it
          if (path.StartsWith(home._path)
              && (home._path.Length == 1 || path.Length == home._path.Length || path[home._path.Length] == Bill.delmiter)) {
            path = path.Substring(home._path.Length);
          } else {
            home = Topic.root;
          }
        }
        var pt = path.Split(Bill.delmiterArr, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pt.Length; i++) {
          if (pt[i] == Bill.maskAll || pt[i] == Bill.maskChildren) {
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
          if (home._children == null) {
            lock (home) {
              if (home._children == null) {
                home._children = new ConcurrentDictionary<string, Topic>();
              }
            }
          } else if (home._children.TryGetValue(pt[i], out next) && next.disposed) {
            next = null;
          }
          if (next == null) {
            if (create) {
              // TryAdd decides the race: exactly one thread publishes its instance and emits
              // the create Perform, the loser drops its candidate and takes the winner's
              var candidate = new Topic(home, pt[i], fill);
              if (home._children.TryAdd(pt[i], candidate)) {
                next = candidate;
                if (fill) {  // else the Perform(create) will be added in Fill()
                  var c = Perform.Create(next, Perform.E_Art.create, prim);
                  _repo.DoCmd(c, inter);
                }
              } else {
                home._children.TryGetValue(pt[i], out next);
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
      public static bool SetField(Perform cmd) {
        Topic t = cmd.src;
        bool r;
        JSValue oc;
        if (t._mfst_pu == null) {
          r = true;
          oc = t._manifest ?? JSValue.Null;
          t._mfst_pu = cmd;
        } else {
          r = false;
          oc = t._mfst_pu.f_v;
          if (cmd.Prim != t._mfst_pu.Prim) {
            t._mfst_pu.Prim = null; // inform all subscribers
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
        if (t._children != null) {
          foreach (var ch in t._children) {
            UpdatePath(ch.Value);
          }
        }
      }
      public static void Remove(Topic t) {
        t.disposed = true;
        if (t._parent != null) {
          Topic tmp;
          t._parent._children.TryRemove(t._name, out tmp);
        }
      }
      public static void Publish(Perform cmd) {
        SubRec sb;
        Topic t = cmd.src;

        if ((cmd.Art == Perform.E_Art.subscribe || cmd.Art == Perform.E_Art.subAck) && (sb = cmd.o as SubRec) != null) {
          try {
            sb.func(cmd, sb);
          }
          catch (Exception ex) {
            Log.Warning("{0}.{1}({2}) - {3}", sb.func.Method.DeclaringType.Name, sb.func.Method.Name, cmd.ToString(), ex.ToString());
          }
        } else {
          if (t._subRecords != null) {
            for (int i = t._subRecords.Count - 1; i >= 0; i--) {
              sb = t._subRecords[i];
              if (((sb.mask & SubRec.SubMask.OnceOrAll) != SubRec.SubMask.None || ((sb.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children && sb.setTopic == t.parent))
                  && (cmd.Art != Perform.E_Art.changedState || (sb.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value)
                  && (cmd.Art != Perform.E_Art.changedField || ((sb.mask & SubRec.SubMask.Field) == SubRec.SubMask.Field && !object.ReferenceEquals((cmd.old_o as JSValue).Field(sb.prefix ?? string.Empty), t._manifest.Field(sb.prefix ?? string.Empty))))
                  ) {
                try {
                  //Log.Debug("$ {0} <= {1}", sb.ToString(), cmd.ToString());
                  sb.func(cmd, sb);
                }
                catch (Exception ex) {
                  Log.Warning("{0}.{1}({2}) - {3}", sb.func.Method.DeclaringType.Name, sb.func.Method.Name, cmd.ToString(), ex.ToString());
                }
              }
            }
          }
        }

      }
      public static void SubscribeByCreation(Topic t_c) {
        Topic p;
        if ((p = t_c.parent) != null) {
          if (p._subRecords != null) {
            lock (p._subRecords) {
              foreach (var st in p._subRecords.Where(z => z.setTopic == p && (z.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children)) {
                Subscribe(t_c, st);
              }
            }
          }
          while (p != null) {
            if (p._subRecords != null) {
              lock (p._subRecords) {
                foreach (var st in p._subRecords.Where(z => (z.mask & SubRec.SubMask.All) == SubRec.SubMask.All)) {
                  Subscribe(t_c, st);
                }
              }
            }
            p = p.parent;
          }
        }
      }
      public static void SubscribeByMove(Topic t) {
        if (t._subRecords != null) {
          lock (t._subRecords) {  // every other mutation of _subRecords is guarded the same way
            // Only INHERITED records go, so SubscribeByCreation below can re-derive them from the
            // new ancestors - hence the setTopic != t guard, which has to apply to both masks.
            // It used to be missing on the All branch, which therefore also dropped the topic's
            // OWN deep subscription; SubscribeByCreation walks t.parent upward and never restores
            // that one, so a moved topic went permanently deaf - no move event, no create event
            // for a new child, nothing. An open Logram document froze the moment its diagram, or
            // any folder above it, was renamed.
            t._subRecords.RemoveAll(z => z.setTopic != t
              && (((z.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children) || ((z.mask & SubRec.SubMask.All) == SubRec.SubMask.All)));
          }
        }
        SubscribeByCreation(t);
        if (t._children != null) {
          foreach (var c in t._children) {
            SubscribeByMove(c.Value);
          }
        }
      }

      public static void Subscribe(Topic t, SubRec sr) {

        if (t._subRecords == null) {
          lock (t._sync) {  // the instance Subscribe lazily creates the same field under _sync
            if (t._subRecords == null) {
              t._subRecords = new List<SubRec>();
            }
          }
        }
        lock (t._subRecords) {
          if (!t._subRecords.Any(z => z.func == sr.func && z.setTopic == sr.setTopic && z.mask == sr.mask && ((z.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || z.prefix == sr.prefix))) {
            t._subRecords.Add(sr);
          }
        }
      }
      public static bool Unsubscribe(Topic t, SubRec sr) {
        if (RemoveSubscripton(t, sr)) {
          var c = Perform.Create(t, Perform.E_Art.unsubscribe, null);
          c.o = sr;
          _repo.DoCmd(c, false);
          return true;
        }
        return false;
      }
      public static bool RemoveSubscripton(Topic t, SubRec sr) {
        if (t._subRecords == null) {
          return false;
        }
        bool fl;
        lock (t._subRecords) {
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
