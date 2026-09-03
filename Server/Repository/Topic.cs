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

    /// <summary>Every event the repository publishes. Dispose the result to stop receiving.</summary>
    /// <remarks>The return value used to be void, so a plugin that subscribed here stayed
    /// subscribed for the life of the process - including after its own Stop() had disposed the
    /// objects the callback touches. Callers own what they get back.</remarks>
    public static IDisposable Subscribe(Action<TopicEvent> func) {
      if (_repo != null) {
        return _repo.SubscribeAll(func);
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
    /// <summary>Registrations made ON this topic - not the ones that reach it from above.</summary>
    /// <remarks>An array replaced whole rather than a List mutated in place: delivery reads the
    /// field once and walks that snapshot, so a callback may dispose any number of registrations,
    /// its own included, without the walk losing its place. The List it replaces was indexed
    /// backwards with Count read once, which under two disposals handed the same event to the same
    /// subscriber twice and under three threw straight out of the tick.</remarks>
    private volatile SubRec[] _subRecords = NoSubs;
    private static readonly SubRec[] NoSubs = new SubRec[0];

    private JSC.JSValue _state;
    private JSC.JSValue _manifest;
    private Pending _mfst_pu;

    /// <summary>The manifest a tick is building for one topic, before it is swapped in.</summary>
    /// <remarks>Several writes in one tick share it, so the topic sees one new manifest and one
    /// event rather than a partial manifest per write.</remarks>
    private sealed class Pending {
      public JSC.JSValue value;
      public Topic author;
      public string path;
    }

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
      return Resolve(this, path, create, prim, true);
    }
    public bool Exist(string path) {
      return Resolve(this, path, false, null, false) != null;
    }
    public bool Exist(string path, out Topic topic) {
      return (topic = Resolve(this, path, false, null, false)) != null;
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
        lock (nParent._sync) {
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
      var c = new CmdMove(this, this._path, prim);
      _parent = nParent;
      this._name = nName;
      UpdatePath(this);
      _repo.DoCmd(c);
    }
    public void Remove(Topic prim = null) {
      this.disposed = true;
      var c = new CmdRemove(this, prim);
      _repo.DoCmd(c);
    }
    public SubRec Subscribe(SubRec.SubMask mask, Action<TopicEvent, SubRec> func) {
      return Subscribe(mask, null, func);
    }
    public SubRec Subscribe(SubRec.SubMask mask, string prefix, Action<TopicEvent, SubRec> func) {
      if (func == null) {
        throw new ArgumentNullException(this.path + ".Subscribe(func == NULL, " + mask.ToString() + (prefix == null ? string.Empty : ", " + prefix) + ")");
      }
      SubRec sb;
      bool exist;
      lock (_sync) {
        sb = Find(_subRecords, func, mask, prefix);
        exist = sb != null;
        if (!exist) {
          sb = new SubRec(this, func, mask, prefix);
          SubRec[] old = _subRecords;
          SubRec[] next = new SubRec[old.Length + 1];
          Array.Copy(old, next, old.Length);
          next[old.Length] = sb;
          _subRecords = next;
        }
      }
      // subAck and not subscribe when the registration was already there: the caller is answered,
      // but the snapshot is not replayed for a subscription that never lapsed.
      Cmd c = exist ? (Cmd)new CmdAck(this, sb) : new CmdSubscribe(this, sb);
      _repo.DoCmd(c);
      return sb;
    }

    /// <summary>The registration equal to this one, or null - what Subscribe dedupes on.</summary>
    /// <remarks>No setTopic comparison: every record in a topic's own array was made on it.</remarks>
    private static SubRec Find(SubRec[] subs, Action<TopicEvent, SubRec> func, SubRec.SubMask mask, string prefix) {
      for (int i = 0; i < subs.Length; i++) {
        SubRec s = subs[i];
        if (s.func == func && s.mask == mask
            && ((mask & SubRec.SubMask.Field) == SubRec.SubMask.None || s.prefix == prefix)) {
          return s;
        }
      }
      return null;
    }

    public JSValue GetState() {
      return _state ?? JSValue.Null;
    }
    public void SetState(JSValue val, Topic prim = null) {
      _repo.DoCmd(new CmdState(this, val, prim));
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
      _repo.DoCmd(new CmdField(this, fPath, value, prim));
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
      var c = new CmdField(this, "attr", attr, null);
      _repo.DoCmd(c);
    }
    public void ClearAttribute(Attribute value) {
      int old;
      JSL.Number attr;
      if (!TryGetAttr(out old)) {
        attr = new JSL.Number((int)value);
      } else {
        attr = new JSL.Number(old & ~(int)value);
      }
      var c = new CmdField(this, "attr", attr, null);
      _repo.DoCmd(c);
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
            // needs the just-removed topic itself to get its unlink command
            yield return cur;
            if (cur._children != null) {
              foreach (var t in _sorted ? cur._children.OrderByDescending(z => z.Key) : (IEnumerable<KeyValuePair<string, Topic>>)cur._children) {
                if (!t.Value.disposed) {  // a separately removed child cascades from its own command
                  hist.Push(t.Value);
                }
              }
            }
          } while (hist.Any());
        }
      }
      System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
        return GetEnumerator();
      }
    }
    internal static void Init(Repo repo) {
      Topic._repo = repo;
      Topic.root = new Topic(null, "/", false);
      Topic.root._manifest = JSObject.CreateObject();
      Topic.root._manifest["attr"] = new JSL.Number((int)(Attribute.Required | Attribute.Internal));
    }

    /// <summary>Finds or creates a topic whose manifest is still to come.</summary>
    /// <remarks>Declare and <see cref="Fill"/> are a pair, for topics whose metadata arrives with
    /// them rather than after them - restored from storage, or read out of a .xst. Announcing the
    /// creation before the manifest is in place would show subscribers a topic without its
    /// attributes, so Declare announces nothing and Fill does it.
    /// <para>Which means a topic declared and never filled is invisible: it is in the tree,
    /// findable by path, and no event ever said it appeared. Whoever declares it owns filling
    /// it.</para></remarks>
    public static Topic Declare(Topic home, string path, Topic prim = null) {
      return Resolve(home, path, true, prim, false);
    }

    /// <summary>Gives a declared topic its manifest and state, and announces it.</summary>
    public static void Fill(Topic t, JSValue state, JSValue manifest, Topic prim) {
      t._manifest = (manifest == null || manifest.IsNull) ? JSObject.CreateObject() : manifest;
      if (!t._manifest["attr"].IsNumber) {
        t._manifest = JsLib.SetField(t._manifest, "attr", new JSL.Number(0));
      }

      var c = new CmdCreate(t, prim);
      _repo.DoCmd(c);

      if (state != null) {
        SetValue(t, state);
      }
    }

    private static Topic Resolve(Topic home, string path, bool create, Topic prim, bool fill) {
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
          lock (home._sync) {
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
            // the create command, the loser drops its candidate and takes the winner
            var candidate = new Topic(home, pt[i], fill);
            if (home._children.TryAdd(pt[i], candidate)) {
              next = candidate;
              if (fill) {  // else the create command is added in Fill()
                var c = new CmdCreate(next, prim);
                _repo.DoCmd(c);
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
    internal static void SetValue(Topic t, JSValue val) {
      t._state = val;
    }
    /// <summary>Merges one manifest write into what this tick is building for the topic.</summary>
    /// <returns>True for the first write of the batch - the one whose command is kept and whose
    /// field path the event will name. False for every later one: it has been merged in.</returns>
    /// <remarks>The author is dropped as soon as two of them disagree, so a batch that several
    /// parties contributed to is not attributed to whichever of them happened to write first -
    /// and therefore is not suppressed as an echo for any of them.</remarks>
    internal static bool SetField(CmdField cmd) {
      Topic t = cmd.Target;
      bool first = t._mfst_pu == null;
      if (first) {
        t._mfst_pu = new Pending { value = t._manifest ?? JSValue.Null, author = cmd.Author, path = cmd.Path };
      } else if (cmd.Author != t._mfst_pu.author) {
        t._mfst_pu.author = null;   // inform all subscribers
      }
      t._mfst_pu.value = JsLib.SetField(t._mfst_pu.value, cmd.Path, cmd.Value);
      return first;
    }

    /// <summary>Swaps the manifest this tick built in, and says what changed.</summary>
    internal static TopicEvent SetField2(Topic t) {
      Pending p = System.Threading.Interlocked.Exchange(ref t._mfst_pu, null);
      JSValue old = System.Threading.Interlocked.Exchange(ref t._manifest, p.value);
      return TopicEvent.FieldChanged(t, p.path, old, p.author);
    }

    private static void UpdatePath(Topic t) {
      t._path = t.parent == root ? "/" + t._name : t.parent._path + "/" + t._name;
      if (t._children != null) {
        foreach (var ch in t._children) {
          UpdatePath(ch.Value);
        }
      }
    }
    internal static void Unlink(Topic t) {
      t.disposed = true;
      if (t._parent != null) {
        Topic tmp;
        t._parent._children.TryRemove(t._name, out tmp);
      }
    }
    /// <summary>Hands one change to every registration that reaches this topic.</summary>
    /// <remarks>Registrations live on the topic they were made on, so reaching them means
    /// walking upwards: the topic's own records answer for Once and All, its parent's for
    /// Children and All, and every ancestor above that for All alone. Splitting the levels is
    /// what keeps a record whose mask carries both Children and All from being called twice.
    /// <para>This replaces copying the record into every node of the subtree, which is where
    /// SubscribeByCreation and SubscribeByMove came from - and with them the defect where a
    /// renamed topic went deaf, because the copies were dropped on the move and only some of
    /// them were derived again.</para></remarks>
    internal static void Publish(TopicEvent e) {
      if ((e.Kind == EventKind.Snapshot || e.Kind == EventKind.Ready) && e.Sub != null) {
        Invoke(e.Sub, e);   // addressed at one registration, not at whoever watches the topic
        return;
      }
      Topic t = e.Source;
      Deliver(t, e, SubRec.SubMask.OnceOrAll);
      Topic a = t.parent;
      if (a != null) {
        Deliver(a, e, SubRec.SubMask.Children | SubRec.SubMask.All);
        for (a = a.parent; a != null; a = a.parent) {
          Deliver(a, e, SubRec.SubMask.All);
        }
      }
    }

    /// <param name="scope">The masks that reach e.Source from this particular level.</param>
    private static void Deliver(Topic node, TopicEvent e, SubRec.SubMask scope) {
      // One read of the field, then walk that: a callback may dispose registrations - its own or
      // someone else's - and the walk must not be indexing the array it changed.
      SubRec[] subs = node._subRecords;
      for (int i = 0; i < subs.Length; i++) {
        SubRec sb = subs[i];
        if ((sb.mask & scope) == SubRec.SubMask.None) {
          continue;
        }
        if (e.Kind == EventKind.StateChanged && (sb.mask & SubRec.SubMask.Value) != SubRec.SubMask.Value) {
          continue;
        }
        if (e.Kind == EventKind.FieldChanged
            && ((sb.mask & SubRec.SubMask.Field) != SubRec.SubMask.Field
                || object.ReferenceEquals(e.OldManifest.Field(sb.prefix ?? string.Empty), e.Source._manifest.Field(sb.prefix ?? string.Empty)))) {
          continue;
        }
        Invoke(sb, e);
      }
    }

    private static void Invoke(SubRec sb, TopicEvent e) {
      try {
        sb.func(e, sb);
      }
      catch (Exception ex) {
        Log.Warning("{0}.{1}({2}) - {3}", sb.func.Method.DeclaringType.Name, sb.func.Method.Name, e.ToString(), ex.ToString());
      }
    }

    internal static bool Unsubscribe(Topic t, SubRec sr) {
      return RemoveSubscripton(t, sr);
    }

    /// <summary>Drops one registration from the topic it was made on.</summary>
    /// <remarks>Copy-on-write, like Subscribe: a delivery already walking the old array runs to
    /// its end against the registrations that were live when it started.</remarks>
    private static bool RemoveSubscripton(Topic t, SubRec sr) {
      lock (t._sync) {
        SubRec[] old = t._subRecords;
        int idx = Array.IndexOf(old, sr);
        if (idx < 0) {
          return false;
        }
        SubRec[] next = new SubRec[old.Length - 1];
        Array.Copy(old, 0, next, 0, idx);
        Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
        t._subRecords = next;
        return true;
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
