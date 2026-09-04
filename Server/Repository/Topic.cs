///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Repository {
  public sealed class Topic : IComparable<Topic> {
    private object _sync;

    /// <summary>Serialises structural change: creating a node, unlinking one, moving one.</summary>
    /// <remarks>One lock for the whole tree rather than one per parent. A move touches two parents
    /// and would need both, and two locks need a total order over topics that does not shift under
    /// the operation - the path does shift, and RuntimeHelpers.GetHashCode is stable but not
    /// unique, so two threads moving in opposite directions between two topics whose hashes
    /// collided would take them in opposite orders and deadlock. An id on every topic would settle
    /// it; one lock costs less and cannot be got wrong. Structural change is rare beside reading
    /// and writing state, and the read paths do not take this at all.
    /// <para>Readers are not covered. A traversal running beside a move can still meet a mix of
    /// old and new paths - a separate problem, with a price of its own.</para></remarks>
    private static readonly object _structural = new object();
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
    /// <summary>This node's children, in ordinal name order. Immutable - replaced, never mutated.</summary>
    /// <remarks>A ConcurrentDictionary per node was the wrong shape twice over. It cost a fixed
    /// amount per INSTANCE - a lock array sized by the processor count, plus a bucket table - which
    /// measured at about 340 bytes for every node that has children, and in a bushy tree that is
    /// most of them. And it holds no order, so every traversal of every node had to snapshot and
    /// sort it, which is where the OrderBy buffering defect came from.
    /// <para>An array kept in order answers both: no per-instance overhead, ordered traversal for
    /// free, and the snapshot a reader needs is the field it already read. Writers replace it whole
    /// under the structural lock; readers take the reference and never see a half-built one.</para>
    /// <para>The key is kept beside the topic rather than read from Topic._name, which is mutable:
    /// a rename changes it in place, and a reader halving an older snapshot would then take the
    /// wrong branch and fail to find a SIBLING that never moved. With the key snapshotted the
    /// worst case is the same as the dictionary's - the renamed one may be missed while the rename
    /// is in flight, everything else is always found.</para></remarks>
    private volatile KeyValuePair<string, Topic>[] _children = NoChildren;
    private static readonly KeyValuePair<string, Topic>[] NoChildren = new KeyValuePair<string, Topic>[0];
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
    private FieldBatch _mfst_pu;

    /// <summary>The manifest a tick is building for one topic, shared by every write into it.</summary>
    /// <remarks>Shared so the topic gets one new manifest rather than a partial one per write, and
    /// so every event of the batch reports the same manifest from before it. Which of the writes
    /// performs the swap does not matter - the first one applied does it, the rest read the result
    /// out of here.</remarks>
    internal sealed class FieldBatch {
      public JSC.JSValue value;         // the manifest being built
      public JSC.JSValue oldManifest;   // what it was before the batch, filled in by the swap
      public bool swapped;
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
    /// <summary>Whether a string can be a topic's own name.</summary>
    /// <remarks>Public because a caller holding a name from outside - a client adding or renaming
    /// a topic - needs to ASK rather than to catch: what it has is a refusal to report back, not
    /// an exception to handle.
    /// <para>Four copies of this rule used to disagree with each other. Resolve refused wildcards,
    /// Move checked nothing at all, the .xst import grew its own, and WebUI's had a fourth set: it
    /// refused '/' and '#' and let '+' through - and Resolve throws on '+', so a client could turn
    /// "add a topic called +" into an unhandled exception. A separator inside a name is the other
    /// half: Move would have taken it verbatim as a dictionary key, leaving a topic that no path
    /// lookup could ever reach.</para>
    /// <para>This is now the only statement of the rule: Resolve and CheckPath call CheckName per
    /// segment instead of keeping their own shorter version. The last thing the short versions
    /// still let through was a blank segment, which is how a topic named " " could be created and
    /// then never renamed to - the two ends of one operation disagreeing.</para></remarks>
    public static bool IsValidName(string name) {
      return !string.IsNullOrWhiteSpace(name)
        && name.IndexOf(Bill.delmiter) < 0
        && name != Bill.maskAll
        && name != Bill.maskChildren;
    }

    /// <summary>Throws unless the string can be a topic's own name.</summary>
    internal static void CheckName(string name, string context) {
      if (!IsValidName(name)) {
        throw new ArgumentException(context + " - not a topic name: \"" + (name ?? "<null>") + "\"");
      }
    }

    /// <summary>Throws unless every segment of a path could be a topic name.</summary>
    /// <remarks>A path is not a name: it may be "/" and it may have several segments, so it is
    /// checked segment by segment and an empty one is simply skipped, the way Resolve skips it.
    /// <para>Each surviving segment is held to the whole rule and not to part of it. This checked
    /// the two wildcards alone, so the .xst import accepted a blank segment in the path it was
    /// addressed at while rejecting the same blank as a child name two lines later - one half of
    /// one operation disagreeing with the other.</para></remarks>
    internal static void CheckPath(string path, string context) {
      string[] segments = (path ?? string.Empty).Split(Bill.delmiterArr, StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < segments.Length; i++) {
        CheckName(segments[i], context);
      }
    }

    public bool HasChildren() {
      KeyValuePair<string, Topic>[] kids = _children;
      for (int i = 0; i < kids.Length; i++) {
        if (!kids[i].Value.disposed) {
          return true;
        }
      }
      return false;
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
    /// <summary>Finds a topic by path, creating what is missing unless told not to.</summary>
    /// <remarks>The rule the whole structural API follows, written down here because Get is the
    /// door most callers come through:
    /// <list type="bullet">
    /// <item>A QUESTION answers, it does not throw: Get(create: false) and Exist return null or
    /// false for "no such topic", GetField returns Undefined, and Try- methods return a bool.
    /// Absence is an answer, not a fault.</item>
    /// <item>A COMMAND refuses a bad argument with ArgumentException, naming the offending value:
    /// Get(create: true), Declare, Move, SetField, Import. ArgumentNullException stays for what it
    /// means in the rest of .NET - a reference argument that is null.</item>
    /// <item>A COMMAND that cannot be carried out in the state the tree is in throws
    /// InvalidOperationException: moving the root, moving a removed topic.</item>
    /// <item>A command NEVER quietly does nothing. Silence is indistinguishable from success, so
    /// a caller that was ignored carries on believing the tree changed. Move had two such returns
    /// and both are throws now.</item>
    /// </list>
    /// A caller holding a name from outside asks first - IsValidName - rather than catching; the
    /// WebUI dispatcher does exactly that, and turns the answer into an error for its client.</remarks>
    public Topic Get(string path, bool create = true, Topic prim = null) {
      return Resolve(this, path, create, prim, true);
    }
    public bool Exist(string path) {
      return Resolve(this, path, false, null, false) != null;
    }
    public bool Exist(string path, out Topic topic) {
      return (topic = Resolve(this, path, false, null, false)) != null;
    }
    /// <summary>Moves the topic under another parent, or renames it, or both.</summary>
    /// <remarks>The whole structural change happens under one lock, and in an order that leaves the
    /// topic in one place or in none - never in two. It used to add the topic to its new parent
    /// first and remove it from the old one after, so for a moment it was reachable from both
    /// branches while parent, name and path still described the old position. The collection it
    /// used kept each of those operations safe on its own; none of that made the transaction one.
    /// <para>Two array replacements now, both inside the lock: the topic leaves its old parent and
    /// then joins the new one.</para></remarks>
    public void Move(Topic nParent, string nName, Topic prim = null) {
      // Refused rather than ignored, and the same for the removed topic below. A command that
      // quietly does nothing is the worst of the three answers a caller can get: it reads exactly
      // like success, so the caller carries on believing the tree changed.
      if (this._parent == null) {
        throw new InvalidOperationException(this._path + ".Move - the root cannot be moved");
      }
      // Reachable, not defensive. Remove marks the topic and leaves the unlink to the tick, so
      // before that tick the topic is still registered under its parent and Move used to carry the
      // move out on something already on its way out; after it, Move fell through to the branch
      // below and returned with a warning nobody reads.
      if (this.disposed) {
        throw new InvalidOperationException(this._path + ".Move - the topic has been removed");
      }
      if (nParent == null) {
        nParent = this.parent;
      }
      if (string.IsNullOrEmpty(nName)) {
        nName = this.name;
      }
      CheckName(nName, this._path + ".Move");
      string oldPath;
      lock (_structural) {
        // A topic may not become its own descendant. The tree would hold a cycle, and UpdatePath
        // below walks children keeping no record of where it has been, so the next call would
        // recurse until the stack ran out - StackOverflowException, which no catch can intercept
        // and which takes the process down with it. Checked before anything is touched.
        for (Topic p = nParent; p != null; p = p._parent) {
          if (p == this) {
            throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") - a topic cannot be moved inside itself");
          }
        }
        KeyValuePair<string, Topic>[] target = nParent._children;
        int to = IndexOf(target, nName);
        if (to >= 0 && target[to].Value != this) {
          throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") - the name is taken");
        }
        Topic oldParent = this._parent;
        string oldName = this._name;
        oldPath = this._path;
        KeyValuePair<string, Topic>[] source = oldParent._children;
        int from = IndexOf(source, oldName);
        if (from < 0 || source[from].Value != this) {
          // Unreachable now that a removed topic is turned away above - that was the one way in.
          // Kept as a throw and not a warning for the same reason as the restore below: a
          // structural call that cannot do what it was asked has to say so.
          throw new InvalidOperationException(this._path + ".Move(" + nParent._path + ", " + nName + ") - not registered under its own parent");
        }
        oldParent._children = RemovedAt(source, from);
        _parent = nParent;
        _name = nName;
        UpdatePath(this);
        // Read the target again: the snapshot above was taken before the removal, and for a rename
        // inside one parent it is the very array that removal replaced.
        target = nParent._children;
        to = IndexOf(target, nName);
        if (to >= 0) {
          // Unreachable while every structural change holds this lock, and undone rather than
          // asserted because the alternative is a topic in no branch at all.
          _parent = oldParent;
          _name = oldName;
          UpdatePath(this);
          source = oldParent._children;
          oldParent._children = Inserted(source, ~IndexOf(source, oldName), oldName, this);
          throw new InvalidOperationException(oldPath + ".Move(" + nParent._path + ", " + nName + ") - the name was taken under the lock");
        }
        nParent._children = Inserted(target, ~to, nName, this);
      }
      _repo.DoCmd(new CmdMove(this, oldPath, prim));
    }
    /// <summary>Marks the topic removed now; the unlink and the event come with the tick.</summary>
    /// <remarks>disposed is set here rather than in CmdRemove.Apply on purpose, and it follows the
    /// rule the rest of the structure follows: creating and moving take effect on the caller's
    /// thread too, and only the events wait. Bill and HasChildren read the flag, so a removed
    /// subtree stops being enumerated the moment it is removed rather than a tick later.
    /// <para>Deferring the flag, to make "a command applies next tick and not earlier" true of
    /// structure as well, was considered and refused: Get would then answer with a topic that
    /// children does not list, and that sentence was never true of structure to begin with.</para>
    /// </remarks>
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
      // Split, because one exception answered for two different faults: an empty string is not a
      // null reference, and ArgumentNullException said it was.
      if (fPath == null) {
        throw new ArgumentNullException("fPath");
      }
      if (!TrySetField(fPath, value, prim)) {
        throw new ArgumentException(this._path + ".SetField - the field path is empty");
      }
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

      public Bill(Topic home, bool deep) {
        _home = home;
        _deep = deep;
      }

      /// <summary>Walks a node's children, or its whole subtree, in ordinal name order.</summary>
      /// <remarks>Nothing is sorted and nothing is copied: a node's children are already an
      /// immutable array in that order, so the walk reads the field once and iterates the snapshot
      /// it got. That is what removed the "sorted" parameter this class used to carry - it existed
      /// to let the remove cascade and the subscribe fan-out skip a per-node sort that no longer
      /// happens.
      /// <para>The deep walk pushes children onto the stack from the last, so popping hands them
      /// back ascending. A parent always comes before its children.</para></remarks>
      public IEnumerator<Topic> GetEnumerator() {
        if (!_deep) {
          KeyValuePair<string, Topic>[] kids = _home._children;
          for (int i = 0; i < kids.Length; i++) {
            if (!kids[i].Value.disposed) {  // Remove() marks disposed at once, the unlink happens a tick later
              yield return kids[i].Value;
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
            KeyValuePair<string, Topic>[] kids = cur._children;
            for (int i = kids.Length - 1; i >= 0; i--) {
              if (!kids[i].Value.disposed) {  // a separately removed child cascades from its own command
                hist.Push(kids[i].Value);
              }
            }
          } while (hist.Count > 0);
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
        // The shared rule, not a copy of it. What stood here checked the two wildcards and nothing
        // else, so a blank segment - RemoveEmptyEntries drops empty ones, not blank ones - became a
        // topic that Move would then refuse to rename anything to.
        CheckName(pt[i], home._path + "[" + path + "]");
        //if(pt[i] == Bill.maskParent) {
        //  home = home.parent;
        //  if(home == null) {
        //    throw new ArgumentException(string.Format("{0}[{1}] BAD path: excessive nesting", home._path, path));
        //  }
        //  continue;
        //}
        next = null;
        KeyValuePair<string, Topic>[] kids = home._children;
        int at = IndexOf(kids, pt[i]);
        if (at >= 0 && !kids[at].Value.disposed) {
          next = kids[at].Value;
        }
        if (next == null) {
          if (!create) {
            return null;
          }
          // Under the structural lock, so that Move can trust the name it found free: an add
          // slipped in between its check and its own add would leave the moved topic nowhere.
          // Read the field again inside it - the search above ran outside, and its answer is only
          // a hint by the time the lock is held.
          lock (_structural) {
            kids = home._children;
            at = IndexOf(kids, pt[i]);
            if (at >= 0 && !kids[at].Value.disposed) {
              next = kids[at].Value;   // another thread got here first; take what it published
            } else {
              // A disposed entry is replaced, not returned. Remove() marks the topic and leaves
              // the unlink to the tick, and whoever asks for the path in between wants a topic
              // they can use - not the one on its way out. Unlink removes by pair, so the removal
              // still pending for the old one cannot take this replacement with it.
              next = new Topic(home, pt[i], fill);
              home._children = at >= 0 ? Replaced(kids, at, pt[i], next) : Inserted(kids, ~at, pt[i], next);
              if (fill) {  // else the create command is added in Fill()
                _repo.DoCmd(new CmdCreate(next, prim));
              }
            }
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
    /// <returns>The batch shared by every write to this topic in this tick. The caller keeps it:
    /// whichever write is applied first swaps the built manifest in, and all of them read the
    /// manifest from before the batch out of it.</returns>
    /// <remarks>Merging is what keeps the manifest consistent - subscribers see one new manifest
    /// rather than a partial one per write. It used to fold the WRITES into one event too, which
    /// carried the path of whichever came first; a consumer matching FieldPath against a name it
    /// knows then lost its own field whenever somebody else wrote first, and both consumers in the
    /// tree match that way. One event per path now, and the merge stays.</remarks>
    internal static FieldBatch SetField(CmdField cmd) {
      Topic t = cmd.Target;
      if (t._mfst_pu == null) {
        t._mfst_pu = new FieldBatch { value = t._manifest ?? JSValue.Null };
      }
      t._mfst_pu.value = JsLib.SetField(t._mfst_pu.value, cmd.Path, cmd.Value);
      return t._mfst_pu;
    }

    /// <summary>Swaps in the manifest this tick built. Once per topic, on the first write applied.</summary>
    /// <remarks>Clearing _mfst_pu here rather than at the end of the tick is safe: it is read only
    /// while commands are being taken off the queue, which is over before anything is applied, and
    /// every command of the batch already holds the batch itself.</remarks>
    internal static void SetField2(Topic t, FieldBatch batch) {
      if (batch.swapped) {
        return;
      }
      batch.swapped = true;
      batch.oldManifest = System.Threading.Interlocked.Exchange(ref t._manifest, batch.value);
      t._mfst_pu = null;
    }

    /// <summary>Where a name sits among a node's children: the index, or ~(insertion point).</summary>
    /// <remarks>Array.BinarySearch's convention without its allocation - searching that would mean
    /// building a KeyValuePair to search for. Ordinal, the order the array is kept in.
    /// <para>Pure, so a reader may call it on its own snapshot with no lock at all, and a writer
    /// calls it on the array it read inside the lock.</para></remarks>
    private static int IndexOf(KeyValuePair<string, Topic>[] kids, string name) {
      int lo = 0, hi = kids.Length - 1;
      while (lo <= hi) {
        int mid = lo + ((hi - lo) >> 1);
        int c = string.CompareOrdinal(kids[mid].Key, name);
        if (c == 0) {
          return mid;
        }
        if (c < 0) {
          lo = mid + 1;
        } else {
          hi = mid - 1;
        }
      }
      return ~lo;
    }

    /// <summary>The three ways a node's children change. Each builds a new array and returns it.</summary>
    /// <remarks>All three are pure and touch no field, so the structural lock their callers hold is
    /// not for them - it is for the read-decide-publish sequence around them. Building the copy
    /// outside the lock and only assigning inside would lose one of two concurrent insertions.
    /// <para>Publication is a single reference assignment, which is what lets readers work without
    /// a lock: a reader sees the whole of the old array or the whole of the new one.</para></remarks>
    private static KeyValuePair<string, Topic>[] Inserted(KeyValuePair<string, Topic>[] kids, int at, string name, Topic child) {
      var next = new KeyValuePair<string, Topic>[kids.Length + 1];
      Array.Copy(kids, 0, next, 0, at);
      next[at] = new KeyValuePair<string, Topic>(name, child);
      Array.Copy(kids, at, next, at + 1, kids.Length - at);
      return next;
    }
    private static KeyValuePair<string, Topic>[] Replaced(KeyValuePair<string, Topic>[] kids, int at, string name, Topic child) {
      var next = (KeyValuePair<string, Topic>[])kids.Clone();
      next[at] = new KeyValuePair<string, Topic>(name, child);
      return next;
    }
    private static KeyValuePair<string, Topic>[] RemovedAt(KeyValuePair<string, Topic>[] kids, int at) {
      if (kids.Length == 1) {
        return NoChildren;
      }
      var next = new KeyValuePair<string, Topic>[kids.Length - 1];
      Array.Copy(kids, 0, next, 0, at);
      Array.Copy(kids, at + 1, next, at, kids.Length - at - 1);
      return next;
    }

    private static void UpdatePath(Topic t) {
      t._path = t.parent == root ? "/" + t._name : t.parent._path + "/" + t._name;
      KeyValuePair<string, Topic>[] kids = t._children;
      for (int i = 0; i < kids.Length; i++) {
        UpdatePath(kids[i].Value);
      }
    }

    /// <summary>Takes the topic out of the tree. Under the same lock as creating and moving one.</summary>
    /// <remarks>Removes the pair, not the name: between Remove() marking the topic and this
    /// running a tick later, someone may have asked for the same path and been given a fresh
    /// topic. Unlinking by name alone would take that one out instead.</remarks>
    internal static void Unlink(Topic t) {
      t.disposed = true;
      Topic parent = t._parent;
      if (parent != null) {
        lock (_structural) {
          KeyValuePair<string, Topic>[] kids = parent._children;
          int at = IndexOf(kids, t._name);
          if (at >= 0 && kids[at].Value == t) {
            parent._children = RemovedAt(kids, at);
          }
        }
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
                || !SameBranch(e.FieldPath, sb.prefix)
                || object.ReferenceEquals(e.OldManifest.Field(sb.prefix ?? string.Empty), e.Source._manifest.Field(sb.prefix ?? string.Empty)))) {
          continue;
        }
        Invoke(sb, e);
      }
    }

    /// <summary>True when a written field path and a subscription prefix lie on the same branch.</summary>
    /// <remarks>Needed once a manifest write reports its own path: without it a subscriber on one
    /// field would be called once per field written into that topic in the tick, because the
    /// manifest comparison below answers the same for every event of the batch.
    /// <para>Both directions count, and for different reasons. The path inside the prefix is the
    /// ordinary case - prefix "MQTT-SN", write "MQTT-SN.gr". The prefix inside the path is the one
    /// that is easy to miss: prefix "MQTT.uri" with a write that replaces the whole of "MQTT"
    /// changes the subscriber's field just as surely.</para>
    /// <para>Segment by segment, and not by string prefix, or "MQTT-SNx" would pass for a write
    /// inside "MQTT-SN". Split the same way GetField splits, so the two agree about what a segment
    /// is - including a trailing dot, which RemoveEmptyEntries drops.</para></remarks>
    private static bool SameBranch(string path, string prefix) {
      if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(path)) {
        return true;   // no prefix means any field
      }
      string[] a = path.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      string[] b = prefix.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      int n = a.Length < b.Length ? a.Length : b.Length;
      for (int i = 0; i < n; i++) {
        if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) {
          return false;
        }
      }
      return true;
    }

    private static void Invoke(SubRec sb, TopicEvent e) {
      try {
        sb.func(e, sb);
      }
      catch (Exception ex) {
        PluginFailed(sb.func.Method.DeclaringType.Name + "." + sb.func.Method.Name, e, ex);
      }
    }

    /// <summary>Reports a fault in somebody else's callback through the tick's own throttle.</summary>
    /// <remarks>Here rather than a bare Log.Warning because a subscriber that throws throws on
    /// every event, and the tick runs about sixty times a second: unthrottled, one broken plugin buries the
    /// log that would name it. Repo owns the throttle; this is the way in for the delivery paths,
    /// which are static and have no repository of their own to ask.</remarks>
    internal static void PluginFailed(string who, object subject, Exception ex) {
      Repo repo = _repo;
      if (repo != null) {
        repo.PluginFailed(who, subject, ex);
      } else {
        Log.Warning("{0}({1}) - {2}", who, subject, ex);
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
